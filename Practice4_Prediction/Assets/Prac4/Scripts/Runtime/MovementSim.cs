using System;
using UnityEngine;

namespace Prac4
{
    [Serializable]
    public class MovementSettings
    {
        public const int TickRate = 50;                 // шагов симуляции в секунду
        public const float Dt = 1f / TickRate;

        public float WalkSpeed = 6f;
        public float DashSpeed = 22f;
        public int DashDurationTicks = 10;              // 0.2 с
        public int DashCooldownTicks = 75;              // 1.5 с
        public float Gravity = -25f;
    }

    /// <summary>
    /// Детерминированный шаг симуляции движения — общий код для клиента и сервера
    /// (аналог PerformMovement в UCharacterMovementComponent). Сервер вызывает его как авторитет,
    /// клиент — для предсказания и для повторного проигрывания сохранённых вводов.
    /// </summary>
    public static class MovementSim
    {
        static readonly Collider[] s_Hits = new Collider[8];

        /// <summary>Параметры, которые меняет только клиент в демонстрационных «читах».</summary>
        public struct ClientOverrides
        {
            public bool AllowDashStart;   // false — клиент не запускает рывок сам, ждёт сервер
            public bool IgnoreCooldown;   // «чит»: клиент считает, что кулдауна нет
            public float SpeedMultiplier; // «чит»: клиент двигается быстрее

            public static ClientOverrides Server => new ClientOverrides { AllowDashStart = true, SpeedMultiplier = 1f };
        }

        /// <summary>Мгновенно ставит контроллер в позицию состояния (перемотка перед повтором ввода).</summary>
        public static void Teleport(CharacterController cc, Vector3 position)
        {
            cc.transform.position = position;
            Physics.SyncTransforms();
        }

        public static bool IsGrounded(CharacterController cc)
        {
            Transform t = cc.transform;
            float radius = cc.radius * 0.95f;
            Vector3 feet = t.position + cc.center + Vector3.down * (cc.height * 0.5f - cc.radius + 0.15f);
            int n = Physics.OverlapSphereNonAlloc(feet, radius, s_Hits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (s_Hits[i] != cc)
                    return true;
            return false;
        }

        /// <summary>Проверка рывка. Именно эту функцию сервер использует для отклонения недопустимого действия.</summary>
        public static DashResult ValidateDash(in MoveState s, bool grounded, bool ignoreCooldown)
        {
            if (s.DashTicksLeft > 0) return DashResult.RejectedAlreadyDashing;
            if (s.CooldownTicksLeft > 0 && !ignoreCooldown) return DashResult.RejectedCooldown;
            if (!grounded) return DashResult.RejectedNotGrounded;
            return DashResult.Started;
        }

        /// <summary>Один шаг симуляции. Контроллер должен стоять в s.Position.</summary>
        public static DashResult Step(CharacterController cc, ref MoveState s, in InputCmd cmd,
                                      MovementSettings cfg, ClientOverrides ov)
        {
            bool grounded = IsGrounded(cc);

            Vector2 m = Vector2.ClampMagnitude(cmd.Move, 1f);
            Vector3 move = new Vector3(m.x, 0f, m.y);
            if (move.sqrMagnitude > 0.0001f)
                s.Facing = move.normalized;
            if (s.Facing.sqrMagnitude < 0.5f)
                s.Facing = Vector3.forward;

            if (s.CooldownTicksLeft > 0)
                s.CooldownTicksLeft--;

            DashResult result = DashResult.None;
            if (cmd.Dash && ov.AllowDashStart)
            {
                result = ValidateDash(s, grounded, ov.IgnoreCooldown);
                if (result == DashResult.Started)
                {
                    s.DashTicksLeft = cfg.DashDurationTicks;
                    s.CooldownTicksLeft = cfg.DashCooldownTicks;
                    s.DashDir = s.Facing;
                }
            }

            Vector3 horizontal;
            if (s.DashTicksLeft > 0)
            {
                horizontal = s.DashDir * cfg.DashSpeed;
                s.DashTicksLeft--;
                s.VelY = 0f; // рывок горизонтальный
            }
            else
            {
                horizontal = move * (cfg.WalkSpeed * ov.SpeedMultiplier);
                if (grounded && s.VelY < 0f)
                    s.VelY = -2f;
                s.VelY += cfg.Gravity * MovementSettings.Dt;
            }

            cc.Move((horizontal + Vector3.up * s.VelY) * MovementSettings.Dt);
            s.Position = cc.transform.position;

            // защита от падения за карту: сервер вернёт персонажа на точку появления
            if (s.Position.y < -20f)
            {
                Teleport(cc, new Vector3(0f, 1.5f, 0f));
                s.Position = cc.transform.position;
                s.VelY = 0f;
            }
            return result;
        }

        public static string Describe(DashResult r) => r switch
        {
            DashResult.RejectedCooldown => "кулдаун не истёк",
            DashResult.RejectedNotGrounded => "персонаж не на земле",
            DashResult.RejectedAlreadyDashing => "рывок уже выполняется",
            DashResult.Started => "рывок начат",
            _ => "-",
        };
    }
}
