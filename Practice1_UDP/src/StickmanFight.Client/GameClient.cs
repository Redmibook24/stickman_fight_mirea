using System.Net.Sockets;

namespace StickmanFight.Net.Client;

/// <summary>
/// UDP-клиент: выполняет рукопожатие с сервером, затем с заданной периодичностью
/// шлёт команды MOVEMENT и SHOOT и печатает ответы сервера.
/// </summary>
public sealed class GameClient : IDisposable
{
    private readonly UdpClient _socket = new();
    private readonly string _host;
    private readonly int _port;
    private readonly string _playerName;
    private readonly Random _random = new();

    private ushort _sequence;     // порядковый номер исходящих пакетов
    private ushort _playerId;
    private int _sent;
    private int _received;

    public GameClient(string host, int port, string playerName)
    {
        _host = host;
        _port = port;
        _playerName = playerName;

        _socket.Connect(host, port);  // фиксируем удалённый адрес: дальше можно Send/Receive без EndPoint
    }

    /// <summary>Рукопожатие: CONNECT -> CONNECT_ACK, до пяти попыток с таймаутом в секунду.</summary>
    public async Task<bool> HandshakeAsync(CancellationToken token)
    {
        for (int attempt = 1; attempt <= 5 && !token.IsCancellationRequested; attempt++)
        {
            var payload = new ConnectPayload(_playerName).ToBytes();
            Send(PacketType.Connect, payload);
            Print(ConsoleColor.DarkGray,
                  $"-> CONNECT seq={_sequence} имя=«{_playerName}» (попытка {attempt}/5)");

            var packet = await ReceiveWithTimeoutAsync(TimeSpan.FromSeconds(1), token);

            if (packet is null)
            {
                Print(ConsoleColor.Yellow, "   ответа нет, повторяем CONNECT...");
                continue;
            }

            if (packet.Header.Type == PacketType.Error)
            {
                Print(ConsoleColor.Red, $"<- ERROR: {ErrorPayload.Read(packet.Payload).Message}");
                return false;
            }

            if (packet.Header.Type == PacketType.ConnectAck &&
                StatePayload.TryRead(packet.Payload, out var state))
            {
                _playerId = state.PlayerId;
                Print(ConsoleColor.Green,
                      $"<- CONNECT_ACK: рукопожатие выполнено. id={state.PlayerId}, " +
                      $"позиция ({state.X:F2}; {state.Y:F2}; {state.Z:F2}), HP={state.Hp}, патроны={state.Ammo}");
                return true;
            }

            Print(ConsoleColor.Yellow, $"<- неожиданный ответ {packet.Header.Type}, ждём CONNECT_ACK");
        }

        return false;
    }

    /// <summary>Основной цикл: команда раз в <paramref name="interval"/>, ответы читаются параллельно.</summary>
    public async Task RunAsync(TimeSpan interval, int commandLimit, CancellationToken token)
    {
        var receiving = Task.Run(() => ReceiveLoopAsync(token), token);

        int tick = 0;
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (tick % 2 == 0) SendMovement();
                else SendShoot();

                tick++;
                if (commandLimit > 0 && tick >= commandLimit) break;
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение по Ctrl+C
        }

