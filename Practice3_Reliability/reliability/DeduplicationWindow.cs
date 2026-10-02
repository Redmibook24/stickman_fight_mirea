namespace StickmanFight.Reliable.Reliability;

/// <summary>
/// Окно недавно обработанных номеров пакетов одного отправителя.
/// Повтор из-за retransmission приходит с тем же sequenceNumber, что и оригинал,
/// поэтому по окну сервер отличает его от новой команды и не применяет эффект дважды.
/// Окно ограничено: самые старые номера вытесняются, память не растёт.
/// </summary>
public sealed class DeduplicationWindow
{
    public const int DefaultCapacity = 256;

    private readonly HashSet<ushort> _seen = new();
    private readonly Queue<ushort> _order = new();

    public DeduplicationWindow(int capacity = DefaultCapacity)
    {
        // Окно должно быть заметно меньше пространства номеров uint16, иначе после
        // оборота счётчика новая команда будет принята за старую.
        if (capacity is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Размер окна должен быть в диапазоне [1; 32768].");

        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _seen.Count;

    /// <summary>
    /// Запоминает номер. Возвращает true, если номер новый и эффект команды нужно применить,
    /// и false, если это повтор уже обработанного пакета.
    /// </summary>
    public bool TryRegister(ushort sequence)
    {
        if (!_seen.Add(sequence)) return false;

        _order.Enqueue(sequence);
        if (_order.Count > Capacity)
            _seen.Remove(_order.Dequeue());

        return true;
    }

    public bool Contains(ushort sequence) => _seen.Contains(sequence);
}
