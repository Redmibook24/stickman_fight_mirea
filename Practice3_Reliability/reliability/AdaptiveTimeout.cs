namespace StickmanFight.Reliable.Reliability;

/// <summary>
/// Адаптивный тайм-аут повторной передачи по мотивам алгоритма Джекобсона (TCP, RFC 6298):
///
///   первый замер:  SRTT = RTT,  RTTVAR = RTT / 2
///   далее:         RTTVAR = (1 - β) * RTTVAR + β * |SRTT - RTT|,  β = 0.25
///                  SRTT   = (1 - α) * SRTT   + α * RTT,           α = 0.125
///   RTO = SRTT + 4 * RTTVAR, ограничен диапазоном [100; 3000] мс.
///
/// RTTVAR обновляется раньше SRTT: отклонение замера считается от прежней оценки.
/// Пока замеров нет, RTO равен начальному значению 1000 мс, как рекомендует RFC 6298.
/// </summary>
public sealed class AdaptiveTimeout
{
    public const double Alpha = 0.125;
    public const double Beta = 0.25;
    public const double DefaultMinRtoMs = 100.0;
    public const double DefaultMaxRtoMs = 3000.0;
    public const double DefaultInitialRtoMs = 1000.0;

    private readonly double _minRtoMs;
    private readonly double _maxRtoMs;
    private readonly double _initialRtoMs;

    public AdaptiveTimeout(double minRtoMs = DefaultMinRtoMs,
                           double maxRtoMs = DefaultMaxRtoMs,
                           double initialRtoMs = DefaultInitialRtoMs)
    {
        if (!(minRtoMs > 0) || !(maxRtoMs >= minRtoMs))
            throw new ArgumentOutOfRangeException(nameof(minRtoMs), "Нужно 0 < minRto <= maxRto.");

        _minRtoMs = minRtoMs;
        _maxRtoMs = maxRtoMs;
        _initialRtoMs = Math.Clamp(initialRtoMs, minRtoMs, maxRtoMs);
    }

    public bool IsInitialized => SampleCount > 0;

    public int SampleCount { get; private set; }

    public double SrttMs { get; private set; }

    public double RttVarMs { get; private set; }

    /// <summary>Текущий тайм-аут повторной передачи, мс.</summary>
    public double RtoMs => IsInitialized
        ? Math.Clamp(SrttMs + 4.0 * RttVarMs, _minRtoMs, _maxRtoMs)
        : _initialRtoMs;

    public ulong RtoUs => (ulong)(RtoMs * 1000.0);

    /// <summary>Учитывает новый замер RTT в миллисекундах.</summary>
    public void OnSample(double rttMs)
    {
        if (!double.IsFinite(rttMs) || rttMs < 0)
            throw new ArgumentOutOfRangeException(nameof(rttMs), rttMs, "Замер RTT должен быть конечным и неотрицательным.");

        if (!IsInitialized)
        {
            SrttMs = rttMs;
            RttVarMs = rttMs / 2.0;
        }
        else
        {
            RttVarMs = (1 - Beta) * RttVarMs + Beta * Math.Abs(SrttMs - rttMs);
            SrttMs = (1 - Alpha) * SrttMs + Alpha * rttMs;
        }

        SampleCount++;
    }
}
