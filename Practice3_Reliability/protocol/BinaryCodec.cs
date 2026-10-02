namespace StickmanFight.Reliable.Protocol;

/// <summary>
/// Примитивы сериализации в сетевом порядке байт (big-endian).
/// Чтение всегда проверяет, что в буфере достаточно данных, и сдвигает курсор
/// только при успехе, поэтому разбор обрезанного пакета не читает мусор.
/// </summary>
public static class BinaryCodec
{
    public static void WriteU8(List<byte> output, byte value) => output.Add(value);

    public static void WriteU16(List<byte> output, ushort value)
    {
        output.Add((byte)(value >> 8));
        output.Add((byte)value);
    }

    public static void WriteU32(List<byte> output, uint value)
    {
        for (int shift = 24; shift >= 0; shift -= 8)
            output.Add((byte)(value >> shift));
    }

    public static void WriteU64(List<byte> output, ulong value)
    {
        for (int shift = 56; shift >= 0; shift -= 8)
            output.Add((byte)(value >> shift));
    }

    /// <summary>float передаётся как его 32-битное представление IEEE 754 в сетевом порядке.</summary>
    public static void WriteF32(List<byte> output, float value) =>
        WriteU32(output, BitConverter.SingleToUInt32Bits(value));

    public static bool TryReadU8(ReadOnlySpan<byte> source, ref int offset, out byte value)
    {
        value = 0;
        if (offset + 1 > source.Length) return false;

        value = source[offset];
        offset += 1;
        return true;
    }

    public static bool TryReadU16(ReadOnlySpan<byte> source, ref int offset, out ushort value)
    {
        value = 0;
        if (offset + 2 > source.Length) return false;

        value = (ushort)((source[offset] << 8) | source[offset + 1]);
        offset += 2;
        return true;
    }

    public static bool TryReadU32(ReadOnlySpan<byte> source, ref int offset, out uint value)
    {
        value = 0;
        if (offset + 4 > source.Length) return false;

        uint result = 0;
        for (int i = 0; i < 4; i++)
            result = (result << 8) | source[offset + i];

        value = result;
        offset += 4;
        return true;
    }

    public static bool TryReadU64(ReadOnlySpan<byte> source, ref int offset, out ulong value)
    {
        value = 0;
        if (offset + 8 > source.Length) return false;

        ulong result = 0;
        for (int i = 0; i < 8; i++)
            result = (result << 8) | source[offset + i];

        value = result;
        offset += 8;
        return true;
    }

    public static bool TryReadF32(ReadOnlySpan<byte> source, ref int offset, out float value)
    {
        value = 0;
        if (!TryReadU32(source, ref offset, out uint bits)) return false;

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }
}
