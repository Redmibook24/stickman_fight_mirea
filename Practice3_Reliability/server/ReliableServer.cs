using System.Net;
using System.Net.Sockets;
using StickmanFight.Reliable.Protocol;
using StickmanFight.Reliable.Reliability;
using StickmanFight.Reliable.Telemetry;
using StickmanFight.Reliable.Transport;

namespace StickmanFight.Reliable.Server;

/// <summary>Состояние игрока на сервере: то, на что влияют команды.</summary>
internal sealed class PlayerState
{
    public PlayerState(int dedupWindow) => Window = new DeduplicationWindow(dedupWindow);

    public DeduplicationWindow Window { get; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>Сколько выстрелов реально применено. Должно совпадать с числом разных SHOOT.</summary>
    public int ShotsApplied { get; set; }
}

/// <summary>
/// Сервер: на пакет с requiresAck = 1 сразу отвечает ACK, затем через окно
/// дедупликации решает, применять ли игровой эффект. Повтор, пришедший из-за
/// retransmission, подтверждается ещё раз (прошлый ACK мог потеряться),
/// но эффект второй раз не применяется.
/// </summary>
public sealed class ReliableServer
{
    private readonly IUdpTransport _transport;
    private readonly IClock _clock;
    private readonly int _dedupWindow;
    private readonly bool _verbose;
    private readonly Dictionary<IPEndPoint, PlayerState> _players = new();

    private ushort _sequence;   // собственный поток номеров сервера (ACK)

    public ReliableServer(IUdpTransport transport, IClock clock, int dedupWindow, bool verbose)
    {
        _transport = transport;
        _clock = clock;
        _dedupWindow = dedupWindow;
        _verbose = verbose;
    }

    public int AcksSent { get; private set; }
    public int DuplicatesSuppressed { get; private set; }
    public int CommandsApplied { get; private set; }
    public int RejectedPackets { get; private set; }

    public async Task RunAsync(CancellationToken token)
    {
        Log($"Сервер слушает {_transport.LocalEndPoint}, окно дедупликации {_dedupWindow} номеров.");

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
                Log($"Ошибка сокета: {ex.SocketErrorCode}");
                continue;
            }

            HandleDatagram(datagram, _clock.NowUs);
        }

        Log($"[STAT] команд применено {CommandsApplied}, повторов отсечено {DuplicatesSuppressed}, " +
            $"ACK отправлено {AcksSent}, отброшено пакетов {RejectedPackets}");
    }

    private void HandleDatagram(Datagram datagram, ulong receivedUs)
    {
        var error = PacketCodec.TryParse(datagram.Data, out var packet);
        if (error != ParseError.None || packet is null)
        {
            RejectedPackets++;
            Log($"[BAD] {datagram.RemoteEndPoint}: {PacketCodec.Describe(error)} ({datagram.Data.Length} байт)");
            return;
        }

        var header = packet.Header;

        // ACK уходит до обработки: клиенту важно узнать о доставке как можно раньше,
        // а применение эффекта от этого не зависит.
        if (header.RequiresAck)
        {
            Send(PacketCodec.SerializeAck(++_sequence, header.Sequence), datagram.RemoteEndPoint);
            AcksSent++;
        }

        switch (packet.Payload)
        {
            case PingPayload ping:
                Send(PacketCodec.SerializePong(header.Sequence, ping.ClientSendTimeUs, receivedUs, _clock.NowUs),
                     datagram.RemoteEndPoint);
                break;

            case MovementPayload movement:
                ApplyOnce(datagram.RemoteEndPoint, header, player =>
                {
                    player.X += movement.Dx;
                    player.Y += movement.Dy;
                    player.Z += movement.Dz;
                    return $"MOVEMENT seq={header.Sequence} -> позиция ({player.X:F2}; {player.Y:F2}; {player.Z:F2})";
                });
                break;

            case ShootPayload shoot:
                ApplyOnce(datagram.RemoteEndPoint, header, player =>
                {
                    player.ShotsApplied++;
                    return $"SHOOT seq={header.Sequence} оружие #{shoot.WeaponId} применён, всего выстрелов {player.ShotsApplied}";
                });
                break;

            case AckPayload:
                // Клиент серверу ничего надёжного не шлёт в ответ, ACK от него не ожидается.
                RejectedPackets++;
                Log($"[BAD] {datagram.RemoteEndPoint}: неожиданный ACK seq={header.Sequence}");
                break;

            case PongPayload:
                RejectedPackets++;
                Log($"[BAD] {datagram.RemoteEndPoint}: неожиданный PONG seq={header.Sequence}");
                break;
        }
    }

    /// <summary>Применяет эффект команды, только если её номер ещё не встречался.</summary>
    private void ApplyOnce(IPEndPoint sender, PacketHeader header, Func<PlayerState, string> effect)
    {
        if (!_players.TryGetValue(sender, out var player))
        {
            player = new PlayerState(_dedupWindow);
            _players[sender] = player;
        }

        if (!player.Window.TryRegister(header.Sequence))
        {
            DuplicatesSuppressed++;
            Log($"[DUP] {sender}: {header.Type} seq={header.Sequence} уже обработан, эффект не применяется");
            return;
        }

        CommandsApplied++;
        string description = effect(player);
        if (_verbose) Log($"{sender}: {description}");
    }

    private void Send(byte[] datagram, IPEndPoint target)
    {
        try
        {
            _transport.Send(datagram, target);
        }
        catch (SocketException ex)
        {
            Log($"Не удалось отправить ответ {target}: {ex.SocketErrorCode}");
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
