using System.Globalization;
using System.Text;
using StickmanFight.Latency.Server;
using StickmanFight.Latency.Telemetry;
using StickmanFight.Latency.Transport;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Практика №2 — UDP-сервер PING/PONG";

int port = ArgInt("--port", 9060);
int seed = ArgInt("--seed", 20250920);
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

Console.WriteLine("=== UDP-сервер PING/PONG, практическая работа №2 ===");
Console.WriteLine($"Порт: {port} | эмуляция сети: {emulation}");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var clock = new MonotonicClock();
IUdpTransport transport = new UdpSocketTransport(port);
if (emulation.IsEnabled) transport = new EmulatedTransport(transport, emulation);

try
{
    using (transport)
    {
        var server = new PongServer(transport, clock, verbose: !quiet);
        await server.RunAsync(cts.Token);

        if (transport is EmulatedTransport emulated)
            Console.WriteLine($"Эмулятор: потеряно {emulated.DroppedCount}, продублировано {emulated.DuplicatedCount} датаграмм.");
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
