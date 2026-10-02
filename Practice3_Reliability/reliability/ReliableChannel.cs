using System.Diagnostics.CodeAnalysis;

namespace StickmanFight.Reliable.Reliability;

/// <summary>Неподтверждённый надёжный пакет.</summary>
public sealed class PendingPacket
{
    public PendingPacket(ushort sequence, byte[] rawBytes, ulong sentAtUs)
    {
        Sequence = sequence;
        RawBytes = rawBytes;
        FirstSentAtUs = sentAtUs;
        LastSentAtUs = sentAtUs;
    }

    public ushort Sequence { get; }

    /// <summary>Сериализованный пакет: при повторе уходит байт в байт тот же, с тем же номером.</summary>
    public byte[] RawBytes { get; }

    /// <summary>Момент первой отправки: от него считается время до подтверждения.</summary>
    public ulong FirstSentAtUs { get; }

    public ulong LastSentAtUs { get; internal set; }

    /// <summary>Сколько раз пакет уже отправлен, первая отправка тоже считается.</summary>
    public int Attempts { get; internal set; } = 1;
}

/// <summary>
/// Менеджер надёжной доставки. Только учёт неподтверждённых пакетов: сокетов,
/// часов и сериализации здесь нет, время приходит параметром. Поэтому класс
/// полностью проверяется модульными тестами.
/// </summary>
public sealed class ReliableChannel
{
    public const int DefaultMaxAttempts = 5;

    private readonly Dictionary<ushort, PendingPacket> _pending = new();
    private readonly List<PendingPacket> _failed = new();

    public ReliableChannel(int maxAttempts = DefaultMaxAttempts)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Нужна хотя бы одна попытка.");

        MaxAttempts = maxAttempts;
    }

    public int MaxAttempts { get; }

    public int PendingCount => _pending.Count;

    public int FailedCount => _failed.Count;

    /// <summary>Окончательно недоставленные пакеты в порядке, в котором они были признаны потерянными.</summary>
    public IReadOnlyList<PendingPacket> Failed => _failed;

    /// <summary>Вызывается, когда пакет исчерпал попытки. Клиент логирует это как событие игры.</summary>
    public event Action<PendingPacket>? PacketFailed;

    /// <summary>Регистрирует отправленный надёжный пакет.</summary>
    public void OnSent(ushort sequence, byte[] rawBytes, ulong nowUs)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);

        // Номер ещё ждёт подтверждения: значит, счётчик отправителя обернулся
        // через 65536 быстрее, чем пришёл ACK. Молча затереть запись нельзя.
        if (_pending.ContainsKey(sequence))
            throw new InvalidOperationException($"Пакет seq={sequence} уже ожидает подтверждения.");

        _pending[sequence] = new PendingPacket(sequence, rawBytes, nowUs);
    }

    /// <summary>Обрабатывает ACK. Возвращает true, если ожидающий пакет найден и снят.</summary>
    public bool OnAckReceived(ushort sequence) => OnAckReceived(sequence, out _);

    /// <summary>
    /// То же, но отдаёт снятый пакет: по нему клиент считает время до подтверждения
    /// и решает, можно ли брать замер RTT (правило Карна).
    /// Повторный ACK на уже снятый пакет, как и ACK на неизвестный номер, игнорируется.
    /// </summary>
    public bool OnAckReceived(ushort sequence, [NotNullWhen(true)] out PendingPacket? packet) =>
        _pending.Remove(sequence, out packet);

    /// <summary>
    /// Возвращает пакеты, которые пора отправить повторно, и увеличивает их счётчик попыток.
    /// Пакет попадает в список не чаще, чем раз в <paramref name="timeoutUs"/> от последней отправки.
    /// Пакет, у которого истёк тайм-аут последней разрешённой попытки, переносится в список
    /// недоставленных и наружу не возвращается.
    /// </summary>
    public IReadOnlyList<PendingPacket> CollectForRetransmission(ulong nowUs, ulong timeoutUs)
    {
        if (timeoutUs == 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutUs), "Тайм-аут должен быть больше нуля.");

        var due = new List<PendingPacket>();
        List<PendingPacket>? exhausted = null;

        foreach (var packet in _pending.Values.OrderBy(p => p.FirstSentAtUs))
        {
            if (nowUs < packet.LastSentAtUs || nowUs - packet.LastSentAtUs < timeoutUs) continue;

            if (packet.Attempts >= MaxAttempts)
            {
                (exhausted ??= new List<PendingPacket>()).Add(packet);
                continue;
            }

            packet.Attempts++;
            packet.LastSentAtUs = nowUs;
            due.Add(packet);
        }

        if (exhausted is not null)
        {
            foreach (var packet in exhausted)
            {
                _pending.Remove(packet.Sequence);
                _failed.Add(packet);
                PacketFailed?.Invoke(packet);
            }
        }

        return due;
    }
}
