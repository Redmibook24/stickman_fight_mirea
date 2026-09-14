using System.Buffers.Binary;

namespace StickmanFight.Net;

/// <summary>
/// Заголовок пакета фиксированного размера — 12 байт, порядок байт little-endian.
///
///  смещение | размер | поле
///  ---------+--------+---------------------------------------------
///     0     |   1    | Version      — версия протокола
///     1     |   1    | Type         — PacketType (тип команды)
///     2     |   2    | Sequence     — порядковый номер пакета (uint16)
///     4     |   2    | PayloadSize  — размер полезной нагрузки в байтах
///     6     |   2    | Reserved     — выравнивание / запас на будущее
///     8     |   4    | Checksum     — CRC32 полезной нагрузки
/// </summary>
public readonly struct PacketHeader
{
    public const int Size = 12;
    public const byte CurrentVersion = 1;

    public byte Version { get; }
    public PacketType Type { get; }
    public ushort Sequence { get; }
    public ushort PayloadSize { get; }
    public uint Checksum { get; }

    public PacketHeader(PacketType type, ushort sequence, ushort payloadSize, uint checksum,
                        byte version = CurrentVersion)
    {
        Version = version;
        Type = type;
        Sequence = sequence;
        PayloadSize = payloadSize;
        Checksum = checksum;
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Для заголовка нужно минимум {Size} байт.", nameof(destination));

        destination[0] = Version;
        destination[1] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], PayloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0); // Reserved
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Checksum);
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out PacketHeader header)
    {
        if (source.Length < Size)
        {
            header = default;
            return false;
        }

        header = new PacketHeader(
            type: (PacketType)source[1],
            sequence: BinaryPrimitives.ReadUInt16LittleEndian(source[2..]),
            payloadSize: BinaryPrimitives.ReadUInt16LittleEndian(source[4..]),
            checksum: BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            version: source[0]);

        return true;
    }

    public override string ToString() =>
        $"v{Version} {Type} seq={Sequence} size={PayloadSize} crc=0x{Checksum:X8}";
}
