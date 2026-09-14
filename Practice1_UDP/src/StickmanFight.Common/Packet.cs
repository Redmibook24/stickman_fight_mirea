using System.Text;

namespace StickmanFight.Net;

/// <summary>Результат разбора датаграммы.</summary>
public enum ParseResult
{
    Ok,
    TooShort,          // датаграмма короче заголовка
    UnknownVersion,    // чужая версия протокола
    SizeMismatch,      // PayloadSize не совпадает с реальной длиной
    ChecksumMismatch,  // CRC32 не сошлась — данные повреждены
}

/// <summary>Готовый пакет: заголовок + полезная нагрузка.</summary>
public sealed class Packet
{
    public PacketHeader Header { get; }
    public byte[] Payload { get; }

    public Packet(PacketHeader header, byte[] payload)
    {
        Header = header;
        Payload = payload;
    }

    /// <summary>Собирает датаграмму: считает CRC32 нагрузки и пишет заголовок.</summary>
    public static byte[] Build(PacketType type, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new ArgumentException("Полезная нагрузка слишком велика для UDP-пакета.", nameof(payload));

        var datagram = new byte[PacketHeader.Size + payload.Length];
        var header = new PacketHeader(type, sequence, (ushort)payload.Length, Crc32.Compute(payload));

        header.WriteTo(datagram);
        payload.CopyTo(datagram.AsSpan(PacketHeader.Size));
        return datagram;
    }

    public static byte[] Build(PacketType type, ushort sequence) =>
        Build(type, sequence, ReadOnlySpan<byte>.Empty);

    /// <summary>Разбирает датаграмму и проверяет версию, длину и контрольную сумму.</summary>
    public static ParseResult TryParse(ReadOnlySpan<byte> datagram, out Packet? packet)
    {
        packet = null;

        if (!PacketHeader.TryRead(datagram, out var header))
            return ParseResult.TooShort;

        if (header.Version != PacketHeader.CurrentVersion)
            return ParseResult.UnknownVersion;

        var payload = datagram[PacketHeader.Size..];
        if (payload.Length != header.PayloadSize)
            return ParseResult.SizeMismatch;

        if (Crc32.Compute(payload) != header.Checksum)
            return ParseResult.ChecksumMismatch;

        packet = new Packet(header, payload.ToArray());
        return ParseResult.Ok;
    }

    public static string Describe(ParseResult result) => result switch
    {
        ParseResult.TooShort         => "датаграмма короче заголовка (12 байт)",
        ParseResult.UnknownVersion   => "неизвестная версия протокола",
        ParseResult.SizeMismatch     => "PayloadSize не совпадает с фактическим размером данных",
        ParseResult.ChecksumMismatch => "не сошлась контрольная сумма CRC32",
        _                            => "пакет корректен",
    };

    /// <summary>Шестнадцатеричный дамп — удобно показывать в логе сервера.</summary>
    public static string ToHex(ReadOnlySpan<byte> data, int maxBytes = 24)
    {
        var sb = new StringBuilder();
        int count = Math.Min(data.Length, maxBytes);

        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }

        if (data.Length > count) sb.Append(" ...");
        return sb.ToString();
    }
}
