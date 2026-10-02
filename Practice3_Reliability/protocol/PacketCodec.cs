namespace StickmanFight.Reliable.Protocol;

/// <summary>Причина, по которой датаграмма не является корректным пакетом.</summary>
public enum ParseError
{
    None = 0,
    TooShort,            // не хватает байт даже на заголовок
    UnknownType,         // packetType не из протокола
    UnsupportedVersion,  // protocolVersion не равен текущей версии
    InvalidAckFlag,      // requiresAck не 0/1 или выставлен у типа, которому подтверждение запрещено
    PayloadSizeMismatch, // payloadSize не совпадает с протоколом или с фактической длиной данных
    TrailingBytes,       // в датаграмме остались лишние байты после нагрузки
    InvalidValue,        // поле прочитано, но значение недопустимо (NaN, бесконечность)
}

/// <summary>
/// Сборка и разбор пакетов протокола версии 2. Модуль не знает ни про сокеты,
/// ни про надёжную доставку: на вход байты, на выход структуры и наоборот.
///
/// Отличия от версии 1 (ПР №2): в заголовок добавлен байт requiresAck,
/// появились пакеты MOVEMENT, SHOOT и ACK.
/// </summary>
public static class PacketCodec
{
    public const ushort ProtocolVersion = 2;

    public const int HeaderSize = 8;            // 1 + 2 + 2 + 2 + 1
    public const int PingPayloadSize = 8;       // clientSendTimeUs
    public const int PongPayloadSize = 24;      // clientSendTimeUs + serverReceiveTimeUs + serverSendTimeUs
    public const int MovementPayloadSize = 12;  // dx, dy, dz
    public const int ShootPayloadSize = 13;     // weaponId + dirX, dirY, dirZ
    public const int AckPayloadSize = 2;        // acknowledgedSequence

    public static byte[] SerializePing(ushort sequence, ulong clientSendTimeUs)
    {
        var output = StartPacket(PacketType.Ping, sequence, PingPayloadSize, requiresAck: false);
        BinaryCodec.WriteU64(output, clientSendTimeUs);
        return output.ToArray();
    }

    /// <summary>Номер в заголовке PONG повторяет номер PING, как и в ПР №2.</summary>
    public static byte[] SerializePong(ushort pingSequence, ulong clientSendTimeUs,
                                       ulong serverReceiveTimeUs, ulong serverSendTimeUs)
    {
        var output = StartPacket(PacketType.Pong, pingSequence, PongPayloadSize, requiresAck: false);
        BinaryCodec.WriteU64(output, clientSendTimeUs);
        BinaryCodec.WriteU64(output, serverReceiveTimeUs);
        BinaryCodec.WriteU64(output, serverSendTimeUs);
        return output.ToArray();
    }

    public static byte[] SerializeMovement(ushort sequence, bool requiresAck, MovementPayload movement)
    {
        var output = StartPacket(PacketType.Movement, sequence, MovementPayloadSize, requiresAck);
        BinaryCodec.WriteF32(output, movement.Dx);
        BinaryCodec.WriteF32(output, movement.Dy);
        BinaryCodec.WriteF32(output, movement.Dz);
        return output.ToArray();
    }

    public static byte[] SerializeShoot(ushort sequence, bool requiresAck, ShootPayload shoot)
    {
        var output = StartPacket(PacketType.Shoot, sequence, ShootPayloadSize, requiresAck);
        BinaryCodec.WriteU8(output, shoot.WeaponId);
        BinaryCodec.WriteF32(output, shoot.DirX);
        BinaryCodec.WriteF32(output, shoot.DirY);
        BinaryCodec.WriteF32(output, shoot.DirZ);
        return output.ToArray();
    }

    /// <summary>ACK никогда не требует подтверждения: иначе ACK на ACK шли бы бесконечно.</summary>
    public static byte[] SerializeAck(ushort sequence, ushort acknowledgedSequence)
    {
        var output = StartPacket(PacketType.Ack, sequence, AckPayloadSize, requiresAck: false);
        BinaryCodec.WriteU16(output, acknowledgedSequence);
        return output.ToArray();
    }

    /// <summary>
    /// Разбирает датаграмму. Длина, тип, версия, флаг подтверждения и размер нагрузки
    /// проверяются до чтения полей нагрузки.
    /// </summary>
    public static ParseError TryParse(ReadOnlySpan<byte> datagram, out Packet? packet)
    {
        packet = null;

        var error = TryParseHeader(datagram, out var header);
        if (error != ParseError.None) return error;

        if (!AckAllowed(header.Type) && header.RequiresAck)
            return ParseError.InvalidAckFlag;

        int expectedPayload = ExpectedPayloadSize(header.Type);
        if (header.PayloadSize != expectedPayload)
            return ParseError.PayloadSizeMismatch;

        int actualPayload = datagram.Length - HeaderSize;
        if (actualPayload < expectedPayload) return ParseError.PayloadSizeMismatch;
        if (actualPayload > expectedPayload) return ParseError.TrailingBytes;

        int offset = HeaderSize;
        Payload? payload = header.Type switch
        {
            PacketType.Ping     => ReadPing(datagram, ref offset),
            PacketType.Pong     => ReadPong(datagram, ref offset),
            PacketType.Movement => ReadMovement(datagram, ref offset),
            PacketType.Shoot    => ReadShoot(datagram, ref offset),
            PacketType.Ack      => ReadAck(datagram, ref offset),
            _                   => null,
        };

        if (payload is null) return ParseError.InvalidValue;

        packet = new Packet(header, payload);
        return ParseError.None;
    }

