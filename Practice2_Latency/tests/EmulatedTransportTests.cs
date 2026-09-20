using System.Net;
using StickmanFight.Latency.Transport;

namespace StickmanFight.Latency.Tests;

/// <summary>Транспорт-заглушка: вместо сокета запоминает отправленные датаграммы.</summary>
internal sealed class FakeTransport : IUdpTransport
{
    public List<byte[]> Sent { get; } = new();

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 0);

    public void Send(ReadOnlySpan<byte> data, IPEndPoint target) => Sent.Add(data.ToArray());

    public Task<Datagram> ReceiveAsync(CancellationToken token) =>
        Task.FromException<Datagram>(new NotSupportedException("Заглушка не принимает датаграммы."));

    public void Dispose() { }
}

/// <summary>Тесты разбора параметров эмуляции и поведения эмулятора сети.</summary>
public class EmulatedTransportTests
{
    private static readonly IPEndPoint Target = new(IPAddress.Loopback, 9060);

    [Fact]
    public void Строка_параметров_разбирается_целиком()
    {
        var options = EmulationOptions.Parse("delay=100,jitter=50,loss=5,duplicate=10", seed: 7);

        Assert.Equal(100, options.DelayMs);
        Assert.Equal(50, options.JitterMs);
        Assert.Equal(5.0, options.LossPercent, 3);
        Assert.Equal(10.0, options.DuplicatePercent, 3);
        Assert.Equal(7, options.Seed);
        Assert.True(options.IsEnabled);
    }

    [Fact]
    public void Пустая_строка_означает_отсутствие_эмуляции()
    {
        Assert.False(EmulationOptions.Parse(null, seed: 1).IsEnabled);
        Assert.False(EmulationOptions.Parse("none", seed: 1).IsEnabled);
    }

    [Fact]
    public void Неизвестный_параметр_отвергается()
    {
        Assert.Throws<FormatException>(() => EmulationOptions.Parse("bandwidth=10", seed: 1));
    }

    [Fact]
    public void Параметр_без_значения_отвергается()
    {
        Assert.Throws<FormatException>(() => EmulationOptions.Parse("delay", seed: 1));
    }

    [Fact]
    public void Доля_потерь_больше_ста_процентов_отвергается()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EmulationOptions.Parse("loss=150", seed: 1));
    }

    [Fact]
    public void Стопроцентная_потеря_не_пропускает_ни_одной_датаграммы()
    {
        var inner = new FakeTransport();
        var emulated = new EmulatedTransport(inner, new EmulationOptions(LossPercent: 100));

        for (int i = 0; i < 10; i++) emulated.Send(new byte[] { 1 }, Target);

        Assert.Empty(inner.Sent);
        Assert.Equal(10, emulated.DroppedCount);
    }

    [Fact]
    public void Стопроцентное_дублирование_отправляет_копию()
    {
        var inner = new FakeTransport();
        var emulated = new EmulatedTransport(inner, new EmulationOptions(DuplicatePercent: 100));

        emulated.Send(new byte[] { 42 }, Target);

        Assert.Equal(2, inner.Sent.Count);
        Assert.Equal(inner.Sent[0], inner.Sent[1]);
        Assert.Equal(1, emulated.DuplicatedCount);
    }

    [Fact]
    public void Одинаковое_зерно_даёт_одинаковую_последовательность_потерь()
    {
        var first = Run(seed: 2024);
        var second = Run(seed: 2024);
        var other = Run(seed: 1);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);

        // Сравниваем сам набор дошедших датаграмм, а не только их количество.
        static string Run(int seed)
        {
            var inner = new FakeTransport();
            var emulated = new EmulatedTransport(inner, new EmulationOptions(LossPercent: 50, Seed: seed));

            for (int i = 0; i < 50; i++) emulated.Send(new byte[] { (byte)i }, Target);

            return string.Join(',', inner.Sent.Select(datagram => datagram[0]));
        }
    }

    [Fact]
    public void Без_эмуляции_датаграмма_уходит_как_есть()
    {
        var inner = new FakeTransport();
        var emulated = new EmulatedTransport(inner, new EmulationOptions());

        emulated.Send(new byte[] { 1, 2, 3 }, Target);

        Assert.Single(inner.Sent);
        Assert.Equal(new byte[] { 1, 2, 3 }, inner.Sent[0]);
    }
}
