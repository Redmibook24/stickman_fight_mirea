using System.Net.Sockets;
using StickmanFight.Reliable.Protocol;
using StickmanFight.Reliable.Reliability;
using StickmanFight.Reliable.Telemetry;
using StickmanFight.Reliable.Transport;

namespace StickmanFight.Reliable.Client;

/// <summary>
/// Клиент с игровым циклом. На каждом такте цикла проверяет, каким пакетам истёк RTO,
/// и отправляет их повторно; раз в интервал шлёт новую порцию команд:
///
///   SHOOT    надёжно (requiresAck = 1): потерянный выстрел меняет исход боя;
///   MOVEMENT без подтверждения: следующая позиция всё равно заменит потерянную;
///   PING     без подтверждения: даёт замеры RTT для адаптивного тайм-аута.
///
/// Решения о повторах принимают ReliableChannel и AdaptiveTimeout, клиент только
/// связывает их с сетью и часами.
/// </summary>
public sealed class ReliableClient
{
    private const string ReliableCommand = "SHOOT";

    private readonly IUdpTransport _transport;
    private readonly IClock _clock;
    private readonly ClientOptions _options;
    private readonly ReliableChannel _channel;
    private readonly AdaptiveTimeout _timeout = new();
    private readonly LatencyTracker _pings = new(timeoutUs: 3_000_000);
    private readonly List<DeliveryRecord> _records = new();
    private readonly Random _random = new(7);   // только направления и смещения, на метрики не влияет
    private readonly Lock _sync = new();
    private readonly HashSet<ushort> _warmupSequences = new();

    /// <summary>Прогревочные пакеты нумеруются из отдельного диапазона и не пересекаются с серией.</summary>
    private const ushort WarmupSequenceBase = 65000;

    private ushort _sequence;
    private ulong _startUs;

    public ReliableClient(IUdpTransport transport, IClock clock, ClientOptions options)
    {
        options.Validate();

        _transport = transport;
        _clock = clock;
        _options = options;
        _channel = new ReliableChannel(options.MaxAttempts);
        _channel.PacketFailed += OnPacketFailed;
    }

    public int Retransmissions { get; private set; }
    public int DuplicateAcks { get; private set; }
    public int AckSamples { get; private set; }
    public int PingSamples { get; private set; }
    public int RejectedPackets { get; private set; }
    public AdaptiveTimeout Timeout => _timeout;

