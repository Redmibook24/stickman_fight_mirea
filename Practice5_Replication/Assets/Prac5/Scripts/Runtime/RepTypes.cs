using System;
using UnityEngine;

namespace Prac5
{
    /// <summary>Классы реплицируемых акторов арены.</summary>
    public enum EntityKind : byte
    {
        Tree,       // декорация, никогда не меняется
        Crate,      // разрушаемое укрытие: HP, разрушение, восстановление
        Pickup,     // аптечка/бонус: подобрали → исчезла → появилась снова
        Bot,        // бот: ходит по арене и стреляет
        Projectile, // снаряд бота (пул)
    }

    /// <summary>Реплицируемые поля актора. Используются как маска «грязных» полей и как маска записи.</summary>
    [Flags]
    public enum RepField : byte
    {
        None = 0,
        Position = 1 << 0,
        Yaw = 1 << 1,
        Velocity = 1 << 2,
        Hp = 1 << 3,
        Flags = 1 << 4,  // Active + номер появления (SpawnSeq)
        Close = 1 << 7,  // актор перестал быть релевантным для клиента (закрытие «канала»)
        All = Position | Yaw | Velocity | Hp | Flags,
    }

    /// <summary>Авторитетное состояние актора на сервере.</summary>
    public struct EntityState
    {
        public Vector3 Position;
        public float Yaw;          // градусы
        public Vector3 Velocity;
        public float Hp;
        public bool Active;
        public byte SpawnSeq;      // растёт при каждом «новом появлении» (выстрел, респаун) — клиент не интерполирует через него
    }

    /// <summary>Набор оптимизаций репликации. Baseline = None.</summary>
    [Flags]
    public enum RepOptimizations
    {
        None = 0,
        Grid = 1 << 0,          // Replication Graph: Grid Spatialization + дистанция отсечения
        Dormancy = 1 << 1,      // Net Dormancy: статичные акторы «спят» до FlushNetDormancy
        Push = 1 << 2,          // Push-Based Replication: только помеченные «грязными» поля
        Quantize = 1 << 3,      // квантование, аналог FVector_NetQuantize100 / NetQuantize10
        Frequency = 1 << 4,     // NetUpdateFrequency по классам акторов
        Priority = 1 << 5,      // доп. задание: NetPriority + бюджет байт на соединение
        ViewRelevancy = 1 << 6, // доп. задание: IsNetRelevantFor с учётом направления взгляда (только вместе с Grid)
        AllRequired = Grid | Dormancy | Push | Quantize | Frequency,
        Everything = AllRequired | Priority | ViewRelevancy,
    }

    /// <summary>Настройки репликации класса акторов (аналог свойств AActor в Unreal).</summary>
    public struct KindPolicy
    {
        public float UpdateHz;      // NetUpdateFrequency
        public float CullDistance;  // NetCullDistanceSquared = CullDistance²
        public bool Dormant;        // NetDormancy = DORM_Initial
        public float Priority;      // NetPriority
    }

    public static class RepPolicies
    {
        public static KindPolicy Get(EntityKind kind)
        {
            switch (kind)
            {
                case EntityKind.Tree:
                    return new KindPolicy { UpdateHz = 1f, CullDistance = 45f, Dormant = true, Priority = 0.5f };
                case EntityKind.Crate:
                    return new KindPolicy { UpdateHz = 4f, CullDistance = 55f, Dormant = true, Priority = 1f };
                case EntityKind.Pickup:
                    return new KindPolicy { UpdateHz = 4f, CullDistance = 55f, Dormant = true, Priority = 1.5f };
                case EntityKind.Bot:
                    return new KindPolicy { UpdateHz = 15f, CullDistance = 60f, Dormant = false, Priority = 3f };
                default:
                    return new KindPolicy { UpdateHz = 30f, CullDistance = 50f, Dormant = false, Priority = 3f };
            }
        }

        public const int KindCount = 5;

        public static string Describe(RepOptimizations o)
        {
            if (o == RepOptimizations.None) return "baseline";
            if (o == RepOptimizations.Everything) return "all+extra";
            if (o == RepOptimizations.AllRequired) return "all";
            return o.ToString().Replace(", ", "+").ToLowerInvariant();
        }

        /// <summary>Разбор режима из командной строки: baseline | all | everything | grid,push,...</summary>
        public static RepOptimizations Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return RepOptimizations.None;
            text = text.Trim().ToLowerInvariant();
            if (text == "baseline" || text == "none") return RepOptimizations.None;
            if (text == "all") return RepOptimizations.AllRequired;
            if (text == "everything" || text == "all+extra") return RepOptimizations.Everything;
            var result = RepOptimizations.None;
            foreach (string part in text.Split(',', '+'))
                if (Enum.TryParse(part.Trim(), true, out RepOptimizations o))
                    result |= o;
            return result;
        }
    }
}
