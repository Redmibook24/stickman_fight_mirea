using System.Globalization;
using StickmanFight.Latency.Telemetry;

namespace StickmanFight.Latency.Tests;

/// <summary>Тесты формата журнала измерений.</summary>
public class LatencyCsvTests
{
    [Fact]
    public void Строка_замера_пишется_через_точку_с_запятой()
    {
        var sample = new LatencySample("baseline", 1, 42, 0.0, 1.73, 1.73, ResponseStatus.Received);

        Assert.Equal("baseline;1;42;0.000;1.730;1.730;received", LatencyCsv.FormatRow(sample));
    }

    [Fact]
    public void Таймаут_пишется_с_пустыми_полями_RTT()
    {
        var sample = new LatencySample("loss_5", 8, 57, 7000.0, null, null, ResponseStatus.Timeout);

        Assert.Equal("loss_5;8;57;7000.000;;;timeout", LatencyCsv.FormatRow(sample));
    }

    [Fact]
    public void Строка_разбирается_обратно_без_потерь()
    {
        var sample = new LatencySample("jitter", 12, 12, 3300.5, 128.25, 96.5, ResponseStatus.Received);

        var parsed = LatencyCsv.ParseRow(LatencyCsv.FormatRow(sample));

        Assert.Equal(sample, parsed);
    }

    [Theory]
    [InlineData(ResponseStatus.Received, "received")]
    [InlineData(ResponseStatus.Timeout, "timeout")]
    [InlineData(ResponseStatus.Late, "late_response")]
    [InlineData(ResponseStatus.Duplicate, "duplicate_response")]
    [InlineData(ResponseStatus.Unknown, "unknown_response")]
    public void Имена_статусов_совпадают_со_спецификацией(ResponseStatus status, string expected)
    {
        Assert.Equal(expected, LatencyCsv.StatusName(status));
        Assert.Equal(status, LatencyCsv.ParseStatus(expected));
    }

    [Fact]
    public void Дробная_часть_всегда_с_точкой_независимо_от_локали()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
            var sample = new LatencySample("baseline", 1, 1, 0.0, 12.5, 12.5, ResponseStatus.Received);

            Assert.Contains("12.500", LatencyCsv.FormatRow(sample));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Строка_с_лишними_полями_отвергается()
    {
        Assert.Throws<FormatException>(() => LatencyCsv.ParseRow("baseline;1;42;0.000;1.730;1.730;received;extra"));
    }

    [Fact]
    public void Неизвестный_статус_отвергается()
    {
        Assert.Throws<FormatException>(() => LatencyCsv.ParseRow("baseline;1;42;0.000;;;broken"));
    }

    [Fact]
    public void Журнал_дописывается_с_заголовком_и_читается_обратно()
    {
        string path = Path.Combine(Path.GetTempPath(), $"latency_{Guid.NewGuid():N}.csv");
        try
        {
            LatencyCsv.Append(path, new[]
            {
                new LatencySample("baseline", 1, 1, 0.0, 1.5, 1.5, ResponseStatus.Received),
            });
            LatencyCsv.Append(path, new[]
            {
                new LatencySample("baseline", 2, 2, 300.0, null, null, ResponseStatus.Timeout),
            });

            var lines = File.ReadAllLines(path);
            var samples = LatencyCsv.Read(path);

            Assert.Equal(LatencyCsv.Header, lines[0]);
            Assert.Equal(3, lines.Length);                 // заголовок пишется один раз
            Assert.Equal(2, samples.Count);
            Assert.Equal(ResponseStatus.Timeout, samples[1].Status);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
