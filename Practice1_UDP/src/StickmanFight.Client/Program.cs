using System.Text;
using StickmanFight.Net.Client;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Битва жердяев — UDP-клиент";

string host = ArgString(args, "--host", "127.0.0.1");
int port = ArgInt(args, "--port", 9050);
string name = ArgString(args, "--name", $"Жердяй-{Random.Shared.Next(100, 999)}");
int intervalMs = ArgInt(args, "--interval", 1000);   // периодичность отправки команд
int count = ArgInt(args, "--count", 0);              // 0 — бесконечно

Console.WriteLine("=== UDP-клиент «Битва жердяев», практическая работа №1 ===");
Console.WriteLine($"Сервер: {host}:{port} | игрок: {name} | команда раз в {intervalMs} мс" +
                  (count > 0 ? $" | всего команд: {count}" : " | Ctrl+C для выхода"));
Console.WriteLine();

using var cts = new CancellationTokenSource();
using var client = new GameClient(host, port, name);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Получен Ctrl+C, отключаемся...");
    cts.Cancel();
};

if (!await client.HandshakeAsync(cts.Token))
{
    Console.WriteLine($"Не удалось выполнить рукопожатие с {host}:{port}. Сервер запущен?");
    return 1;
}

await client.RunAsync(TimeSpan.FromMilliseconds(intervalMs), count, cts.Token);

client.SendDisconnect();
client.PrintSummary();
return 0;

static string ArgString(string[] args, string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static int ArgInt(string[] args, string name, int fallback) =>
    int.TryParse(ArgString(args, name, fallback.ToString()), out int value) ? value : fallback;
