using StickmanFight.Reliable.Reliability;

namespace StickmanFight.Reliable.Tests;

/// <summary>Тесты окна дедупликации: повтор не должен давать повторный игровой эффект.</summary>
public class DeduplicationWindowTests
{
    [Fact]
    public void Новый_номер_принимается_повтор_отсекается()
    {
        var window = new DeduplicationWindow();

        Assert.True(window.TryRegister(42));
        Assert.False(window.TryRegister(42));
        Assert.False(window.TryRegister(42));
    }

    [Fact]
    public void Эффект_применяется_по_одному_разу_на_номер()
    {
        var window = new DeduplicationWindow();
        int shots = 0;
        ushort[] arrived = { 1, 2, 2, 3, 1, 3, 3, 4 };   // повторы из-за retransmission

        foreach (ushort sequence in arrived)
            if (window.TryRegister(sequence)) shots++;

        Assert.Equal(4, shots);
    }

    [Fact]
    public void Старые_номера_вытесняются_по_размеру_окна()
    {
        var window = new DeduplicationWindow(capacity: 3);
        foreach (ushort sequence in new ushort[] { 1, 2, 3, 4 }) window.TryRegister(sequence);

        Assert.Equal(3, window.Count);
        Assert.False(window.Contains(1));
        Assert.True(window.Contains(2));
        Assert.False(window.TryRegister(4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40_000)]
    public void Недопустимый_размер_окна_отвергается(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeduplicationWindow(capacity));
    }
}
