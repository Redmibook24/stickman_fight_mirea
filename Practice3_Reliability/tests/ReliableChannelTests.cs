using StickmanFight.Reliable.Reliability;

namespace StickmanFight.Reliable.Tests;

/// <summary>Тесты менеджера надёжной доставки. Время задаётся числами, сеть не нужна.</summary>
public class ReliableChannelTests
{
    // Три теста из методички, перенесённые с Catch2 на xUnit.

    [Fact]
    public void Пакет_отправляется_повторно_после_истечения_RTO()
    {
        var channel = new ReliableChannel(maxAttempts: 3);
        channel.OnSent(1, new byte[] { 0x04, 0x00, 0x01 }, nowUs: 0);

        var none = channel.CollectForRetransmission(nowUs: 50_000, timeoutUs: 200_000);
        Assert.Empty(none);

        var due = channel.CollectForRetransmission(nowUs: 250_000, timeoutUs: 200_000);
        Assert.Single(due);
        Assert.Equal(2, due[0].Attempts);
    }

    [Fact]
    public void Пакет_исчерпавший_попытки_уходит_в_failed()
    {
        var channel = new ReliableChannel(maxAttempts: 2);
        channel.OnSent(7, Array.Empty<byte>(), 0);

        channel.CollectForRetransmission(300_000, 200_000);   // попытка 2
        channel.CollectForRetransmission(600_000, 200_000);   // тайм-аут последней попытки: failed

        Assert.Equal(1, channel.FailedCount);
        Assert.Equal(0, channel.PendingCount);
    }

    [Fact]
    public void Повторный_ACK_игнорируется()
    {
        var channel = new ReliableChannel();
        channel.OnSent(3, Array.Empty<byte>(), 0);

        Assert.True(channel.OnAckReceived(3));
        Assert.False(channel.OnAckReceived(3));
    }

    // Дополнительные проверки поведения, которого требует задание.

    [Fact]
    public void ACK_на_неизвестный_номер_ничего_не_ломает()
    {
        var channel = new ReliableChannel();
        channel.OnSent(1, Array.Empty<byte>(), 0);

        Assert.False(channel.OnAckReceived(999));
        Assert.Equal(1, channel.PendingCount);
    }

    [Fact]
    public void ACK_снимает_пакет_и_отдаёт_его_историю()
    {
        var channel = new ReliableChannel();
        channel.OnSent(5, new byte[] { 1 }, nowUs: 1_000);
        channel.CollectForRetransmission(nowUs: 400_000, timeoutUs: 300_000);

        Assert.True(channel.OnAckReceived(5, out var packet));

        Assert.Equal(2, packet.Attempts);
        Assert.Equal(1_000UL, packet.FirstSentAtUs);
        Assert.Equal(400_000UL, packet.LastSentAtUs);
        Assert.Equal(0, channel.PendingCount);
    }

    [Fact]
    public void Пакет_не_возвращается_чаще_чем_раз_в_тайм_аут()
    {
        var channel = new ReliableChannel(maxAttempts: 5);
        channel.OnSent(1, Array.Empty<byte>(), 0);

        Assert.Single(channel.CollectForRetransmission(250_000, 200_000));   // попытка 2 в 250 мс
        Assert.Empty(channel.CollectForRetransmission(300_000, 200_000));    // прошло 50 мс
        Assert.Empty(channel.CollectForRetransmission(449_000, 200_000));    // прошло 199 мс

        var due = channel.CollectForRetransmission(450_000, 200_000);         // ровно 200 мс
        Assert.Single(due);
        Assert.Equal(3, due[0].Attempts);
    }

    [Fact]
    public void Повтор_отправляет_те_же_байты_с_тем_же_номером()
    {
        var raw = new byte[] { 4, 0, 9, 0, 13, 0, 2, 1 };
        var channel = new ReliableChannel();
        channel.OnSent(9, raw, 0);

        var due = channel.CollectForRetransmission(500_000, 200_000);

        Assert.Equal(9, due[0].Sequence);
        Assert.Same(raw, due[0].RawBytes);
    }

    [Fact]
    public void Окончательная_недоставка_сообщается_событием_один_раз()
    {
        var channel = new ReliableChannel(maxAttempts: 2);
        var failed = new List<PendingPacket>();
        channel.PacketFailed += failed.Add;
        channel.OnSent(11, Array.Empty<byte>(), 0);

        channel.CollectForRetransmission(200_000, 200_000);
        var afterFail = channel.CollectForRetransmission(400_000, 200_000);
        channel.CollectForRetransmission(800_000, 200_000);

        Assert.Empty(afterFail);                 // недоставленный пакет наружу не возвращается
        Assert.Single(failed);
        Assert.Equal(11, failed[0].Sequence);
        Assert.Equal(2, failed[0].Attempts);
        Assert.Equal(11, channel.Failed.Single().Sequence);
    }

    [Fact]
    public void Опоздавший_ACK_после_failed_игнорируется()
    {
        var channel = new ReliableChannel(maxAttempts: 1);
        channel.OnSent(2, Array.Empty<byte>(), 0);
        channel.CollectForRetransmission(300_000, 200_000);

        Assert.False(channel.OnAckReceived(2));
        Assert.Equal(1, channel.FailedCount);
    }

    [Fact]
    public void Одна_попытка_означает_отсутствие_повторов()
    {
        var channel = new ReliableChannel(maxAttempts: 1);
        channel.OnSent(1, Array.Empty<byte>(), 0);

        Assert.Empty(channel.CollectForRetransmission(300_000, 200_000));
        Assert.Equal(1, channel.FailedCount);
    }

    [Fact]
    public void Возвращаются_только_просроченные_пакеты_в_порядке_отправки()
    {
        var channel = new ReliableChannel();
        channel.OnSent(30, Array.Empty<byte>(), 0);
        channel.OnSent(10, Array.Empty<byte>(), 50_000);
        channel.OnSent(20, Array.Empty<byte>(), 150_000);   // ему ещё рано

        var due = channel.CollectForRetransmission(260_000, 200_000);

        Assert.Equal(new ushort[] { 30, 10 }, due.Select(p => p.Sequence));
        Assert.Equal(3, channel.PendingCount);
    }

    [Fact]
    public void Повторная_регистрация_ожидающего_номера_запрещена()
    {
        var channel = new ReliableChannel();
        channel.OnSent(1, Array.Empty<byte>(), 0);

        Assert.Throws<InvalidOperationException>(() => channel.OnSent(1, Array.Empty<byte>(), 10));
    }

    [Fact]
    public void Некорректные_параметры_отвергаются()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReliableChannel(maxAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReliableChannel().CollectForRetransmission(0, 0));
    }
}