    public async Task<IReadOnlyList<DeliveryRecord>> RunAsync(CancellationToken token)
    {
        _startUs = _clock.NowUs;

        // Приём останавливаем сами до выхода, иначе он упадёт на закрытом сокете.
        using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receiving = Task.Run(() => ReceiveLoopAsync(receiveStop.Token), CancellationToken.None);

        await WarmUpAsync(token);
        _startUs = _clock.NowUs;   // время серии отсчитывается после прогрева

        int commandsSent = 0;
        ulong nextCommandUs = _startUs;
        ulong intervalUs = (ulong)_options.IntervalMs * 1000;

        while (!token.IsCancellationRequested)
        {
            ulong now = _clock.NowUs;

            lock (_sync)
            {
                if (commandsSent < _options.Count && now >= nextCommandUs)
                {
                    SendCommands(now);
                    commandsSent++;
                    nextCommandUs += intervalUs;
                }

                foreach (var packet in _channel.CollectForRetransmission(now, _timeout.RtoUs))
                {
                    Send(packet.RawBytes);
                    Retransmissions++;
                    Print(ConsoleColor.Yellow,
                          $"   повтор SHOOT seq={packet.Sequence}, попытка {packet.Attempts}, RTO {_timeout.RtoMs:F1} мс");
                }

                _pings.ExpireTimeouts(now);

                if (commandsSent >= _options.Count && _channel.PendingCount == 0) break;
            }

            try
            {
                await Task.Delay(_options.TickMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Короткая пауза, чтобы дошли запоздавшие ACK и попали в счётчик повторных.
        try { await Task.Delay(300, token); } catch (OperationCanceledException) { }

        receiveStop.Cancel();
        await receiving;

        lock (_sync) return _records.OrderBy(r => r.FirstSentAtMs).ToList();
    }

    /// <summary>
    /// Прогрев вне журнала: PING и MOVEMENT с requiresAck = 1 проходят по тем же путям
    /// кода, что и серия, включая отправку и разбор ACK. Ответы на них игнорируются,
    /// а их RTT не попадает в оценку RTO.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken token)
    {
        for (int i = 0; i < _options.WarmupCount; i++)
        {
            ushort pingSeq = (ushort)(WarmupSequenceBase + 2 * i);
            ushort moveSeq = (ushort)(pingSeq + 1);

            lock (_sync)
            {
                _warmupSequences.Add(pingSeq);
                _warmupSequences.Add(moveSeq);
            }

            Send(PacketCodec.SerializePing(pingSeq, _clock.NowUs));
            Send(PacketCodec.SerializeMovement(moveSeq, requiresAck: true, new MovementPayload(0f, 0f, 0f)));

            try { await Task.Delay(100, token); } catch (OperationCanceledException) { return; }
        }

        if (_options.WarmupCount > 0)
            Print(ConsoleColor.DarkGray, $"Прогрев завершён ({_options.WarmupCount} обмена вне журнала).");
    }

    /// <summary>Порция команд одного интервала. Вызывается под блокировкой.</summary>
    private void SendCommands(ulong now)
    {
        ushort pingSeq = ++_sequence;
        _pings.OnPingSent(pingSeq, now);
        Send(PacketCodec.SerializePing(pingSeq, now));

        var movement = new MovementPayload(Step(), 0f, Step());
        Send(PacketCodec.SerializeMovement(++_sequence, requiresAck: false, movement));

        double angle = _random.NextDouble() * Math.PI * 2;
        var shoot = new ShootPayload((byte)_random.Next(1, 4), (float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
        ushort shootSeq = ++_sequence;
        byte[] raw = PacketCodec.SerializeShoot(shootSeq, requiresAck: true, shoot);

        _channel.OnSent(shootSeq, raw, now);
        Send(raw);
        Print(ConsoleColor.DarkCyan, $"-> SHOOT seq={shootSeq} (requiresAck = 1), RTO {_timeout.RtoMs:F1} мс");
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
                Print(ConsoleColor.Red, $"Приём: {ex.SocketErrorCode}, сервер {_options.Server} недоступен?");
                try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                continue;
            }

            ulong receivedUs = _clock.NowUs;
            lock (_sync) HandleDatagram(datagram, receivedUs);
        }
    }

    /// <summary>Разбор ответа сервера. Вызывается под блокировкой.</summary>
    private void HandleDatagram(Datagram datagram, ulong now)
    {
        if (!datagram.RemoteEndPoint.Equals(_options.Server))
        {
            RejectedPackets++;
            return;
        }

        var error = PacketCodec.TryParse(datagram.Data, out var packet);
        if (error != ParseError.None || packet is null)
        {
            RejectedPackets++;
            Print(ConsoleColor.Red, $"<- ответ отброшен: {PacketCodec.Describe(error)}");
            return;
        }

        // Ответы на прогревочные пакеты не учитываются ни в доставке, ни в RTO.
        ushort answered = packet.Payload is AckPayload a ? a.AcknowledgedSequence : packet.Header.Sequence;
        if (_warmupSequences.Contains(answered)) return;

        switch (packet.Payload)
        {
            case AckPayload ack:
                HandleAck(ack.AcknowledgedSequence, now);
                break;

            case PongPayload:
                var outcome = _pings.OnPong(packet.Header.Sequence, now);
                if (outcome.Status == ResponseStatus.Received && outcome.RttMs is double rtt)
                {
                    _timeout.OnSample(rtt);
                    PingSamples++;
                }
                break;

            default:
                RejectedPackets++;
                break;
        }
    }

    private void HandleAck(ushort acknowledged, ulong now)
    {
        if (!_channel.OnAckReceived(acknowledged, out var packet))
        {
            // Пакет уже снят: это ACK на повтор, который сервер подтвердил ещё раз.
            DuplicateAcks++;
            Print(ConsoleColor.DarkGray, $"<- повторный ACK seq={acknowledged}, пакет уже подтверждён, игнорируем");
            return;
        }

        // Правило Карна: если пакет уходил несколько раз, неизвестно, на какую
        // из отправок пришёл ACK, поэтому такой замер RTT в оценку не берём.
        if (packet.Attempts == 1)
        {
            _timeout.OnSample((now - packet.LastSentAtUs) / 1000.0);
            AckSamples++;
        }

        double timeToAckMs = (now - packet.FirstSentAtUs) / 1000.0;
        _records.Add(Record(packet, now, DeliveryStatus.Delivered, timeToAckMs));

        Print(packet.Attempts == 1 ? ConsoleColor.Green : ConsoleColor.DarkGreen,
              $"<- ACK seq={acknowledged}: доставлен с попытки {packet.Attempts}, " +
              $"за {timeToAckMs:F1} мс, RTO {_timeout.RtoMs:F1} мс");
    }

    /// <summary>Попытки исчерпаны. Вызывается из CollectForRetransmission, то есть под блокировкой.</summary>
    private void OnPacketFailed(PendingPacket packet)
    {
        _records.Add(Record(packet, _clock.NowUs, DeliveryStatus.Failed, null));
        Print(ConsoleColor.Red,
              $"[FAILED] выстрел seq={packet.Sequence} не подтверждён сервером после {packet.Attempts} попыток, " +
              "команда считается недоставленной");
    }

    private DeliveryRecord Record(PendingPacket packet, ulong now, DeliveryStatus status, double? timeToAckMs) =>
        new(_options.ExperimentId,
            ReliableCommand,
            packet.Sequence,
            FirstSentAtMs: (packet.FirstSentAtUs - _startUs) / 1000.0,
            ResolvedAtMs: (now - _startUs) / 1000.0,
            packet.Attempts,
            status,
            timeToAckMs,
            _timeout.RtoMs,
            _timeout.SrttMs,
            _timeout.RttVarMs);

    private void Send(byte[] datagram)
    {
        try
        {
            _transport.Send(datagram, _options.Server);
        }
        catch (SocketException ex)
        {
            Print(ConsoleColor.Red, $"Отправка не удалась: {ex.SocketErrorCode}");
        }
    }

    private float Step() => (float)Math.Round(_random.NextDouble() * 2 - 1, 2);

    private static void Print(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.ForegroundColor = previous;
    }
}
