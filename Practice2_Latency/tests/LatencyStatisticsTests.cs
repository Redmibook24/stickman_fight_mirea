using StickmanFight.Latency.Telemetry;

namespace StickmanFight.Latency.Tests;

/// <summary>Тесты расчёта сводных метрик по журналу замеров.</summary>
public class LatencyStatisticsTests
{
    private static LatencySample Received(int sample, double rtt, double srtt) =>
        new("test", sample, (ushort)sample, sample * 300.0, rtt, srtt, ResponseStatus.Received);

    [Fact]
    public void Медиана_нечётного_числа_значений_это_центральный_элемент()
    {
        Assert.Equal(30.0, LatencyStatistics.Median(new[] { 50.0, 10.0, 30.0 }), 3);
    }

    [Fact]
    public void Медиана_чётного_числа_значений_это_среднее_двух_центральных()
    {
        Assert.Equal(20.0, LatencyStatistics.Median(new[] { 10.0, 30.0, 40.0, 10.0 }), 3);
    }

    [Fact]
    public void Медиана_пустого_набора_равна_нулю()
    {
        Assert.Equal(0.0, LatencyStatistics.Median(Array.Empty<double>()), 3);
    }

    [Fact]
    public void SRTT_повторяет_формулу_из_задания()
    {
        // 20 -> 0.875*20 + 0.125*100 = 30 -> 0.875*30 + 0.125*30 = 30
        Assert.Equal(30.0, LatencyStatistics.SmoothedRtt(new[] { 20.0, 100.0, 30.0 }), 3);
    }

    [Fact]
    public void Джиттер_одного_замера_равен_нулю()
    {
        Assert.Equal(0.0, LatencyStatistics.MeanJitter(new[] { 42.0 }), 3);
    }

    [Fact]
    public void Джиттер_считается_по_соседним_замерам()
    {
        Assert.Equal(12.5, LatencyStatistics.MeanJitter(new[] { 10.0, 30.0, 25.0 }), 3);
    }

    [Fact]
    public void Сводка_считает_потери_и_разделяет_статусы()
    {
        var samples = new List<LatencySample>
        {
            Received(1, 10.0, 10.0),
            Received(2, 30.0, 12.5),
            new("test", 3, 3, 900.0, null, null, ResponseStatus.Timeout),
            new("test", 4, 4, 1200.0, 1500.0, null, ResponseStatus.Late),
            new("test", 2, 2, 300.0, null, null, ResponseStatus.Duplicate),
            new("test", 0, 777, 0.0, null, null, ResponseStatus.Unknown),
        };

        var stats = LatencyStatistics.Compute("test", samples);

        Assert.Equal(4, stats.Sent);                 // 2 received + 1 timeout + 1 late
        Assert.Equal(2, stats.Received);
        Assert.Equal(1, stats.Timeouts);
        Assert.Equal(1, stats.LateResponses);
        Assert.Equal(1, stats.Duplicates);
        Assert.Equal(1, stats.UnknownResponses);
        Assert.Equal(10.0, stats.MinRttMs, 3);
        Assert.Equal(30.0, stats.MaxRttMs, 3);
        Assert.Equal(20.0, stats.MeanRttMs, 3);
        Assert.Equal(20.0, stats.MedianRttMs, 3);
        Assert.Equal(25.0, stats.LossRatePercent, 3);
    }

    [Fact]
    public void Сводка_пустого_журнала_не_падает()
    {
        var stats = LatencyStatistics.Compute("empty", Array.Empty<LatencySample>());

        Assert.Equal(0, stats.Sent);
        Assert.Equal(0.0, stats.LossRatePercent, 3);
        Assert.Equal(0.0, stats.MeanRttMs, 3);
    }

    [Fact]
    public void Опоздавшие_ответы_не_попадают_в_RTT()
    {
        var samples = new List<LatencySample>
        {
            Received(1, 10.0, 10.0),
            new("test", 2, 2, 300.0, 5000.0, null, ResponseStatus.Late),
        };

        var stats = LatencyStatistics.Compute("test", samples);

        Assert.Equal(10.0, stats.MaxRttMs, 3);
    }
}
