using System.Text;
using StickmanFight.Net.Server;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Битва жердяев — UDP-сервер";

int port = ArgInt(args, "--port", 9050);
string logDirectory = ArgString(args, "--log", "logs");

using var log = new Logger(logDirectory);
log.Info("=== UDP-сервер «Битва жердяев», практическая работа №1 ===");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;                 // не убиваем процесс жёстко — даём дописать лог
    log.Info("Получен Ctrl+C, останавливаемся...");
    cts.Cancel();
};

try
{
    using var server = new GameServer(port, log);
    await server.RunAsync(cts.Token);
}
catch (Exception ex)
{
    log.Error($"Фатальная ошибка: {ex.Message}");
    return 1;
}

return 0;

static string ArgString(string[] args, string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static int ArgInt(string[] args, string name, int fallback) =>
    int.TryParse(ArgString(args, name, fallback.ToString()), out int value) ? value : fallback;
