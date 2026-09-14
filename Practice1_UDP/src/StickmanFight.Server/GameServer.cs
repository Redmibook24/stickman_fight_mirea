using System.Net;
using System.Net.Sockets;

namespace StickmanFight.Net.Server;

/// <summary>
/// UDP-сервер: принимает датаграммы, разбирает заголовок, обрабатывает команды
/// MOVEMENT и SHOOT и отвечает обновлённым состоянием или результатом выстрела.
/// </summary>
public sealed class GameServer : IDisposable
{
    private readonly UdpClient _socket;
    private readonly Logger _log;
    private readonly Dictionary<IPEndPoint, PlayerSession> _players = new();
    private readonly Random _random = new();

    private ushort _nextPlayerId = 1;
    private ushort _sequence;          // порядковый номер исходящих пакетов сервера
    private int _receivedPackets;
    private int _rejectedPackets;

    public GameServer(int port, Logger log)
    {
        _log = log;
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));

        // Windows: без этого после «мёртвого» клиента ReceiveAsync падает с WSAECONNRESET (10054).
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        }

        _log.Info($"UDP-сервер слушает 0.0.0.0:{port} (размер заголовка — {PacketHeader.Size} байт)");
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log.Warn($"Ошибка сокета при приёме: {ex.SocketErrorCode} ({ex.Message})");
                continue;
            }

            try
            {
                HandleDatagram(received.Buffer, received.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _log.Error($"Сбой при обработке пакета от {received.RemoteEndPoint}: {ex.Message}");
            }
        }

        _log.Info($"Сервер остановлен. Принято пакетов: {_receivedPackets}, отвергнуто: {_rejectedPackets}.");
    }

    private void HandleDatagram(byte[] datagram, IPEndPoint sender)
    {
        _receivedPackets++;

        var parseResult = Packet.TryParse(datagram, out var packet);
        if (parseResult != ParseResult.Ok || packet is null)
        {
            _rejectedPackets++;
            _log.Warn($"Пакет от {sender} отвергнут: {Packet.Describe(parseResult)}. " +
                      $"Байты: {Packet.ToHex(datagram)}");
            return;
        }

        var header = packet.Header;
        _log.Packet($"{sender} <- {header} | данные: [{Packet.ToHex(packet.Payload)}]");

        // CONNECT обрабатываем отдельно: до него игрока в таблице ещё нет.
        if (header.Type == PacketType.Connect)
        {
            HandleConnect(packet, sender);
            return;
        }

        if (!_players.TryGetValue(sender, out var player))
        {
            _rejectedPackets++;
            _log.Warn($"{sender}: команда {header.Type} без рукопожатия — отправлен ERROR.");
            SendError(sender, "Сначала отправьте пакет CONNECT.");
            return;
        }

        player.LastSeenUtc = DateTime.UtcNow;

        // Защита от дубликатов и «опоздавших» датаграмм: UDP порядок доставки не гарантирует.
        if (header.Sequence <= player.LastSequence && header.Type != PacketType.Disconnect)
        {
            _rejectedPackets++;
            _log.Warn($"{player}: повторный или устаревший пакет seq={header.Sequence} " +
                      $"(последний принятый — {player.LastSequence}), команда проигнорирована.");
            Send(sender, PacketType.StateUpdate, player.ToState(header.Sequence).ToBytes());
            return;
        }

        player.LastSequence = header.Sequence;

        switch (header.Type)
        {
            case PacketType.Movement:
                HandleMovement(packet, player);
                break;

            case PacketType.Shoot:
                HandleShoot(packet, player);
                break;

            case PacketType.Disconnect:
                _log.Command($"{player}: DISCONNECT — игрок покинул арену.");
                _players.Remove(sender);
                break;

            default:
                _rejectedPackets++;
                _log.Warn($"{player}: неизвестный тип команды {(byte)header.Type}.");
                SendError(sender, $"Тип команды {(byte)header.Type} не поддерживается.");
                break;
        }
    }

    private void HandleConnect(Packet packet, IPEndPoint sender)
    {
        if (!ConnectPayload.TryRead(packet.Payload, out var connect))
        {
            _rejectedPackets++;
            SendError(sender, "Некорректная нагрузка CONNECT.");
            return;
        }

        if (!_players.TryGetValue(sender, out var player))
        {
            player = new PlayerSession(_nextPlayerId++, connect.PlayerName, sender)
            {
                X = _random.Next(-10, 11),
                Y = 0f,
                Z = _random.Next(-10, 11),
            };
            _players[sender] = player;

            _log.Command($"{player}: CONNECT — игрок подключён, " +
                         $"старт ({player.X:F1}; {player.Y:F1}; {player.Z:F1}), " +
                         $"HP={player.Hp}, патроны={player.Ammo}. Всего игроков: {_players.Count}.");
        }
        else
        {
            _log.Command($"{player}: повторный CONNECT — рукопожатие подтверждено заново.");
        }

        player.LastSequence = packet.Header.Sequence;
        player.LastSeenUtc = DateTime.UtcNow;

        Send(sender, PacketType.ConnectAck, player.ToState(packet.Header.Sequence).ToBytes());
    }

    private void HandleMovement(Packet packet, PlayerSession player)
    {
        if (!MovementPayload.TryRead(packet.Payload, out var movement))
        {
            _rejectedPackets++;
            _log.Warn($"{player}: MOVEMENT с нагрузкой {packet.Payload.Length} байт вместо {MovementPayload.Size}.");
            SendError(player.EndPoint, "Некорректная нагрузка MOVEMENT.");
            return;
        }

        // Сервер авторитетен: обрезаем слишком большой шаг и держим игрока внутри арены.
        float dx = Math.Clamp(movement.Dx, -GameRules.MaxStep, GameRules.MaxStep);
        float dy = Math.Clamp(movement.Dy, -GameRules.MaxStep, GameRules.MaxStep);
        float dz = Math.Clamp(movement.Dz, -GameRules.MaxStep, GameRules.MaxStep);

        bool clamped = dx != movement.Dx || dy != movement.Dy || dz != movement.Dz;

        player.X = GameRules.Clamp(player.X + dx);
        player.Y = GameRules.Clamp(player.Y + dy);
        player.Z = GameRules.Clamp(player.Z + dz);

        _log.Command($"{player}: MOVEMENT seq={packet.Header.Sequence} " +
                     $"смещение ({movement.Dx:F2}; {movement.Dy:F2}; {movement.Dz:F2})" +
                     $"{(clamped ? " [обрезано]" : string.Empty)} " +
                     $"-> позиция ({player.X:F2}; {player.Y:F2}; {player.Z:F2})");

        Send(player.EndPoint, PacketType.StateUpdate, player.ToState(packet.Header.Sequence).ToBytes());
    }

    private void HandleShoot(Packet packet, PlayerSession player)
    {
        if (!ShootPayload.TryRead(packet.Payload, out var shoot))
        {
            _rejectedPackets++;
            _log.Warn($"{player}: SHOOT с нагрузкой {packet.Payload.Length} байт вместо {ShootPayload.Size}.");
            SendError(player.EndPoint, "Некорректная нагрузка SHOOT.");
            return;
        }

        string weapon = Weapons.Name(shoot.WeaponId);
        var now = DateTime.UtcNow;

        bool onCooldown = (now - player.LastShotUtc).TotalMilliseconds < GameRules.ShootCooldownMs;
        bool knownWeapon = Weapons.Damage(shoot.WeaponId) > 0;
        bool hasAmmo = player.Ammo > 0;
        bool fired = hasAmmo && !onCooldown && knownWeapon;

        bool hit = false;
        ushort damage = 0;

        if (fired)
        {
            player.Ammo--;
            player.LastShotUtc = now;

            hit = _random.NextDouble() < 0.5;           // имитация попадания
            damage = hit ? Weapons.Damage(shoot.WeaponId) : (ushort)0;
        }

        string outcome = !knownWeapon ? "оружие неизвестно"
                       : !hasAmmo ? "патроны кончились"
                       : onCooldown ? "оружие не готово (кулдаун)"
                       : hit ? $"попадание, урон {damage}"
                       : "промах";

        _log.Command($"{player}: SHOOT seq={packet.Header.Sequence} {weapon} " +
                     $"направление ({shoot.DirX:F2}; {shoot.DirY:F2}; {shoot.DirZ:F2}) " +
                     $"-> {outcome}, патронов осталось {player.Ammo}");

        var result = new ShootResultPayload(player.Id, shoot.WeaponId, hit, damage, player.Ammo,
                                            packet.Header.Sequence);
        Send(player.EndPoint, PacketType.ShootResult, result.ToBytes());
    }

    private void SendError(IPEndPoint target, string message) =>
        Send(target, PacketType.Error, new ErrorPayload(message).ToBytes());

    private void Send(IPEndPoint target, PacketType type, ReadOnlySpan<byte> payload)
    {
        var datagram = Packet.Build(type, ++_sequence, payload);

        try
        {
            _socket.Send(datagram, datagram.Length, target);
            _log.Packet($"{target} -> {type} seq={_sequence} size={payload.Length}");
        }
        catch (SocketException ex)
        {
            _log.Warn($"Не удалось отправить {type} на {target}: {ex.SocketErrorCode}");
        }
    }

    public void Dispose() => _socket.Dispose();
}
