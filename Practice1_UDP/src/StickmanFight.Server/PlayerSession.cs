using System.Net;

namespace StickmanFight.Net.Server;

/// <summary>Авторитетное состояние одного игрока на сервере.</summary>
public sealed class PlayerSession
{
    public ushort Id { get; }
    public string Name { get; }
    public IPEndPoint EndPoint { get; }

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public ushort Hp { get; set; } = GameRules.MaxHp;
    public ushort Ammo { get; set; } = GameRules.StartAmmo;

    /// <summary>Номер последнего принятого пакета — по нему ловим дубликаты и «опоздавшие» датаграммы.</summary>
    public ushort LastSequence { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastShotUtc { get; set; } = DateTime.MinValue;

    public PlayerSession(ushort id, string name, IPEndPoint endPoint)
    {
        Id = id;
        Name = name;
        EndPoint = endPoint;
    }

    public StatePayload ToState(ushort ackSequence) =>
        new(Id, X, Y, Z, Hp, Ammo, ackSequence);

    public override string ToString() => $"#{Id} «{Name}» {EndPoint}";
}

/// <summary>Правила симуляции. Сервер авторитетен: клиент только просит, решает сервер.</summary>
public static class GameRules
{
    public const ushort MaxHp = 100;
    public const ushort StartAmmo = 30;

    /// <summary>Половина размера арены по каждой оси, метры.</summary>
    public const float ArenaHalfSize = 50f;

    /// <summary>Максимальное смещение за одну команду MOVEMENT (анти-чит «телепорт»).</summary>
    public const float MaxStep = 5f;

    /// <summary>Минимальная пауза между выстрелами, мс.</summary>
    public const int ShootCooldownMs = 200;

    public static float Clamp(float value) =>
        Math.Clamp(value, -ArenaHalfSize, ArenaHalfSize);
}
