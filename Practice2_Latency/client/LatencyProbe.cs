using System.Net;
using System.Net.Sockets;
using StickmanFight.Latency.Protocol;
using StickmanFight.Latency.Telemetry;
using StickmanFight.Latency.Transport;

namespace StickmanFight.Latency.Client;

/// <summary>
/// Прогон одного сценария: шлёт PING с заданной периодичностью, принимает PONG,
/// отдаёт расчёт метрик модулю telemetry и собирает строки журнала.
/// Сам ничего не считает и не пишет в файл — этим занимаются telemetry и вызывающий код.
/// </summary>
public sealed class LatencyProbe
{
    private readonly IUdpTransport _transport;
    private readonly IClock _clock;
    private readonly LatencyTracker _tracker;
    private readonly ProbeOptions _options;

    private readonly Dictionary<ushort, PingRecord> _pings = new();
    private readonly HashSet<ushort> _warmupSequences = new();
    private readonly List<LatencySample> _rows = new();
    private readonly Lock _sync = new();

    private ulong _startUs;

    public LatencyProbe(IUdpTransport transport, IClock clock, ProbeOptions options)
    {
        options.Validate();

        _transport = transport;
        _clock = clock;
        _options = options;
        _tracker = new LatencyTracker((ulong)options.TimeoutMs * 1000);
    }

    /// <summary>Сколько датаграмм отброшено проверками протокола.</summary>
    public int RejectedPackets { get; private set; }

    public LatencyTracker Tracker => _tracker;

