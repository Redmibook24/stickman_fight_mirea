using System.Globalization;
using System.Text;

namespace StickmanFight.Reliable.Reliability;

/// <summary>Итог доставки одной надёжной команды.</summary>
public enum DeliveryStatus
{
    Delivered,  // пришёл ACK
    Failed,     // попытки исчерпаны, ACK так и не пришёл
}

/// <summary>Строка журнала docs/reliability_samples.csv.</summary>
/// <param name="ExperimentId">Серия: baseline, loss_5, ...</param>
/// <param name="Command">Имя команды, например SHOOT.</param>
/// <param name="Sequence">sequenceNumber пакета.</param>
/// <param name="FirstSentAtMs">Первая отправка от начала серии, мс.</param>
/// <param name="ResolvedAtMs">Когда стало известно, доставлен пакет или нет, мс.</param>
/// <param name="Attempts">Сколько раз пакет отправлен всего.</param>
/// <param name="Status">Доставлен или окончательно потерян.</param>
/// <param name="TimeToAckMs">От первой отправки до ACK, только для доставленных.</param>
/// <param name="RtoMs">RTO в момент, когда судьба пакета решилась.</param>
/// <param name="SrttMs">SRTT в тот же момент.</param>
/// <param name="RttVarMs">RTTVAR в тот же момент.</param>
public readonly record struct DeliveryRecord(
    string ExperimentId,
    string Command,
    ushort Sequence,
    double FirstSentAtMs,
    double ResolvedAtMs,
    int Attempts,
    DeliveryStatus Status,
    double? TimeToAckMs,
    double RtoMs,
    double SrttMs,
    double RttVarMs);

/// <summary>Сводка по серии: ровно те показатели, что требует таблица отчёта.</summary>
public readonly record struct DeliveryStatistics(
    string ExperimentId,
    int Sent,
    int DeliveredFirstTry,
    int RetransmitTotal,
    int Failed,
    double AverageAttempts,
    double AverageRtoMs,
    double AverageTimeToAckMs,
    double FinalRtoMs)
{
    public double DeliveredFirstTryPercent => Sent == 0 ? 0 : 100.0 * DeliveredFirstTry / Sent;

    public double FailedPercent => Sent == 0 ? 0 : 100.0 * Failed / Sent;

    public static DeliveryStatistics Compute(string experimentId, IReadOnlyList<DeliveryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
            return new DeliveryStatistics(experimentId, 0, 0, 0, 0, 0, 0, 0, 0);

        var delivered = records.Where(r => r.Status == DeliveryStatus.Delivered && r.TimeToAckMs.HasValue).ToList();

        return new DeliveryStatistics(
            ExperimentId: experimentId,
            Sent: records.Count,
            DeliveredFirstTry: records.Count(r => r.Status == DeliveryStatus.Delivered && r.Attempts == 1),
            RetransmitTotal: records.Sum(r => r.Attempts - 1),
            Failed: records.Count(r => r.Status == DeliveryStatus.Failed),
            AverageAttempts: records.Average(r => r.Attempts),
            AverageRtoMs: records.Average(r => r.RtoMs),
            AverageTimeToAckMs: delivered.Count > 0 ? delivered.Average(r => r.TimeToAckMs!.Value) : 0,
            FinalRtoMs: records.OrderBy(r => r.ResolvedAtMs).Last().RtoMs);
    }
}

/// <summary>Формат журнала доставки: разделитель «;», дробная часть через точку.</summary>
public static class DeliveryCsv
{
    public const string Header =
        "experiment_id;command;sequence;first_sent_at_ms;resolved_at_ms;attempts;status;time_to_ack_ms;rto_ms;srtt_ms;rttvar_ms";

    public static string StatusName(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Delivered => "delivered",
        DeliveryStatus.Failed    => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Неизвестный статус доставки."),
    };

    public static DeliveryStatus ParseStatus(string name) => name switch
    {
        "delivered" => DeliveryStatus.Delivered,
        "failed"    => DeliveryStatus.Failed,
        _ => throw new FormatException($"Неизвестный статус доставки «{name}»."),
    };

    public static string FormatRow(DeliveryRecord r) => string.Join(';',
        r.ExperimentId,
        r.Command,
        r.Sequence.ToString(CultureInfo.InvariantCulture),
        F(r.FirstSentAtMs),
        F(r.ResolvedAtMs),
        r.Attempts.ToString(CultureInfo.InvariantCulture),
        StatusName(r.Status),
        r.TimeToAckMs.HasValue ? F(r.TimeToAckMs.Value) : string.Empty,
        F(r.RtoMs),
        F(r.SrttMs),
        F(r.RttVarMs));

    public static DeliveryRecord ParseRow(string row)
    {
        var p = row.Split(';');
        if (p.Length != 11)
            throw new FormatException($"Ожидалось 11 полей, получено {p.Length}: «{row}»");

        return new DeliveryRecord(
            ExperimentId: p[0],
            Command: p[1],
            Sequence: ushort.Parse(p[2], CultureInfo.InvariantCulture),
            FirstSentAtMs: D(p[3]),
            ResolvedAtMs: D(p[4]),
            Attempts: int.Parse(p[5], CultureInfo.InvariantCulture),
            Status: ParseStatus(p[6]),
            TimeToAckMs: string.IsNullOrEmpty(p[7]) ? null : D(p[7]),
            RtoMs: D(p[8]),
            SrttMs: D(p[9]),
            RttVarMs: D(p[10]));
    }

    /// <summary>Дописывает строки, создавая файл с заголовком при первом вызове.</summary>
    public static void Append(string path, IEnumerable<DeliveryRecord> records)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        bool needHeader = !File.Exists(path) || new FileInfo(path).Length == 0;

        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (needHeader) writer.WriteLine(Header);

        foreach (var record in records)
            writer.WriteLine(FormatRow(record));
    }

    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static double D(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}