    /// <summary>Читает только заголовок.</summary>
    public static ParseError TryParseHeader(ReadOnlySpan<byte> datagram, out PacketHeader header)
    {
        header = default;
        int offset = 0;

        if (!BinaryCodec.TryReadU8(datagram, ref offset, out byte rawType) ||
            !BinaryCodec.TryReadU16(datagram, ref offset, out ushort sequence) ||
            !BinaryCodec.TryReadU16(datagram, ref offset, out ushort payloadSize) ||
            !BinaryCodec.TryReadU16(datagram, ref offset, out ushort version) ||
            !BinaryCodec.TryReadU8(datagram, ref offset, out byte requiresAck))
            return ParseError.TooShort;

        if (!Enum.IsDefined(typeof(PacketType), rawType))
            return ParseError.UnknownType;

        if (version != ProtocolVersion)
            return ParseError.UnsupportedVersion;

        if (requiresAck > 1)
            return ParseError.InvalidAckFlag;

        header = new PacketHeader((PacketType)rawType, sequence, payloadSize, version, requiresAck == 1);
        return ParseError.None;
    }

    /// <summary>Каким типам разрешено требовать подтверждение.</summary>
    public static bool AckAllowed(PacketType type) => type is PacketType.Movement or PacketType.Shoot;

    public static int ExpectedPayloadSize(PacketType type) => type switch
    {
        PacketType.Ping     => PingPayloadSize,
        PacketType.Pong     => PongPayloadSize,
        PacketType.Movement => MovementPayloadSize,
        PacketType.Shoot    => ShootPayloadSize,
        PacketType.Ack      => AckPayloadSize,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Неизвестный тип пакета."),
    };

    public static string Describe(ParseError error) => error switch
    {
        ParseError.None                => "пакет корректен",
        ParseError.TooShort            => "датаграмма короче заголовка",
        ParseError.UnknownType         => "неизвестный packetType",
        ParseError.UnsupportedVersion  => "неподдерживаемая версия протокола",
        ParseError.InvalidAckFlag      => "недопустимое значение requiresAck для этого типа",
        ParseError.PayloadSizeMismatch => "payloadSize не совпадает с длиной данных",
        ParseError.TrailingBytes       => "лишние байты в конце датаграммы",
        ParseError.InvalidValue        => "недопустимое значение поля",
        _                              => "неизвестная ошибка разбора",
    };

    private static List<byte> StartPacket(PacketType type, ushort sequence, int payloadSize, bool requiresAck)
    {
        var output = new List<byte>(HeaderSize + payloadSize);
        BinaryCodec.WriteU8(output, (byte)type);
        BinaryCodec.WriteU16(output, sequence);
        BinaryCodec.WriteU16(output, (ushort)payloadSize);
        BinaryCodec.WriteU16(output, ProtocolVersion);
        BinaryCodec.WriteU8(output, requiresAck ? (byte)1 : (byte)0);
        return output;
    }

    // Длина уже проверена в TryParse, поэтому чтение полей здесь не может выйти за буфер.

    private static PingPayload ReadPing(ReadOnlySpan<byte> data, ref int offset)
    {
        BinaryCodec.TryReadU64(data, ref offset, out ulong sent);
        return new PingPayload(sent);
    }

    private static PongPayload ReadPong(ReadOnlySpan<byte> data, ref int offset)
    {
        BinaryCodec.TryReadU64(data, ref offset, out ulong clientSend);
        BinaryCodec.TryReadU64(data, ref offset, out ulong serverReceive);
        BinaryCodec.TryReadU64(data, ref offset, out ulong serverSend);
        return new PongPayload(clientSend, serverReceive, serverSend);
    }

    private static MovementPayload? ReadMovement(ReadOnlySpan<byte> data, ref int offset)
    {
        BinaryCodec.TryReadF32(data, ref offset, out float dx);
        BinaryCodec.TryReadF32(data, ref offset, out float dy);
        BinaryCodec.TryReadF32(data, ref offset, out float dz);

        return float.IsFinite(dx) && float.IsFinite(dy) && float.IsFinite(dz)
            ? new MovementPayload(dx, dy, dz)
            : null;
    }

    private static ShootPayload? ReadShoot(ReadOnlySpan<byte> data, ref int offset)
    {
        BinaryCodec.TryReadU8(data, ref offset, out byte weapon);
        BinaryCodec.TryReadF32(data, ref offset, out float x);
        BinaryCodec.TryReadF32(data, ref offset, out float y);
        BinaryCodec.TryReadF32(data, ref offset, out float z);

        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            ? new ShootPayload(weapon, x, y, z)
            : null;
    }

    private static AckPayload ReadAck(ReadOnlySpan<byte> data, ref int offset)
    {
        BinaryCodec.TryReadU16(data, ref offset, out ushort acknowledged);
        return new AckPayload(acknowledged);
    }
}
