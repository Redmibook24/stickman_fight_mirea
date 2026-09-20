namespace StickmanFight.Latency.Protocol;

/// <summary>Причина, по которой датаграмма не является корректным пакетом.</summary>
public enum ParseError
{
    None = 0,
    TooShort,            // не хватает байт даже на заголовок
    UnknownType,         // packetType не из протокола
    UnexpectedType,      // тип корректен, но ожидался другой (PING вместо PONG и наоборот)
    UnsupportedVersion,  // protocolVersion не равен текущей версии
    PayloadSizeMismatch, // payloadSize не совпадает с фактической длиной данных
    TrailingBytes,       // в датаграмме остались лишние байты после нагрузки
}

/// <summary>Заголовок пакета: 7 байт, big-endian.</summary>
/// <param name="Type">packetType, 1 байт.</param>
/// <param name="Sequence">sequenceNumber, 2 байта.</param>
/// <param name="PayloadSize">payloadSize, 2 байта.</param>
/// <param name="Version">protocolVersion, 2 байта.</param>
public readonly record struct PacketHeader(PacketType Type, ushort Sequence, ushort PayloadSize, ushort Version);

/// <summary>PING: заголовок + время отправки по часам клиента (мкс).</summary>
public readonly record struct PingPacket(ushort Sequence, ulong ClientSendTimeUs);

/// <summary>
/// PONG: эхо времени клиента + две отметки сервера.
/// Часы клиента и сервера не синхронизированы, поэтому отметки сервера нужны
/// только для оценки времени обработки, а RTT считается по часам клиента.
/// </summary>
public readonly record struct PongPacket(
    ushort Sequence,
    ulong ClientSendTimeUs,
    ulong ServerReceiveTimeUs,
    ulong ServerSendTimeUs)
{
    /// <summary>Сколько времени пакет пролежал на сервере, мкс.</summary>
    public ulong ServerProcessingTimeUs =>
        ServerSendTimeUs >= ServerReceiveTimeUs ? ServerSendTimeUs - ServerReceiveTimeUs : 0;
}

/// <summary>
/// Сериализация и разбор пакетов PING/PONG. Модуль не знает ни про сокеты,
/// ни про метрики: на вход байты, на выход структуры (и наоборот).
/// </summary>
public static class PacketCodec
{
    public const ushort ProtocolVersion = 1;

    public const int HeaderSize = 7;          // 1 + 2 + 2 + 2
    public const int PingPayloadSize = 8;     // clientSendTimeUs
    public const int PongPayloadSize = 24;    // clientSendTimeUs + serverReceiveTimeUs + serverSendTimeUs
    public const int PingPacketSize = HeaderSize + PingPayloadSize;   // 15
    public const int PongPacketSize = HeaderSize + PongPayloadSize;   // 31

    public static byte[] SerializePing(ushort sequence, ulong clientSendTimeUs)
    {
        var output = new List<byte>(PingPacketSize);

        WriteHeader(output, PacketType.Ping, sequence, PingPayloadSize);
        BinaryCodec.WriteU64(output, clientSendTimeUs);

        return output.ToArray();
    }

    public static byte[] SerializePong(ushort sequence, ulong clientSendTimeUs,
                                       ulong serverReceiveTimeUs, ulong serverSendTimeUs)
    {
        var output = new List<byte>(PongPacketSize);

        WriteHeader(output, PacketType.Pong, sequence, PongPayloadSize);
        BinaryCodec.WriteU64(output, clientSendTimeUs);
        BinaryCodec.WriteU64(output, serverReceiveTimeUs);
        BinaryCodec.WriteU64(output, serverSendTimeUs);

        return output.ToArray();
    }

    public static byte[] SerializePong(PongPacket packet) =>
        SerializePong(packet.Sequence, packet.ClientSendTimeUs,
                      packet.ServerReceiveTimeUs, packet.ServerSendTimeUs);

