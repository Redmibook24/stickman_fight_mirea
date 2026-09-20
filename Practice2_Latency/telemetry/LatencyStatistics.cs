namespace StickmanFight.Latency.Telemetry;

/// <summary>Сводные метрики одного сценария.</summary>
public readonly record struct LatencyStatistics(
    string ExperimentId,
    int Sent,
    int Received,
    int Timeouts,
    int LateResponses,
    int Duplicates,
    int UnknownResponses,
    double MinRttMs,
    double MaxRttMs,
    double MeanRttMs,
    double MedianRttMs,
    double SrttMs,
    double MeanJitterMs,
    double LossRatePercent)
{
    /// <summary>
    /// Считает метрики по журналу замеров одного сценария.
    /// Порядок строк важен: джиттер и SRTT считаются по последовательности RTT.
    /// </summary>
    public static LatencyStatistics Compute(string experimentId, IReadOnlyList<LatencySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var rtts = samples
            .Where(sample => sample.Status == ResponseStatus.Received && sample.RttMs.HasValue)
            .Select(sample => sample.RttMs!.Value)
            .ToList();

        int timeouts = samples.Count(sample => sample.Status == ResponseStatus.Timeout);
        int late = samples.Count(sample => sample.Status == ResponseStatus.Late);
        int duplicates = samples.Count(sample => sample.Status == ResponseStatus.Duplicate);
        int unknown = samples.Count(sample => sample.Status == ResponseStatus.Unknown);

        // Отправленным считается каждый PING: у него ровно одна строка-итог.
        int sent = rtts.Count + timeouts + late;

        return new LatencyStatistics(
            ExperimentId: experimentId,
            Sent: sent,
            Received: rtts.Count,
            Timeouts: timeouts,
            LateResponses: late,
            Duplicates: duplicates,
            UnknownResponses: unknown,
            MinRttMs: rtts.Count > 0 ? rtts.Min() : 0.0,
            MaxRttMs: rtts.Count > 0 ? rtts.Max() : 0.0,
            MeanRttMs: rtts.Count > 0 ? rtts.Average() : 0.0,
            MedianRttMs: Median(rtts),
            SrttMs: SmoothedRtt(rtts),
            MeanJitterMs: MeanJitter(rtts),
            LossRatePercent: sent == 0 ? 0.0 : 100.0 * timeouts / sent);
    }

    /// <summary>Медиана: для чётного числа замеров — среднее двух центральных.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;

        var sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>SRTT = 0.875 * SRTT + 0.125 * RTT, первый замер берётся как есть.</summary>
    public static double SmoothedRtt(IReadOnlyList<double> rtts)
    {
        if (rtts.Count == 0) return 0.0;

        double srtt = rtts[0];
        for (int i = 1; i < rtts.Count; i++)
            srtt = 0.875 * srtt + 0.125 * rtts[i];

        return srtt;
    }

    /// <summary>J = (1 / (n - 1)) * sum |RTT_i - RTT_{i-1}|.</summary>
    public static double MeanJitter(IReadOnlyList<double> rtts)
    {
        if (rtts.Count < 2) return 0.0;

        double sum = 0.0;
        for (int i = 1; i < rtts.Count; i++)
            sum += Math.Abs(rtts[i] - rtts[i - 1]);

        return sum / (rtts.Count - 1);
    }
}
