using System.Globalization;
using System.Net;
using System.Text;
using StickmanFight.Latency.Client;
using StickmanFight.Latency.Telemetry;
using StickmanFight.Latency.Transport;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Практика №2 — UDP-клиент PING/PONG";

string host = ArgString("--host", "127.0.0.1")!;
int port = ArgInt("--port", 9060);
string experiment = ArgString("--experiment", "baseline")!;
int count = ArgInt("--count", 50);
int interval = ArgInt("--interval", 300);
int timeout = ArgInt("--timeout", 1000);
int warmup = ArgInt("--warmup", 2);
string csvPath = ArgString("--csv", Path.Combine("docs", "latency_samples.csv"))!;

if (!IPAddress.TryParse(host, out var address))
{
    var resolved = await Dns.GetHostAddressesAsync(host);
    address = resolved.FirstOrDefault()
              ?? throw new ArgumentException($"Не удалось разрешить адрес «{host}».");
}

var options = new ProbeOptions(experiment, new IPEndPoint(address, port), count, interval, timeout, warmup);

Console.WriteLine("=== UDP-клиент PING/PONG, практическая работа №2 ===");
Console.WriteLine($"Сценарий: {experiment} | сервер: {options.Server} | PING: {count} шт. " +
                  $"раз в {interval} мс | таймаут: {timeout} мс | прогрев: {warmup} PING");
Console.WriteLine($"Журнал замеров: {Path.GetFullPath(csvPath)}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Получен Ctrl+C, завершаем прогон...");
    cts.Cancel();
};

IReadOnlyList<LatencySample> samples;
LatencyProbe probe;

try
{
    using var transport = new UdpSocketTransport();
    probe = new LatencyProbe(transport, new MonotonicClock(), options);
    samples = await probe.RunAsync(cts.Token);
}
catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
{
    Console.Error.WriteLine($"Ошибка в параметрах запуска: {ex.Message}");
    return 2;
}

LatencyCsv.Append(csvPath, samples);

var statistics = LatencyStatistics.Compute(experiment, samples);
PrintSummary(statistics, probe.RejectedPackets);

return 0;

void PrintSummary(LatencyStatistics stats, int rejected)
{
    Console.WriteLine();
    Console.WriteLine($"--- Итоги сценария «{stats.ExperimentId}» ---");
    Console.WriteLine($"Отправлено PING:      {stats.Sent}");
    Console.WriteLine($"Получено ответов:     {stats.Received}");
    Console.WriteLine($"Таймаутов:            {stats.Timeouts}");
    Console.WriteLine($"Опоздавших ответов:   {stats.LateResponses}");
    Console.WriteLine($"Дубликатов:           {stats.Duplicates}");
    Console.WriteLine($"Ответов на чужие seq: {stats.UnknownResponses}");
    Console.WriteLine($"Отброшено пакетов:    {rejected}");
    Console.WriteLine($"RTT min/mean/median/max: {stats.MinRttMs:F3} / {stats.MeanRttMs:F3} / " +
                      $"{stats.MedianRttMs:F3} / {stats.MaxRttMs:F3} мс");
    Console.WriteLine($"SRTT:                 {stats.SrttMs:F3} мс");
    Console.WriteLine($"Средний джиттер:      {stats.MeanJitterMs:F3} мс");
    Console.WriteLine($"Доля потерь:          {stats.LossRatePercent:F2} %");
    Console.WriteLine($"Строк дописано в CSV: {samples.Count}");
}

string? ArgString(string name, string? fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

int ArgInt(string name, int fallback) =>
    int.TryParse(ArgString(name, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : fallback;
