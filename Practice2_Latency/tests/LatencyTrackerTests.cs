using StickmanFight.Latency.Telemetry;

namespace StickmanFight.Latency.Tests;

/// <summary>
/// Тесты классификации ответов и расчёта метрик.
/// Время задаётся вручную через <see cref="ManualClock"/>, поэтому результаты детерминированы.
/// </summary>
public class LatencyTrackerTests
{
    private static LatencyTracker CreateTracker() => new(timeoutUs: 1_000_000);

    [Fact]
    public void Ответ_вовремя_даёт_RTT_и_SRTT()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        tracker.OnPingSent(1, clock.NowUs);
        clock.AdvanceMs(20);
        var outcome = tracker.OnPong(1, clock.NowUs);

        Assert.Equal(ResponseStatus.Received, outcome.Status);
        Assert.Equal(20.0, outcome.RttMs!.Value, 3);
        Assert.Equal(20.0, outcome.SrttMs!.Value, 3);   // первый замер: SRTT = RTT
        Assert.Equal(1, tracker.SampleCount);
    }

    [Fact]
    public void SRTT_сглаживается_по_формуле_задания()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        tracker.OnPingSent(1, clock.NowUs);
        clock.AdvanceMs(20);
        tracker.OnPong(1, clock.NowUs);

        tracker.OnPingSent(2, clock.NowUs);
        clock.AdvanceMs(100);
        var second = tracker.OnPong(2, clock.NowUs);

        // 0.875 * 20 + 0.125 * 100 = 30
        Assert.Equal(30.0, second.SrttMs!.Value, 3);
        Assert.Equal(30.0, tracker.SrttMs, 3);
    }

    [Fact]
    public void Джиттер_считается_как_среднее_модулей_разностей()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();
        double[] rtts = { 10, 30, 25 };                 // |30-10| + |25-30| = 25, делим на 2

        for (ushort sequence = 1; sequence <= rtts.Length; sequence++)
        {
            tracker.OnPingSent(sequence, clock.NowUs);
            clock.AdvanceMs(rtts[sequence - 1]);
            tracker.OnPong(sequence, clock.NowUs);
        }

        Assert.Equal(12.5, tracker.MeanJitterMs, 3);
    }

    [Fact]
    public void Повторный_ответ_считается_дубликатом()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        tracker.OnPingSent(7, clock.NowUs);
        clock.AdvanceMs(15);
        tracker.OnPong(7, clock.NowUs);

        var duplicate = tracker.OnPong(7, clock.NowUs);

        Assert.Equal(ResponseStatus.Duplicate, duplicate.Status);
        Assert.Null(duplicate.RttMs);
        Assert.Equal(1, tracker.SampleCount);           // дубликат не влияет на статистику
    }

    [Fact]
    public void Ответ_на_неотправленный_пакет_считается_неизвестным()
    {
        var tracker = CreateTracker();

        var outcome = tracker.OnPong(555, 1_000);

        Assert.Equal(ResponseStatus.Unknown, outcome.Status);
        Assert.Equal(0, tracker.SampleCount);
    }

    [Fact]
    public void Ответ_позже_таймаута_считается_опоздавшим_и_не_меняет_SRTT()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        tracker.OnPingSent(1, clock.NowUs);
        clock.AdvanceMs(10);
        tracker.OnPong(1, clock.NowUs);                 // обычный замер, SRTT = 10

        tracker.OnPingSent(2, clock.NowUs);
        clock.AdvanceMs(1500);                          // больше таймаута в 1000 мс
        var late = tracker.OnPong(2, clock.NowUs);

        Assert.Equal(ResponseStatus.Late, late.Status);
        Assert.Equal(1500.0, late.RttMs!.Value, 3);
        Assert.Equal(10.0, tracker.SrttMs, 3);
        Assert.Equal(1, tracker.SampleCount);
    }

    [Fact]
    public void Просроченные_пакеты_закрываются_как_таймауты()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        tracker.OnPingSent(1, clock.NowUs);
        tracker.OnPingSent(2, clock.NowUs);
        clock.AdvanceMs(1200);
        tracker.OnPingSent(3, clock.NowUs);             // этот ещё ждёт ответа

        var expired = tracker.ExpireTimeouts(clock.NowUs);

        Assert.Equal(new ushort[] { 1, 2 }, expired.OrderBy(sequence => sequence));
        Assert.Equal(2, tracker.TimeoutCount);
        Assert.True(tracker.HasPending);
    }

    [Fact]
    public void Повторный_вызов_ExpireTimeouts_не_удваивает_потери()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        tracker.OnPingSent(1, clock.NowUs);
        clock.AdvanceMs(1200);

        tracker.ExpireTimeouts(clock.NowUs);
        tracker.ExpireTimeouts(clock.NowUs);

        Assert.Equal(1, tracker.TimeoutCount);
    }

    [Fact]
    public void Доля_потерь_считается_от_числа_отправленных()
    {
        var clock = new ManualClock();
        var tracker = CreateTracker();

        for (ushort sequence = 1; sequence <= 4; sequence++)
            tracker.OnPingSent(sequence, clock.NowUs);

        clock.AdvanceMs(10);
        tracker.OnPong(1, clock.NowUs);
        tracker.OnPong(2, clock.NowUs);
        tracker.OnPong(3, clock.NowUs);

        clock.AdvanceMs(1200);
        tracker.ExpireTimeouts(clock.NowUs);            // остался только пакет 4

        Assert.Equal(4, tracker.SentCount);
        Assert.Equal(1, tracker.TimeoutCount);
        Assert.Equal(25.0, tracker.LossRatePercent, 3);
    }

    [Fact]
    public void Нулевой_таймаут_запрещён()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyTracker(timeoutUs: 0));
    }
}
