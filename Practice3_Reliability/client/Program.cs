using System.Globalization;
using System.Net;
using System.Text;
using StickmanFight.Reliable.Client;
using StickmanFight.Reliable.Reliability;
using StickmanFight.Reliable.Telemetry;
using StickmanFight.Reliable.Transport;

Console.OutputEncoding = Encoding.UTF8;

string host = ArgString("--host", "127.0.0.1")!;
int port = ArgInt("--port", 9070);
string experiment = ArgString("--experiment", "baseline")!;
int count = ArgInt("--count", 50);
int interval = ArgInt("--interval", 200);
int tick = ArgInt("--tick", 10);
int maxAttempts = ArgInt("--max-attempts", ReliableChannel.DefaultMaxAttempts);
int warmup = ArgInt("--warmup", 2);
int seed = ArgInt("--seed", 20251003);
string? emulate = ArgString("--emulate", null);
string csvPath = ArgString("--csv", Path.Combine("docs", "reliability_samples.csv"))!;

if (!IPAddress.TryParse(host, out var address))
{
    var resolved = await Dns.GetHostAddressesAsync(host);
    address = resolved.FirstOrDefault()
              ?? throw new ArgumentException($"Не удалось разрешить адрес «{host}».");
}

ClientOptions options;
EmulationOptions emulation;
try
{
    options = new ClientOptions(experiment, new IPEndPoint(address, port), count, interval, tick, maxAttempts, warmup);
    options.Validate();
    emulation = EmulationOptions.Parse(emulate, seed);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"Ошибка в параметрах запуска: {ex.Message}");
    return 2;
}

Console.WriteLine("=== UDP-клиент с надёжной доставкой, практическая работа №3 ===");
Console.WriteLine($"Серия: {experiment} | сервер: {options.Server} | SHOOT: {count} шт. раз в {interval} мс | " +
                  $"такт цикла: {tick} мс | попыток: {maxAttempts} | прогрев: {warmup}");
Console.WriteLine($"Эмуляция исходящих пакетов клиента: {emulation}");
Console.WriteLine($"Журнал доставки: {Path.GetFullPath(csvPath)}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Получен Ctrl+C, завершаем серию...");
    cts.Cancel();
};

IUdpTransport transport = new UdpSocketTransport();
if (emulation.IsEnabled) transport = new EmulatedTransport(transport, emulation);

IReadOnlyList<DeliveryRecord> records;
ReliableClient client;

using (transport)
{
    client = new ReliableClient(transport, new MonotonicClock(), options);
    records = await client.RunAsync(cts.Token);
}

DeliveryCsv.Append(csvPath, records);

var stats = DeliveryStatistics.Compute(experiment, records);
Console.WriteLine();
Console.WriteLine($"--- Итоги серии «{experiment}» ---");
Console.WriteLine($"Отправлено SHOOT:              {stats.Sent}");
Console.WriteLine($"Доставлено с первой попытки:   {stats.DeliveredFirstTry} ({stats.DeliveredFirstTryPercent:F1} %)");
Console.WriteLine($"Повторных отправок всего:      {stats.RetransmitTotal}");
Console.WriteLine($"Недоставлено (failed):         {stats.Failed} ({stats.FailedPercent:F1} %)");
Console.WriteLine($"Среднее число попыток:         {stats.AverageAttempts:F3}");
Console.WriteLine($"Среднее время до ACK:          {stats.AverageTimeToAckMs:F3} мс");
Console.WriteLine($"Средний RTO:                   {stats.AverageRtoMs:F3} мс");
Console.WriteLine($"RTO в конце серии:             {stats.FinalRtoMs:F3} мс");
Console.WriteLine($"Повторных ACK проигнорировано: {client.DuplicateAcks}");
Console.WriteLine($"Замеров RTT: PING {client.PingSamples}, ACK {client.AckSamples}");

return 0;

string? ArgString(string name, string? fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

int ArgInt(string name, int fallback) =>
    int.TryParse(ArgString(name, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : fallback;
