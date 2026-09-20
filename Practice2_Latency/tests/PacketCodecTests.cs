using StickmanFight.Latency.Protocol;

namespace StickmanFight.Latency.Tests;

/// <summary>Тесты сериализации: корректные пакеты и все виды недействительных датаграмм.</summary>
public class PacketCodecTests
{
    [Fact]
    public void Ping_сериализуется_и_разбирается_обратно()
    {
        var bytes = PacketCodec.SerializePing(17, 987654321UL);

        var error = PacketCodec.TryParsePing(bytes, out var ping);

        Assert.Equal(ParseError.None, error);
        Assert.Equal(17, ping.Sequence);
        Assert.Equal(987654321UL, ping.ClientSendTimeUs);
    }

    [Fact]
    public void Pong_сериализуется_и_разбирается_обратно()
    {
        var bytes = PacketCodec.SerializePong(42, 1000UL, 1500UL, 1700UL);

        var error = PacketCodec.TryParsePong(bytes, out var pong);

        Assert.Equal(ParseError.None, error);
        Assert.Equal(42, pong.Sequence);
        Assert.Equal(1000UL, pong.ClientSendTimeUs);
        Assert.Equal(1500UL, pong.ServerReceiveTimeUs);
        Assert.Equal(1700UL, pong.ServerSendTimeUs);
        Assert.Equal(200UL, pong.ServerProcessingTimeUs);
    }

    [Fact]
    public void Размеры_пакетов_соответствуют_протоколу()
    {
        Assert.Equal(15, PacketCodec.SerializePing(1, 1).Length);
        Assert.Equal(31, PacketCodec.SerializePong(1, 1, 2, 3).Length);
    }

    [Fact]
    public void Заголовок_пишется_в_сетевом_порядке_байт()
    {
        var bytes = PacketCodec.SerializePing(0x0102, 0x1122334455667788UL);

        Assert.Equal((byte)PacketType.Ping, bytes[0]);
        Assert.Equal(0x01, bytes[1]);                       // старший байт sequenceNumber
        Assert.Equal(0x02, bytes[2]);                       // младший байт sequenceNumber
        Assert.Equal(0x00, bytes[3]);                       // payloadSize = 8
        Assert.Equal(0x08, bytes[4]);
        Assert.Equal(0x00, bytes[5]);                       // protocolVersion = 1
        Assert.Equal(0x01, bytes[6]);
        Assert.Equal(0x11, bytes[7]);                       // старший байт clientSendTimeUs
        Assert.Equal(0x88, bytes[14]);                      // младший байт clientSendTimeUs
    }

    [Fact]
    public void Обрезанный_Ping_отвергается()
    {
        var bytes = PacketCodec.SerializePing(17, 100);

        var error = PacketCodec.TryParsePing(bytes.AsSpan(0, bytes.Length - 1), out _);

        Assert.Equal(ParseError.PayloadSizeMismatch, error);
    }

    [Fact]
    public void Пустая_датаграмма_отвергается()
    {
        Assert.Equal(ParseError.TooShort, PacketCodec.TryParsePing(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void Датаграмма_короче_заголовка_отвергается()
    {
        Assert.Equal(ParseError.TooShort, PacketCodec.TryParsePing(new byte[] { 1, 0, 17 }, out _));
    }

    [Fact]
    public void Лишние_байты_в_конце_отвергаются()
    {
        var bytes = PacketCodec.SerializePing(5, 50).Concat(new byte[] { 0xFF }).ToArray();

        Assert.Equal(ParseError.TrailingBytes, PacketCodec.TryParsePing(bytes, out _));
    }

    [Fact]
    public void Неизвестный_тип_пакета_отвергается()
    {
        var bytes = PacketCodec.SerializePing(5, 50);
        bytes[0] = 99;

        Assert.Equal(ParseError.UnknownType, PacketCodec.TryParsePing(bytes, out _));
    }

    [Fact]
    public void Чужой_тип_пакета_отвергается()
    {
        var pong = PacketCodec.SerializePong(5, 1, 2, 3);

        Assert.Equal(ParseError.UnexpectedType, PacketCodec.TryParsePing(pong, out _));
    }

    [Fact]
    public void Неподдерживаемая_версия_протокола_отвергается()
    {
        var bytes = PacketCodec.SerializePing(5, 50);
        bytes[5] = 0;
        bytes[6] = 2;                                        // protocolVersion = 2

        Assert.Equal(ParseError.UnsupportedVersion, PacketCodec.TryParsePing(bytes, out _));
    }

    [Fact]
    public void Неверный_payloadSize_отвергается()
    {
        var bytes = PacketCodec.SerializePing(5, 50);
        bytes[3] = 0;
        bytes[4] = 7;                                        // payloadSize = 7 вместо 8

        Assert.Equal(ParseError.PayloadSizeMismatch, PacketCodec.TryParsePing(bytes, out _));
    }

    [Fact]
    public void Заголовок_читается_отдельно_от_нагрузки()
    {
        var bytes = PacketCodec.SerializePong(7, 1, 2, 3);

        var error = PacketCodec.TryParseHeader(bytes, out var header);

        Assert.Equal(ParseError.None, error);
        Assert.Equal(PacketType.Pong, header.Type);
        Assert.Equal(7, header.Sequence);
        Assert.Equal(PacketCodec.PongPayloadSize, header.PayloadSize);
        Assert.Equal(PacketCodec.ProtocolVersion, header.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ushort.MaxValue)]
    public void Граничные_номера_пакетов_переживают_сериализацию(ushort sequence)
    {
        var bytes = PacketCodec.SerializePing(sequence, ulong.MaxValue);

        Assert.Equal(ParseError.None, PacketCodec.TryParsePing(bytes, out var ping));
        Assert.Equal(sequence, ping.Sequence);
        Assert.Equal(ulong.MaxValue, ping.ClientSendTimeUs);
    }
}