        // даём серверу время ответить на последнюю команду
        await Task.WhenAny(receiving, Task.Delay(300, CancellationToken.None));
    }

    private void SendMovement()
    {
        // Каждая четвёртая команда — заведомо слишком большой шаг: видно, как сервер его обрезает.
        bool cheat = _sequence % 8 == 7;
        float dx = cheat ? 42f : (float)Math.Round(_random.NextDouble() * 3 - 1.5, 2);
        float dz = cheat ? 42f : (float)Math.Round(_random.NextDouble() * 3 - 1.5, 2);

        var payload = new MovementPayload(dx, 0f, dz).ToBytes();
        Send(PacketType.Movement, payload);

        Print(ConsoleColor.DarkCyan,
              $"-> MOVEMENT seq={_sequence} смещение ({dx:F2}; 0.00; {dz:F2}){(cheat ? " [намеренно завышено]" : string.Empty)}");
    }

    private void SendShoot()
    {
        byte weapon = (byte)_random.Next(1, 4);       // 1 — винтовка, 2 — гранатомёт, 3 — ближний бой
        double angle = _random.NextDouble() * Math.PI * 2;

        float dirX = (float)Math.Round(Math.Cos(angle), 3);
        float dirZ = (float)Math.Round(Math.Sin(angle), 3);

        var payload = new ShootPayload(weapon, dirX, 0f, dirZ).ToBytes();
        Send(PacketType.Shoot, payload);

        Print(ConsoleColor.DarkYellow,
              $"-> SHOOT seq={_sequence} {Weapons.Name(weapon)} направление ({dirX:F2}; 0.00; {dirZ:F2})");
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var packet = await ReceiveWithTimeoutAsync(Timeout.InfiniteTimeSpan, token);
            if (packet is null)
            {
                // сервер недоступен или пакет битый — чуть подождём, чтобы не крутить цикл впустую
                try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
                continue;
            }

            PrintServerAnswer(packet);
        }
    }

    private void PrintServerAnswer(Packet packet)
    {
        switch (packet.Header.Type)
        {
            case PacketType.StateUpdate when StatePayload.TryRead(packet.Payload, out var state):
                Print(ConsoleColor.Cyan,
                      $"<- STATE_UPDATE (ack seq={state.AckSequence}): позиция " +
                      $"({state.X:F2}; {state.Y:F2}; {state.Z:F2}), HP={state.Hp}, патроны={state.Ammo}");
                break;

            case PacketType.ShootResult when ShootResultPayload.TryRead(packet.Payload, out var shot):
                Print(ConsoleColor.Yellow,
                      $"<- SHOOT_RESULT (ack seq={shot.AckSequence}): {Weapons.Name(shot.WeaponId)} — " +
                      $"{(shot.Hit ? $"попадание, урон {shot.Damage}" : "промах")}, патронов {shot.AmmoLeft}");
                break;

            case PacketType.Error:
                Print(ConsoleColor.Red, $"<- ERROR: {ErrorPayload.Read(packet.Payload).Message}");
                break;

            default:
                Print(ConsoleColor.DarkGray, $"<- {packet.Header}");
                break;
        }
    }

    private async Task<Packet?> ReceiveWithTimeoutAsync(TimeSpan timeout, CancellationToken token)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);

        try
        {
            var result = await _socket.ReceiveAsync(linked.Token);
            _received++;

            var parseResult = Packet.TryParse(result.Buffer, out var packet);
            if (parseResult != ParseResult.Ok || packet is null)
            {
                Print(ConsoleColor.Red, $"<- пакет отброшен: {Packet.Describe(parseResult)}");
                return null;
            }

            return packet;
        }
        catch (OperationCanceledException)
        {
            return null;      // таймаут или остановка клиента
        }
        catch (SocketException ex)
        {
            // ICMP «port unreachable» прилетает, когда сервер не запущен
            Print(ConsoleColor.Red, $"<- сокет: {ex.SocketErrorCode} — сервер {_host}:{_port} недоступен?");
            return null;
        }
    }

    public void SendDisconnect()
    {
        try
        {
            Send(PacketType.Disconnect, ReadOnlySpan<byte>.Empty);
            Print(ConsoleColor.DarkGray, $"-> DISCONNECT seq={_sequence}");
        }
        catch (SocketException)
        {
            // сервер мог уже закрыться — выходим молча
        }
    }

    private void Send(PacketType type, ReadOnlySpan<byte> payload)
    {
        var datagram = Packet.Build(type, ++_sequence, payload);
        _socket.Send(datagram, datagram.Length);
        _sent++;
    }

    private static void Print(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.ForegroundColor = previous;
    }

    public void PrintSummary() =>
        Print(ConsoleColor.White, $"Итого: отправлено пакетов {_sent}, получено {_received}, id игрока {_playerId}.");

    public void Dispose() => _socket.Dispose();
}
