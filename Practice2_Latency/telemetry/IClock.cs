using System.Diagnostics;

namespace StickmanFight.Latency.Telemetry;

/// <summary>
/// Источник монотонного времени в микросекундах.
/// Вынесен в интерфейс, чтобы в тестах подставлять время вручную,
/// а не зависеть от реального хода часов.
/// </summary>
public interface IClock
{
    ulong NowUs { get; }
}

/// <summary>
/// Монотонные часы на <see cref="Stopwatch"/>: не прыгают при переводе системного времени
/// и при синхронизации по NTP, поэтому подходят для измерения интервалов.
/// </summary>
public sealed class MonotonicClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public ulong NowUs => (ulong)(_stopwatch.Elapsed.TotalMicroseconds);
}

/// <summary>Часы с ручным управлением — только для тестов.</summary>
public sealed class ManualClock : IClock
{
    public ulong NowUs { get; private set; }

    public ManualClock(ulong startUs = 0) => NowUs = startUs;

    public void AdvanceUs(ulong deltaUs) => NowUs += deltaUs;

    public void AdvanceMs(double deltaMs) => NowUs += (ulong)(deltaMs * 1000.0);
}
