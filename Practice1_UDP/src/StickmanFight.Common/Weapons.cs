namespace StickmanFight.Net;

/// <summary>Оружие из игры «Битва жердяев» (значения урона взяты из README проекта).</summary>
public static class Weapons
{
    public const byte Rifle = 1;            // автоматическая винтовка
    public const byte GrenadeLauncher = 2;  // гранатомёт
    public const byte Melee = 3;            // удар в ближнем бою

    public static string Name(byte weaponId) => weaponId switch
    {
        Rifle           => "винтовка",
        GrenadeLauncher => "гранатомёт",
        Melee           => "ближний бой",
        _               => $"неизвестное оружие #{weaponId}",
    };

    public static ushort Damage(byte weaponId) => weaponId switch
    {
        Rifle           => 25,
        GrenadeLauncher => 40,
        Melee           => 15,
        _               => 0,
    };
}