    public async Task<IReadOnlyList<LatencySample>> RunAsync(CancellationToken token)
    {
        _startUs = _clock.NowUs;

        var receiving = Task.Run(() => ReceiveLoopAsync(token), token);

        await WarmUpAsync(token);
        _startUs = _clock.NowUs;   // отсчёт времени эксперимента начинается после прогрева

        for (int sample = 1; sample <= _options.Count && !token.IsCancellationRequested; sample++)
        {
            SendPing((ushort)sample, sample);

            try
            {
                await Task.Delay(_options.IntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ExpireAndLog();
        }

        // Ждём ответы на последние PING: таймаут плюс небольшой запас.
        try
        {
            await Task.Delay(_options.TimeoutMs + 300, token);
        }
        catch (OperationCanceledException)
        {
            // прогон прерван пользователем — фиксируем то, что успели собрать
        }

        ExpireAndLog();

        await Task.WhenAny(receiving, Task.Delay(200, CancellationToken.None));

        lock (_sync)
        {
            // Строки-события без своего PING (unknown_response) уходят в конец файла.
            return _rows
                .OrderBy(row => row.Sample == 0 ? int.MaxValue : row.Sample)
                .ToList();
        }
    }

    /// <summary>
    /// Прогрев: несколько PING вне журнала, чтобы первый измеренный замер
    /// не включал в себя JIT-компиляцию и первичную инициализацию сокета.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken token)
    {
        if (_options.WarmupCount <= 0) return;

        for (int i = 0; i < _options.WarmupCount; i++)
        {
            ushort sequence = (ushort)(WarmupSequenceBase + i);

            lock (_sync) _warmupSequences.Add(sequence);

            try
            {
                _transport.Send(PacketCodec.SerializePing(sequence, _clock.NowUs), _options.Server);
                await Task.Delay(100, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                Print(ConsoleColor.Red, $"Прогревочный PING не отправлен: {ex.SocketErrorCode}");
            }
        }

        Print(ConsoleColor.DarkGray, $"Прогрев завершён ({_options.WarmupCount} PING вне журнала).");
    }

    private void SendPing(ushort sequence, int sample)
    {
        ulong sentUs = _clock.NowUs;
        double sentAtMs = (sentUs - _startUs) / 1000.0;

        lock (_sync)
        {
            _pings[sequence] = new PingRecord(sample, sentUs, sentAtMs);
            _tracker.OnPingSent(sequence, sentUs);
        }

        try
        {
            _transport.Send(PacketCodec.SerializePing(sequence, sentUs), _options.Server);
        }
        catch (SocketException ex)
        {
            Print(ConsoleColor.Red, $"PING seq={sequence} не отправлен: {ex.SocketErrorCode}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Datagram datagram;
            try
            {
                datagram = await _transport.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Print(ConsoleColor.Red, $"Приём: {ex.SocketErrorCode} — сервер {_options.Server} недоступен?");
                try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                continue;
            }

            // Отметку времени берём сразу после приёма, до разбора пакета.
            ulong receivedUs = _clock.NowUs;
            HandleDatagram(datagram, receivedUs);
        }
    }

    private void HandleDatagram(Datagram datagram, ulong receivedUs)
    {
        if (!datagram.RemoteEndPoint.Equals(_options.Server))
        {
            RejectedPackets++;
            Print(ConsoleColor.Red, $"Датаграмма с чужого адреса {datagram.RemoteEndPoint} — отброшена.");
            return;
        }

        var error = PacketCodec.TryParsePong(datagram.Data, out var pong);
        if (error != ParseError.None)
        {
            RejectedPackets++;
            Print(ConsoleColor.Red, $"Ответ отброшен: {PacketCodec.Describe(error)} ({datagram.Data.Length} байт).");
            return;
        }

        PongOutcome outcome;
        PingRecord? record;

        lock (_sync)
        {
            // Ответы на прогревочные пакеты в журнал не попадают.
            if (_warmupSequences.Contains(pong.Sequence)) return;

            record = _pings.TryGetValue(pong.Sequence, out var found) ? found : null;

            // Сервер обязан вернуть нашу же отметку времени: иначе это чужой или испорченный ответ.
            if (record is { } known && pong.ClientSendTimeUs != known.SentUs)
            {
                RejectedPackets++;
                Print(ConsoleColor.Red,
                      $"Ответ seq={pong.Sequence} отброшен: эхо clientSendTimeUs не совпадает с отправленным.");
                return;
            }

            outcome = _tracker.OnPong(pong.Sequence, receivedUs);
            AddRow(outcome, record, pong);
        }
    }

    /// <summary>Записывает строку журнала и печатает событие. Вызывается под блокировкой.</summary>
    private void AddRow(PongOutcome outcome, PingRecord? record, PongPacket pong)
    {
        int sample = record?.Sample ?? 0;
        double sentAtMs = record?.SentAtMs ?? 0.0;
        double serverMs = pong.ServerProcessingTimeUs / 1000.0;

        switch (outcome.Status)
        {
            case ResponseStatus.Received:
                _rows.Add(new LatencySample(_options.ExperimentId, sample, pong.Sequence, sentAtMs,
                                            outcome.RttMs, outcome.SrttMs, ResponseStatus.Received));
                Print(ConsoleColor.Green,
                      $"#{sample,3} seq={pong.Sequence,-5} RTT={outcome.RttMs,7:F3} мс  SRTT={outcome.SrttMs,7:F3} мс  " +
                      $"(на сервере {serverMs:F3} мс)");
                break;

            case ResponseStatus.Late:
                _rows.Add(new LatencySample(_options.ExperimentId, sample, pong.Sequence, sentAtMs,
                                            outcome.RttMs, null, ResponseStatus.Late));
                Print(ConsoleColor.Yellow,
                      $"#{sample,3} seq={pong.Sequence,-5} ответ опоздал: {outcome.RttMs:F3} мс > таймаута {_options.TimeoutMs} мс");
                break;

            case ResponseStatus.Duplicate:
                _rows.Add(new LatencySample(_options.ExperimentId, sample, pong.Sequence, sentAtMs,
                                            null, null, ResponseStatus.Duplicate));
                Print(ConsoleColor.DarkYellow, $"#{sample,3} seq={pong.Sequence,-5} дубликат ответа — не учитываем");
                break;

            case ResponseStatus.Unknown:
                _rows.Add(new LatencySample(_options.ExperimentId, 0, pong.Sequence, 0.0,
                                            null, null, ResponseStatus.Unknown));
                Print(ConsoleColor.Magenta, $"     seq={pong.Sequence,-5} ответ на неотправленный PING — не учитываем");
                break;
        }
    }

    /// <summary>Закрывает просроченные PING и пишет их в журнал как timeout.</summary>
    private void ExpireAndLog()
    {
        lock (_sync)
        {
            foreach (ushort sequence in _tracker.ExpireTimeouts(_clock.NowUs))
            {
                var record = _pings[sequence];
                _rows.Add(new LatencySample(_options.ExperimentId, record.Sample, sequence, record.SentAtMs,
                                            null, null, ResponseStatus.Timeout));
                Print(ConsoleColor.Red, $"#{record.Sample,3} seq={sequence,-5} таймаут {_options.TimeoutMs} мс — ответа нет");
            }
        }
    }

    private static void Print(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.ForegroundColor = previous;
    }

    /// <summary>Прогревочные пакеты нумеруются из отдельного диапазона, чтобы не пересекаться с замерами.</summary>
    private const ushort WarmupSequenceBase = 65000;

    private readonly record struct PingRecord(int Sample, ulong SentUs, double SentAtMs);
}
