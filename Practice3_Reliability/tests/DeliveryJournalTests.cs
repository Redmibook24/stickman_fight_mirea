using StickmanFight.Reliable.Reliability;

namespace StickmanFight.Reliable.Tests;

/// <summary>Тесты журнала доставки и сводных показателей серии.</summary>
public class DeliveryJournalTests
{
    private static DeliveryRecord Delivered(ushort seq, int attempts, double timeToAck, double resolvedAt, double rto) =>
        new("test", "SHOOT", seq, resolvedAt - timeToAck, resolvedAt, attempts, DeliveryStatus.Delivered,
            timeToAck, rto, 1.0, 0.5);

    private static DeliveryRecord Failed(ushort seq, int attempts, double resolvedAt, double rto) =>
        new("test", "SHOOT", seq, 0, resolvedAt, attempts, DeliveryStatus.Failed, null, rto, 1.0, 0.5);

    [Fact]
    public void Доставленная_команда_пишется_в_одну_строку()
    {
        var row = DeliveryCsv.FormatRow(Delivered(3, 1, 1.25, 10.0, 100.0));

        Assert.Equal("test;SHOOT;3;8.750;10.000;1;delivered;1.250;100.000;1.000;0.500", row);
    }

    [Fact]
    public void У_недоставленной_команды_время_до_ACK_пустое()
    {
        var row = DeliveryCsv.FormatRow(Failed(4, 5, 900.0, 100.0));

        Assert.Contains(";failed;;", row);
    }

    [Fact]
    public void Строка_разбирается_обратно_без_потерь()
    {
        var record = Delivered(77, 2, 215.5, 1000.0, 180.25);

        Assert.Equal(record, DeliveryCsv.ParseRow(DeliveryCsv.FormatRow(record)));
    }

    [Fact]
    public void Строка_с_неверным_числом_полей_отвергается()
    {
        Assert.Throws<FormatException>(() => DeliveryCsv.ParseRow("test;SHOOT;1;0;0;1;delivered"));
    }

    [Fact]
    public void Сводка_считает_показатели_таблицы_отчёта()
    {
        var records = new[]
        {
            Delivered(1, attempts: 1, timeToAck: 2.0, resolvedAt: 10, rto: 100),
            Delivered(2, attempts: 3, timeToAck: 220.0, resolvedAt: 400, rto: 120),
            Failed(3, attempts: 5, resolvedAt: 900, rto: 140),
            Delivered(4, attempts: 1, timeToAck: 4.0, resolvedAt: 500, rto: 100),
        };

        var stats = DeliveryStatistics.Compute("test", records);

        Assert.Equal(4, stats.Sent);
        Assert.Equal(2, stats.DeliveredFirstTry);
        Assert.Equal(0 + 2 + 4 + 0, stats.RetransmitTotal);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(2.5, stats.AverageAttempts, 3);
        Assert.Equal(115.0, stats.AverageRtoMs, 3);
        Assert.Equal(75.333, stats.AverageTimeToAckMs, 3);   // только доставленные: (2 + 220 + 4) / 3
        Assert.Equal(140.0, stats.FinalRtoMs, 3);            // по последнему решённому пакету
        Assert.Equal(25.0, stats.FailedPercent, 3);
    }

    [Fact]
    public void Сводка_пустой_серии_не_падает()
    {
        var stats = DeliveryStatistics.Compute("empty", Array.Empty<DeliveryRecord>());

        Assert.Equal(0, stats.Sent);
        Assert.Equal(0.0, stats.FailedPercent, 3);
    }
}
