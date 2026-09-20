using System.Net;
using System.Net.Sockets;

namespace StickmanFight.Latency.Transport;

/// <summary>Обычный UDP-сокет (System.Net.Sockets поверх Berkeley Sockets).</summary>
public sealed class UdpSocketTransport : IUdpTransport
{
    private readonly UdpClient _socket;

    /// <param name="localPort">0 — взять свободный порт, назначенный системой.</param>
    public UdpSocketTransport(int localPort = 0)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));

        // Windows: иначе после ICMP «port unreachable» приём падает с WSAECONNRESET (10054).
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
        }

        LocalEndPoint = (IPEndPoint)_socket.Client.LocalEndPoint!;
    }

    public IPEndPoint LocalEndPoint { get; }

    public void Send(ReadOnlySpan<byte> data, IPEndPoint target) => _socket.Send(data, target);

    public async Task<Datagram> ReceiveAsync(CancellationToken token)
    {
        var result = await _socket.ReceiveAsync(token);
        return new Datagram(result.Buffer, result.RemoteEndPoint);
    }

    public void Dispose() => _socket.Dispose();
}