    /// <summary>Читает только заголовок — по нему получатель решает, как разбирать остальное.</summary>
    public static ParseError TryParseHeader(ReadOnlySpan<byte> datagram, out PacketHeader header)
    {
        header = default;
        int offset = 0;

        if (!BinaryCodec.TryReadU8(datagram, ref offset, out byte rawType) ||
            !BinaryCodec.TryReadU16(datagram, ref offset, out ushort sequence) ||
            !BinaryCodec.TryReadU16(datagram, ref offset, out ushort payloadSize) ||
            !BinaryCodec.TryReadU16(datagram, ref offset, out ushort version))
            return ParseError.TooShort;

        if (!Enum.IsDefined(typeof(PacketType), rawType))
            return ParseError.UnknownType;

        if (version != ProtocolVersion)
            return ParseError.UnsupportedVersion;

        header = new PacketHeader((PacketType)rawType, sequence, payloadSize, version);
        return ParseError.None;
    }

    public static ParseError TryParsePing(ReadOnlySpan<byte> datagram, out PingPacket packet)
    {
        packet = default;

        var error = ValidateBody(datagram, PacketType.Ping, PingPayloadSize, out var header);
        if (error != ParseError.None) return error;

        int offset = HeaderSize;
        BinaryCodec.TryReadU64(datagram, ref offset, out ulong clientSendTimeUs);

        packet = new PingPacket(header.Sequence, clientSendTimeUs);
        return ParseError.None;
    }

    public static ParseError TryParsePong(ReadOnlySpan<byte> datagram, out PongPacket packet)
    {
        packet = default;

        var error = ValidateBody(datagram, PacketType.Pong, PongPayloadSize, out var header);
        if (error != ParseError.None) return error;

        int offset = HeaderSize;
        BinaryCodec.TryReadU64(datagram, ref offset, out ulong clientSendTimeUs);
        BinaryCodec.TryReadU64(datagram, ref offset, out ulong serverReceiveTimeUs);
        BinaryCodec.TryReadU64(datagram, ref offset, out ulong serverSendTimeUs);

        packet = new PongPacket(header.Sequence, clientSendTimeUs, serverReceiveTimeUs, serverSendTimeUs);
        return ParseError.None;
    }

    public static string Describe(ParseError error) => error switch
    {
        ParseError.None                => "пакет корректен",
        ParseError.TooShort            => "датаграмма короче заголовка",
        ParseError.UnknownType         => "неизвестный packetType",
        ParseError.UnexpectedType      => "неожиданный тип пакета",
        ParseError.UnsupportedVersion  => "неподдерживаемая версия протокола",
        ParseError.PayloadSizeMismatch => "payloadSize не совпадает с длиной данных",
        ParseError.TrailingBytes       => "лишние байты в конце датаграммы",
        _                              => "неизвестная ошибка разбора",
    };

    private static void WriteHeader(List<byte> output, PacketType type, ushort sequence, ushort payloadSize)
    {
        BinaryCodec.WriteU8(output, (byte)type);
        BinaryCodec.WriteU16(output, sequence);
        BinaryCodec.WriteU16(output, payloadSize);
        BinaryCodec.WriteU16(output, ProtocolVersion);
    }

    /// <summary>Общие проверки: тип, заявленный размер нагрузки и отсутствие «хвоста».</summary>
    private static ParseError ValidateBody(ReadOnlySpan<byte> datagram, PacketType expected,
                                           int expectedPayloadSize, out PacketHeader header)
    {
        var error = TryParseHeader(datagram, out header);
        if (error != ParseError.None) return error;

        if (header.Type != expected)
            return ParseError.UnexpectedType;

        if (header.PayloadSize != expectedPayloadSize)
            return ParseError.PayloadSizeMismatch;

        int actualPayload = datagram.Length - HeaderSize;
        if (actualPayload < expectedPayloadSize)
            return ParseError.PayloadSizeMismatch;

        if (actualPayload > expectedPayloadSize)
            return ParseError.TrailingBytes;

        return ParseError.None;
    }
}
