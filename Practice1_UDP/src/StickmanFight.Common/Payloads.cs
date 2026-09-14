using System.Buffers.Binary;
using System.Text;

namespace StickmanFight.Net;

/// <summary>CONNECT: имя игрока (1 байт длины + UTF-8).</summary>
public readonly record struct ConnectPayload(string PlayerName)
{
    public byte[] ToBytes()
    {
        var name = Encoding.UTF8.GetBytes(PlayerName);
        if (name.Length > 32) name = name[..32];

        var buffer = new byte[1 + name.Length];
        buffer[0] = (byte)name.Length;
        name.CopyTo(buffer, 1);
        return buffer;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ConnectPayload value)
    {
        value = default;
        if (payload.Length < 1 || payload.Length < 1 + payload[0]) return false;

        value = new ConnectPayload(Encoding.UTF8.GetString(payload.Slice(1, payload[0])));
        return true;
    }
}

/// <summary>MOVEMENT: желаемое смещение игрока за тик, три float (12 байт).</summary>
public readonly record struct MovementPayload(float Dx, float Dy, float Dz)
{
    public const int Size = 12;

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(0), Dx);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), Dy);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(8), Dz);
        return buffer;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out MovementPayload value)
    {
        value = default;
        if (payload.Length != Size) return false;

        value = new MovementPayload(
            BinaryPrimitives.ReadSingleLittleEndian(payload),
            BinaryPrimitives.ReadSingleLittleEndian(payload[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[8..]));
        return true;
    }
}

/// <summary>SHOOT: id оружия (1 байт) + нормализованное направление (12 байт).</summary>
public readonly record struct ShootPayload(byte WeaponId, float DirX, float DirY, float DirZ)
{
    public const int Size = 13;

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        buffer[0] = WeaponId;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(1), DirX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(5), DirY);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(9), DirZ);
        return buffer;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ShootPayload value)
    {
        value = default;
        if (payload.Length != Size) return false;

        value = new ShootPayload(
            payload[0],
            BinaryPrimitives.ReadSingleLittleEndian(payload[1..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[5..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[9..]));
        return true;
    }
}

/// <summary>CONNECT_ACK / STATE_UPDATE: авторитетное состояние игрока (22 байта).</summary>
public readonly record struct StatePayload(
    ushort PlayerId, float X, float Y, float Z, ushort Hp, ushort Ammo, ushort AckSequence)
{
    public const int Size = 22;

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0), PlayerId);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(2), X);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(6), Y);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(10), Z);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), Hp);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(16), Ammo);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(18), AckSequence);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(20), 0); // Reserved
        return buffer;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out StatePayload value)
    {
        value = default;
        if (payload.Length != Size) return false;

        value = new StatePayload(
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadSingleLittleEndian(payload[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[6..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]));
        return true;
    }
}

/// <summary>SHOOT_RESULT: чем закончился выстрел (10 байт).</summary>
public readonly record struct ShootResultPayload(
    ushort PlayerId, byte WeaponId, bool Hit, ushort Damage, ushort AmmoLeft, ushort AckSequence)
{
    public const int Size = 10;

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0), PlayerId);
        buffer[2] = WeaponId;
        buffer[3] = Hit ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), Damage);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), AmmoLeft);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), AckSequence);
        return buffer;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out ShootResultPayload value)
    {
        value = default;
        if (payload.Length != Size) return false;

        value = new ShootResultPayload(
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            payload[2],
            payload[3] != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]));
        return true;
    }
}

/// <summary>ERROR: текстовое описание причины отказа.</summary>
public readonly record struct ErrorPayload(string Message)
{
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Message);

    public static ErrorPayload Read(ReadOnlySpan<byte> payload) =>
        new(Encoding.UTF8.GetString(payload));
}
