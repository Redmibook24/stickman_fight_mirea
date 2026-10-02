using StickmanFight.Reliable.Reliability;

namespace StickmanFight.Reliable.Tests;

/// <summary>Тесты адаптивного RTO: формулы, ограничения и реакция на изменение сети.</summary>
public class AdaptiveTimeoutTests
{
    [Fact]
    public void До_первого_замера_RTO_равен_начальному()
    {
        var timeout = new AdaptiveTimeout();

        Assert.False(timeout.IsInitialized);
        Assert.Equal(1000.0, timeout.RtoMs, 3);
    }

    [Fact]
    public void Первый_замер_задаёт_SRTT_и_половину_RTTVAR()
    {
        var timeout = new AdaptiveTimeout();

        timeout.OnSample(100);

        Assert.Equal(100.0, timeout.SrttMs, 3);
        Assert.Equal(50.0, timeout.RttVarMs, 3);
        Assert.Equal(300.0, timeout.RtoMs, 3);       // 100 + 4 * 50
    }

    [Fact]
    public void Второй_замер_считается_по_формуле_Джекобсона()
    {
        var timeout = new AdaptiveTimeout();

        timeout.OnSample(100);
        timeout.OnSample(200);

        // RTTVAR = 0.75 * 50 + 0.25 * |100 - 200| = 62.5 (от прежнего SRTT)
        // SRTT   = 0.875 * 100 + 0.125 * 200      = 112.5
        Assert.Equal(62.5, timeout.RttVarMs, 3);
        Assert.Equal(112.5, timeout.SrttMs, 3);
        Assert.Equal(362.5, timeout.RtoMs, 3);
    }

    [Fact]
    public void RTO_не_опускается_ниже_нижней_границы()
    {
        var timeout = new AdaptiveTimeout();

        timeout.OnSample(1);                          // сырое значение 3 мс

        Assert.Equal(100.0, timeout.RtoMs, 3);
    }

    [Fact]
    public void RTO_не_поднимается_выше_верхней_границы()
    {
        var timeout = new AdaptiveTimeout();

        timeout.OnSample(2000);                       // сырое значение 6000 мс

        Assert.Equal(3000.0, timeout.RtoMs, 3);
    }

    [Fact]
    public void На_стабильной_сети_RTO_сходится_к_SRTT()
    {
        var timeout = new AdaptiveTimeout();

        for (int i = 0; i < 60; i++) timeout.OnSample(200);

        Assert.Equal(200.0, timeout.SrttMs, 3);
        Assert.True(timeout.RttVarMs < 0.1);
        Assert.InRange(timeout.RtoMs, 200.0, 200.5);
    }

    [Fact]
    public void Всплеск_задержки_сразу_поднимает_RTO()
    {
        var timeout = new AdaptiveTimeout();
        for (int i = 0; i < 30; i++) timeout.OnSample(100);
        double before = timeout.RtoMs;

        timeout.OnSample(400);

        // Одиночный всплеск почти не двигает SRTT, но заметно увеличивает RTTVAR.
        Assert.True(timeout.RtoMs > before + 250, $"RTO {timeout.RtoMs} должен вырасти больше чем на 250 мс от {before}");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void Некорректный_замер_отвергается(double sample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveTimeout().OnSample(sample));
    }

    [Fact]
    public void RTO_в_микросекундах_согласован_с_миллисекундами()
    {
        var timeout = new AdaptiveTimeout();
        timeout.OnSample(100);

        Assert.Equal(300_000UL, timeout.RtoUs);
    }
}
