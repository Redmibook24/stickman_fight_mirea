namespace StickmanFight.Latency.Telemetry;

/// <summary>Чем закончилась обработка ответа PONG.</summary>
public enum ResponseStatus
{
    Received,   // ответ пришёл вовремя, RTT учтён
    Timeout,    // ответа не дождались
    Late,       // ответ пришёл позже таймаута — в статистику RTT не берём
    Duplicate,  // ответ на уже закрытый sequenceNumber
    Unknown,    // sequenceNumber, который клиент не отправлял
}

/// <summary>Результат обработки одного PONG.</summary>
/// <param name="Status">Классификация ответа.</param>
/// <param name="Sequence">Номер пакета, к которому относится ответ.</param>
/// <param name="RttMs">RTT в миллисекундах, если его удалось измерить.</param>
/// <param name="SrttMs">Сглаженный RTT после учёта этого замера.</param>
public readonly record struct PongOutcome(ResponseStatus Status, ushort Sequence, double? RttMs, double? SrttMs);

/// <summary>Ожидающий ответа PING.</summary>
internal sealed class Flight
{
    public ulong SentUs { get; init; }
    public bool Completed { get; set; }
}

/// <summary>
/// Ядро телеметрии: помнит отправленные PING, классифицирует ответы,
/// считает RTT, сглаженный RTT и джиттер. Никакой работы с сокетами и файлами —
/// на вход только номера пакетов и отметки времени, поэтому модуль полностью тестируем.
///
///   SRTT = RTT для первого замера, далее SRTT = 0.875 * SRTT + 0.125 * RTT
///   J    = (1 / (n - 1)) * sum |RTT_i - RTT_{i-1}|
/// </summary>
public sealed class LatencyTracker
{
    public const ulong DefaultTimeoutUs = 1_000_000;   // 1 секунда

    private readonly Dictionary<ushort, Flight> _inFlight = new();
    private readonly ulong _timeoutUs;

    private double _srttMs;
    private double _previousRttMs;
    private double _jitterSumMs;
    private bool _hasPrevious;

    public LatencyTracker(ulong timeoutUs = DefaultTimeoutUs)
    {
        if (timeoutUs == 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutUs), "Таймаут должен быть больше нуля.");

        _timeoutUs = timeoutUs;
    }

    /// <summary>Сколько замеров попало в статистику RTT.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Сколько PING остались без ответа (посчитано методом <see cref="ExpireTimeouts"/>).</summary>
    public int TimeoutCount { get; private set; }

    /// <summary>Сколько PING отправлено всего.</summary>
    public int SentCount { get; private set; }

    public double SrttMs => _srttMs;

    /// <summary>Средний джиттер по формуле из задания.</summary>
    public double MeanJitterMs => SampleCount > 1 ? _jitterSumMs / (SampleCount - 1) : 0.0;

    /// <summary>Доля потерь в процентах.</summary>
    public double LossRatePercent => SentCount == 0 ? 0.0 : 100.0 * TimeoutCount / SentCount;

    public ulong TimeoutUs => _timeoutUs;

    /// <summary>Регистрирует отправленный PING.</summary>
    public void OnPingSent(ushort sequence, ulong nowUs)
    {
        _inFlight[sequence] = new Flight { SentUs = nowUs };
        SentCount++;
    }

    /// <summary>Классифицирует ответ и при необходимости обновляет метрики.</summary>
    public PongOutcome OnPong(ushort sequence, ulong nowUs)
    {
        if (!_inFlight.TryGetValue(sequence, out var flight))
            return new PongOutcome(ResponseStatus.Unknown, sequence, null, null);

        // Часы монотонные, но защищаемся от отметки «из прошлого» на всякий случай.
        ulong elapsedUs = nowUs >= flight.SentUs ? nowUs - flight.SentUs : 0;
        double rttMs = elapsedUs / 1000.0;

        // Ответ пришёл позже таймаута: замер уже закрыт (как правило, как timeout),
        // поэтому в статистику RTT он не идёт — только фиксируется в журнале.
        if (elapsedUs > _timeoutUs)
        {
            flight.Completed = true;
            return new PongOutcome(ResponseStatus.Late, sequence, rttMs, _srttMs);
        }

        if (flight.Completed)
            return new PongOutcome(ResponseStatus.Duplicate, sequence, null, null);

        flight.Completed = true;

        if (_hasPrevious)
            _jitterSumMs += Math.Abs(rttMs - _previousRttMs);

        _previousRttMs = rttMs;
        _hasPrevious = true;

        _srttMs = SampleCount == 0 ? rttMs : 0.875 * _srttMs + 0.125 * rttMs;
        SampleCount++;

        return new PongOutcome(ResponseStatus.Received, sequence, rttMs, _srttMs);
    }

    /// <summary>
    /// Закрывает PING, для которых истёк таймаут, и возвращает их номера.
    /// Вызывается периодически и один раз в конце эксперимента.
    /// </summary>
    public IReadOnlyList<ushort> ExpireTimeouts(ulong nowUs)
    {
        List<ushort>? expired = null;

        foreach (var (sequence, flight) in _inFlight)
        {
            if (flight.Completed || nowUs - flight.SentUs <= _timeoutUs) continue;

            flight.Completed = true;
            TimeoutCount++;
            (expired ??= new List<ushort>()).Add(sequence);
        }

        return (IReadOnlyList<ushort>?)expired ?? Array.Empty<ushort>();
    }

    /// <summary>Есть ли ещё пакеты, по которым не принято решение.</summary>
    public bool HasPending => _inFlight.Values.Any(flight => !flight.Completed);
}
