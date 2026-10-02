using StickmanFight.Reliable.Protocol;

namespace StickmanFight.Reliable.Tests;

/// <summary>Тесты протокола версии 2: ACK, флаг requiresAck и проверки входных данных.</summary>
public class PacketCodecTests
{
    private static Packet Parse(byte[] bytes)
    {
        Assert.Equal(ParseError.None, PacketCodec.TryParse(bytes, out var packet));
        return packet!;
    }

    [Fact]
    public void Надёжный_SHOOT_сериализуется_с_флагом_и_разбирается_обратно()
    {
        var bytes = PacketCodec.SerializeShoot(300, requiresAck: true, new ShootPayload(2, 0.6f, 0f, -0.8f));

        var packet = Parse(bytes);

        Assert.Equal(PacketType.Shoot, packet.Header.Type);
        Assert.Equal(300, packet.Header.Sequence);
        Assert.True(packet.Header.RequiresAck);
        Assert.Equal(new ShootPayload(2, 0.6f, 0f, -0.8f), packet.Payload);
    }

    [Fact]
    public void Ненадёжный_MOVEMENT_разбирается_с_нулевым_флагом()
    {
        var bytes = PacketCodec.SerializeMovement(5, requiresAck: false, new MovementPayload(1.5f, 0f, -2.25f));

        var packet = Parse(bytes);

        Assert.False(packet.Header.RequiresAck);
        Assert.Equal(new MovementPayload(1.5f, 0f, -2.25f), packet.Payload);
    }

    [Fact]
    public void ACK_несёт_подтверждаемый_номер_и_не_требует_подтверждения()
    {
        var bytes = PacketCodec.SerializeAck(sequence: 9, acknowledgedSequence: 0x1234);

        var packet = Parse(bytes);

        Assert.Equal(PacketType.Ack, packet.Header.Type);
        Assert.False(packet.Header.RequiresAck);
        Assert.Equal(new AckPayload(0x1234), packet.Payload);
        Assert.Equal(0x12, bytes[8]);                   // acknowledgedSequence в сетевом порядке
        Assert.Equal(0x34, bytes[9]);
    }

    [Fact]
    public void PING_и_PONG_разбираются_как_в_ПР2()
    {
        var ping = Parse(PacketCodec.SerializePing(17, 987654321UL));
        var pong = Parse(PacketCodec.SerializePong(17, 1000, 1500, 1700));

        Assert.Equal(new PingPayload(987654321UL), ping.Payload);
        Assert.Equal(new PongPayload(1000, 1500, 1700), pong.Payload);
    }

    [Fact]
    public void Заголовок_занимает_8_байт_и_флаг_стоит_последним()
    {
        var bytes = PacketCodec.SerializeShoot(0x0102, requiresAck: true, new ShootPayload(1, 0, 0, 1));

        Assert.Equal(PacketCodec.HeaderSize + PacketCodec.ShootPayloadSize, bytes.Length);
        Assert.Equal((byte)PacketType.Shoot, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
        Assert.Equal(0x02, bytes[2]);
        Assert.Equal(0x00, bytes[3]);
        Assert.Equal(13, bytes[4]);                     // payloadSize
        Assert.Equal(0x00, bytes[5]);
        Assert.Equal(0x02, bytes[6]);                   // protocolVersion = 2
        Assert.Equal(0x01, bytes[7]);                   // requiresAck
    }

    [Fact]
    public void ACK_с_требованием_подтверждения_отвергается()
    {
        var bytes = PacketCodec.SerializeAck(1, 2);
        bytes[7] = 1;

        Assert.Equal(ParseError.InvalidAckFlag, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void PING_с_требованием_подтверждения_отвергается()
    {
        var bytes = PacketCodec.SerializePing(1, 2);
        bytes[7] = 1;

        Assert.Equal(ParseError.InvalidAckFlag, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void Флаг_requiresAck_больше_единицы_отвергается()
    {
        var bytes = PacketCodec.SerializeShoot(1, requiresAck: true, new ShootPayload(1, 0, 0, 1));
        bytes[7] = 2;

        Assert.Equal(ParseError.InvalidAckFlag, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void Пакет_версии_1_из_ПР2_отвергается()
    {
        var bytes = PacketCodec.SerializePing(1, 2);
        bytes[6] = 1;

        Assert.Equal(ParseError.UnsupportedVersion, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void Неизвестный_тип_отвергается()
    {
        var bytes = PacketCodec.SerializePing(1, 2);
        bytes[0] = 77;

        Assert.Equal(ParseError.UnknownType, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void Датаграмма_короче_заголовка_отвергается()
    {
        Assert.Equal(ParseError.TooShort, PacketCodec.TryParse(new byte[] { 4, 0, 1, 0, 13, 0, 2 }, out _));
        Assert.Equal(ParseError.TooShort, PacketCodec.TryParse(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void Обрезанный_SHOOT_отвергается()
    {
        var bytes = PacketCodec.SerializeShoot(1, requiresAck: true, new ShootPayload(1, 0, 0, 1));

        Assert.Equal(ParseError.PayloadSizeMismatch, PacketCodec.TryParse(bytes.AsSpan(0, bytes.Length - 1), out _));
    }

    [Fact]
    public void Лишние_байты_отвергаются()
    {
        var bytes = PacketCodec.SerializeAck(1, 2).Concat(new byte[] { 0 }).ToArray();

        Assert.Equal(ParseError.TrailingBytes, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void Неверный_payloadSize_в_заголовке_отвергается()
    {
        var bytes = PacketCodec.SerializeAck(1, 2);
        bytes[4] = 3;

        Assert.Equal(ParseError.PayloadSizeMismatch, PacketCodec.TryParse(bytes, out _));
    }

    [Fact]
    public void NaN_в_координатах_отвергается()
    {
        var bytes = PacketCodec.SerializeMovement(1, requiresAck: false, new MovementPayload(float.NaN, 0, 0));

        Assert.Equal(ParseError.InvalidValue, PacketCodec.TryParse(bytes, out _));
    }

    [Theory]
    [InlineData(PacketType.Movement, true)]
    [InlineData(PacketType.Shoot, true)]
    [InlineData(PacketType.Ping, false)]
    [InlineData(PacketType.Pong, false)]
    [InlineData(PacketType.Ack, false)]
    public void Подтверждение_разрешено_только_игровым_командам(PacketType type, bool allowed)
    {
        Assert.Equal(allowed, PacketCodec.AckAllowed(type));
    }
}
