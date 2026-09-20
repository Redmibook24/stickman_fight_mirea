using System.Globalization;
using System.Net;

namespace StickmanFight.Latency.Transport;

/// <summary>
/// Параметры эмуляции сети. Задаются строкой вида
/// «delay=100,jitter=50,loss=5,duplicate=10» и применяются к исходящим датаграммам.
/// </summary>
/// <param name="DelayMs">Постоянная задержка отправки, мс.</param>
/// <param name="JitterMs">Случайная добавка к задержке, равномерно из [0; JitterMs], мс.</param>
/// <param name="LossPercent">Вероятность потерять датаграмму, %.</param>
/// <param name="DuplicatePercent">Вероятность отправить датаграмму дважды, %.</param>
/// <param name="Seed">Зерно генератора — при одном и том же значении сценарий воспроизводим.</param>
public readonly record struct EmulationOptions(
    int DelayMs = 0,
    int JitterMs = 0,
    double LossPercent = 0,
    double DuplicatePercent = 0,
    int Seed = 20250920)
{
    public bool IsEnabled => DelayMs > 0 || JitterMs > 0 || LossPercent > 0 || DuplicatePercent > 0;

    /// <summary>Разбирает строку «delay=100,jitter=50,loss=5,duplicate=10».</summary>
    public static EmulationOptions Parse(string? text, int seed)
    {
        var options = new EmulationOptions(Seed: seed);
        if (string.IsNullOrWhiteSpace(text) || text == "none") return options;

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
                throw new FormatException($"Не разобран параметр эмуляции «{part}», ожидается имя=значение.");

            string name = pair[0].Trim().ToLowerInvariant();
            double value = double.Parse(pair[1].Trim(), CultureInfo.InvariantCulture);

            options = name switch
            {
                "delay"     => options with { DelayMs = Check(value, 0, 10_000, name) },
                "jitter"    => options with { JitterMs = Check(value, 0, 10_000, name) },
                "loss"      => options with { LossPercent = CheckPercent(value, name) },
                "duplicate" => options with { DuplicatePercent = CheckPercent(value, name) },
                _ => throw new FormatException($"Неизвестный параметр эмуляции «{name}»."),
            };
        }

        return options;
    }

    public override string ToString() =>
        IsEnabled
            ? $"delay={DelayMs}мс, jitter={JitterMs}мс, loss={LossPercent}%, duplicate={DuplicatePercent}%, seed={Seed}"
            : "без эмуляции";

    private static int Check(double value, int min, int max, string name) =>
        value >= min && value <= max
            ? (int)value
            : throw new ArgumentOutOfRangeException(name, value, $"Значение должно быть в диапазоне [{min}; {max}].");

    private static double CheckPercent(double value, string name) =>
        value is >= 0 and <= 100
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "Доля должна быть в диапазоне [0; 100] %.");
}

/// <summary>
/// Декоратор транспорта: добавляет исходящим датаграммам задержку, джиттер,
/// потери и дубликаты. Нужен, чтобы сценарии эксперимента воспроизводились
/// на одной машине без внешнего эмулятора вроде Clumsy — при одинаковом seed
/// последовательность решений «потерять / задержать / продублировать» одна и та же.
/// </summary>
public sealed class EmulatedTransport : IUdpTransport
{
    private readonly IUdpTransport _inner;
    private readonly EmulationOptions _options;
    private readonly Random _random;
    private readonly Lock _sync = new();

    public EmulatedTransport(IUdpTransport inner, EmulationOptions options)
    {
        _inner = inner;
        _options = options;
        _random = new Random(options.Seed);
    }

    public IPEndPoint LocalEndPoint => _inner.LocalEndPoint;

    public int DroppedCount { get; private set; }
    public int DuplicatedCount { get; private set; }

    public void Send(ReadOnlySpan<byte> data, IPEndPoint target)
    {
        var payload = data.ToArray();   // копия: отправка может быть отложенной

        bool drop;
        bool duplicate;
        int delayMs;

        lock (_sync)
        {
            drop = _options.LossPercent > 0 && _random.NextDouble() * 100.0 < _options.LossPercent;
            duplicate = !drop && _options.DuplicatePercent > 0 &&
                        _random.NextDouble() * 100.0 < _options.DuplicatePercent;
            delayMs = _options.DelayMs + (_options.JitterMs > 0 ? _random.Next(_options.JitterMs + 1) : 0);

            if (drop) DroppedCount++;
            if (duplicate) DuplicatedCount++;
        }

        if (drop) return;

        ScheduleSend(payload, target, delayMs);
        if (duplicate) ScheduleSend(payload, target, delayMs);
    }

    public Task<Datagram> ReceiveAsync(CancellationToken token) => _inner.ReceiveAsync(token);

    public void Dispose() => _inner.Dispose();

    private void ScheduleSend(byte[] payload, IPEndPoint target, int delayMs)
    {
        if (delayMs <= 0)
        {
            _inner.Send(payload, target);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs);
                _inner.Send(payload, target);
            }
            catch (ObjectDisposedException)
            {
                // транспорт закрылся, пока датаграмма ждала отправки — это нормально
            }
        });
    }
}
