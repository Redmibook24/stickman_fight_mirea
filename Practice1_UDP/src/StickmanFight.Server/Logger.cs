namespace StickmanFight.Net.Server;

/// <summary>
/// Лог сервера: одновременно в консоль и в файл logs/server-YYYY-MM-DD.log.
/// По заданию сервер обязан вести лог всех полученных команд.
/// </summary>
public sealed class Logger : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly Lock _sync = new();

    public Logger(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"server-{DateTime.Now:yyyy-MM-dd}.log");
        _file = new StreamWriter(path, append: true) { AutoFlush = true };

        Info($"Лог пишется в файл: {Path.GetFullPath(path)}");
    }

    public void Info(string message) => Write("INFO ", message, ConsoleColor.Gray);
    public void Packet(string message) => Write("PKT  ", message, ConsoleColor.Cyan);
    public void Command(string message) => Write("CMD  ", message, ConsoleColor.Green);
    public void Warn(string message) => Write("WARN ", message, ConsoleColor.Yellow);
    public void Error(string message) => Write("ERROR", message, ConsoleColor.Red);

    private void Write(string level, string message, ConsoleColor color)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";

        lock (_sync)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = previous;

            _file?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_sync) _file?.Dispose();
    }
}
