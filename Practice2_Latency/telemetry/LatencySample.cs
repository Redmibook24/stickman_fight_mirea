using System.Globalization;
using System.Text;

namespace StickmanFight.Latency.Telemetry;

/// <summary>Одна строка журнала измерений.</summary>
/// <param name="ExperimentId">Идентификатор сценария: baseline, delay_50, ...</param>
/// <param name="Sample">Номер замера внутри сценария (0 — событие без своего PING).</param>
/// <param name="Sequence">sequenceNumber пакета.</param>
/// <param name="SentAtMs">Момент отправки PING от начала эксперимента, мс.</param>
/// <param name="RttMs">Измеренный RTT, если он есть.</param>
/// <param name="SrttMs">Сглаженный RTT после этого замера, если он есть.</param>
/// <param name="Status">Статус замера.</param>
public readonly record struct LatencySample(
    string ExperimentId,
    int Sample,
    ushort Sequence,
    double SentAtMs,
    double? RttMs,
    double? SrttMs,
    ResponseStatus Status);

/// <summary>
/// Чтение и запись журнала docs/latency_samples.csv.
/// Разделитель — «;», разделитель дробной части — точка (InvariantCulture),
/// чтобы файл одинаково читался в Excel и в pandas/matplotlib.
/// </summary>
public static class LatencyCsv
{
    public const string Header = "experiment_id;sample;sequence;sent_at_ms;rtt_ms;srtt_ms;status";

    public static string StatusName(ResponseStatus status) => status switch
    {
        ResponseStatus.Received  => "received",
        ResponseStatus.Timeout   => "timeout",
        ResponseStatus.Late      => "late_response",
        ResponseStatus.Duplicate => "duplicate_response",
        ResponseStatus.Unknown   => "unknown_response",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Неизвестный статус замера."),
    };

    public static ResponseStatus ParseStatus(string name) => name switch
    {
        "received"           => ResponseStatus.Received,
        "timeout"            => ResponseStatus.Timeout,
        "late_response"      => ResponseStatus.Late,
        "duplicate_response" => ResponseStatus.Duplicate,
        "unknown_response"   => ResponseStatus.Unknown,
        _ => throw new FormatException($"Неизвестный статус «{name}» в журнале измерений."),
    };

    public static string FormatRow(LatencySample sample) => string.Join(';',
        sample.ExperimentId,
        sample.Sample.ToString(CultureInfo.InvariantCulture),
        sample.Sequence.ToString(CultureInfo.InvariantCulture),
        sample.SentAtMs.ToString("F3", CultureInfo.InvariantCulture),
        sample.RttMs?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
        sample.SrttMs?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty,
        StatusName(sample.Status));

    public static LatencySample ParseRow(string row)
    {
        var parts = row.Split(';');
        if (parts.Length != 7)
            throw new FormatException($"Ожидалось 7 полей, получено {parts.Length}: «{row}»");

        return new LatencySample(
            ExperimentId: parts[0],
            Sample: int.Parse(parts[1], CultureInfo.InvariantCulture),
            Sequence: ushort.Parse(parts[2], CultureInfo.InvariantCulture),
            SentAtMs: double.Parse(parts[3], CultureInfo.InvariantCulture),
            RttMs: ParseOptional(parts[4]),
            SrttMs: ParseOptional(parts[5]),
            Status: ParseStatus(parts[6]));
    }

    /// <summary>Дописывает замеры в файл, создавая его вместе с заголовком при первом вызове.</summary>
    public static void Append(string path, IEnumerable<LatencySample> samples)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        bool needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

        // UTF-8 без BOM: иначе первый столбец заголовка читается с лишним символом.
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (needHeader) writer.WriteLine(Header);

        foreach (var sample in samples)
            writer.WriteLine(FormatRow(sample));
    }

    public static IReadOnlyList<LatencySample> Read(string path) =>
        File.ReadLines(path)
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseRow)
            .ToList();

    private static double? ParseOptional(string value) =>
        string.IsNullOrEmpty(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
}
