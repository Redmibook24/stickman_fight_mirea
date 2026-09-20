using System.Net;

namespace StickmanFight.Latency.Transport;

/// <summary>Принятая датаграмма вместе с адресом отправителя.</summary>
public readonly record struct Datagram(byte[] Data, IPEndPoint RemoteEndPoint);

/// <summary>
/// Транспорт: только байты и адреса. Ни сериализации, ни метрик —
/// поэтому его можно подменить эмулятором сети или заглушкой в тестах.
/// </summary>
public interface IUdpTransport : IDisposable
{
    /// <summary>Локальный адрес, на котором открыт сокет.</summary>
    IPEndPoint LocalEndPoint { get; }

    void Send(ReadOnlySpan<byte> data, IPEndPoint target);

    Task<Datagram> ReceiveAsync(CancellationToken token);
}
