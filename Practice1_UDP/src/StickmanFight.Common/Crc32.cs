namespace StickmanFight.Net;

/// <summary>
/// Табличная реализация CRC32 (полином IEEE 802.3, 0xEDB88320).
/// Используется для проверки целостности полезной нагрузки:
/// UDP не гарантирует доставку неповреждённых данных на уровне приложения,
/// поэтому контрольную сумму считаем сами.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;
            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }
}
