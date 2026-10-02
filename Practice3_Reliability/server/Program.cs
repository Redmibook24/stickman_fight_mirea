using System.Globalization;
using System.Text;
using StickmanFight.Reliable.Reliability;
using StickmanFight.Reliable.Server;
using StickmanFight.Reliable.Telemetry;
using StickmanFight.Reliable.Transport;

Console.OutputEncoding = Encoding.UTF8;

int port = ArgInt("--port", 9070);
int seed = ArgInt("--seed", 20251002);
int window = ArgInt("--dedup-window", DeduplicationWindow.DefaultCapacity);
string? emulate = ArgString("--emulate", null);
bool quiet = args.Contains("--quiet");

EmulationOptions emulation;
try
{
    emulation = EmulationOptions.Parse(emulate, seed);
}
catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
{
    Console.Error.WriteLine($"Ошибка в параметре --emulate: {ex.Message}");
    return 2;
}

Console.WriteLine("=== UDP-сервер с надёжной доставкой, практическая работа №3 ===");
Console.WriteLine($"Порт: {port} | эмуляция исходящих пакетов: {emulation}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

IUdpTransport transport = new UdpSocketTransport(port);
if (emulation.IsEnabled) transport = new EmulatedTransport(transport, emulation);

try
{
    using (transport)
    {
        var server = new ReliableServer(transport, new MonotonicClock(), window, verbose: !quiet);
        await server.RunAsync(cts.Token);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Фатальная ошибка: {ex.Message}");
    return 1;
}

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
