using System.Net.Sockets;
using StickmanFight.Latency.Protocol;
using StickmanFight.Latency.Telemetry;
using StickmanFight.Latency.Transport;

namespace StickmanFight.Latency.Server;

/// <summary>
/// Сервер PING/PONG. Отвечает на каждый корректный PING, подставляя две свои отметки
/// времени, и отбрасывает всё, что не прошло проверку протокола.
/// Работа с байтами делегирована транспорту, разбор — модулю protocol.
/// </summary>
public sealed class PongServer
{
    private readonly IUdpTransport _transport;
    private readonly IClock _clock;
    private readonly bool _verbose;

    public PongServer(IUdpTransport transport, IClock clock, bool verbose = true)
    {
        _transport = transport;
        _clock = clock;
        _verbose = verbose;
    }

    public int ValidPings { get; private set; }
    public int RejectedPackets { get; private set; }

    public async Task RunAsync(CancellationToken token)
    {
        Console.WriteLine($"[{Timestamp()}] Сервер слушает {_transport.LocalEndPoint}, ждёт PING.");

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
                Console.WriteLine($"[{Timestamp()}] Ошибка сокета: {ex.SocketErrorCode}");
                continue;
            }

            // Отметку времени берём сразу после приёма, до любого разбора.
            ulong receiveUs = _clock.NowUs;
            HandleDatagram(datagram, receiveUs);
        }

        Console.WriteLine($"[{Timestamp()}] Остановка. Обработано PING: {ValidPings}, отброшено пакетов: {RejectedPackets}.");
    }

    private void HandleDatagram(Datagram datagram, ulong receiveUs)
    {
        var error = PacketCodec.TryParsePing(datagram.Data, out var ping);
        if (error != ParseError.None)
        {
            RejectedPackets++;
            Console.WriteLine($"[{Timestamp()}] Пакет от {datagram.RemoteEndPoint} отброшен: " +
                              $"{PacketCodec.Describe(error)} ({datagram.Data.Length} байт).");
            return;
        }

        ValidPings++;

        var pong = PacketCodec.SerializePong(ping.Sequence, ping.ClientSendTimeUs, receiveUs, _clock.NowUs);
        _transport.Send(pong, datagram.RemoteEndPoint);

        if (_verbose)
            Console.WriteLine($"[{Timestamp()}] PING seq={ping.Sequence} от {datagram.RemoteEndPoint} -> PONG.");
    }

    private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss.fff");
}
