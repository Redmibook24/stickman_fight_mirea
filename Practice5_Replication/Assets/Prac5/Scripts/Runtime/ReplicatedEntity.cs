using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Реплицируемый актор арены. На сервере хранит авторитетное состояние и маску «грязных» полей
    /// (push-based: геймплейный код сам помечает, что изменилось). На клиенте получает записи
    /// репликации, показывает/прячет актор по релевантности и сглаживает движение.
    /// </summary>
    public class ReplicatedEntity : NetworkBehaviour
    {
        public EntityKind Kind;
        [Tooltip("Дочерний объект с моделью: двигается и скрывается, корневой объект не трогаем.")]
        public Transform Visual;

        public static readonly List<ReplicatedEntity> ServerEntities = new List<ReplicatedEntity>();
        public static readonly Dictionary<ulong, ReplicatedEntity> ClientEntities = new Dictionary<ulong, ReplicatedEntity>();
        public static int ClientVisibleCount;

        // ── сервер ──
        public EntityState State;
        public RepField DirtyMask;     // что изменилось с прошлого кадра репликации этого актора
        public float NextUpdateTime;   // NetUpdateFrequency: когда актор снова рассматривается
        public bool EligibleThisTick;
        public int Index = -1;         // позиция в ServerEntities
        public float RespawnAt;        // таймеры геймплея (используются ArenaDirector)
        public float ActionAt;
        public Vector3 Target;
        public float LifeLeft;

        // ── клиент ──
        struct Snapshot
        {
            public float Time;
            public Vector3 Position;
            public float Yaw;
        }

        EntityState m_Known;
        bool m_Relevant;
        float m_LastRecvTime;
        readonly List<Snapshot> m_Snapshots = new List<Snapshot>(8);
        Renderer[] m_Renderers;
        MaterialPropertyBlock m_Block;
        Color m_BaseColor;
        bool m_HasBaseColor;

        public const float InterpolationDelay = 0.12f;

        public bool ClientRelevant => m_Relevant;

        void Awake()
        {
            if (Visual == null)
                Visual = transform;
            m_Renderers = Visual.GetComponentsInChildren<Renderer>(true);
            m_Block = new MaterialPropertyBlock();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                State.Position = transform.position;
                State.Yaw = transform.eulerAngles.y;
                Index = ServerEntities.Count;
                ServerEntities.Add(this);
                DirtyMask = RepField.All;
            }
            else
            {
                ClientEntities[NetworkObjectId] = this;
                m_Known.Position = transform.position;
                SetVisible(false); // до первой записи репликации актор клиенту не известен
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                ServerEntities.Remove(this);
                for (int i = 0; i < ServerEntities.Count; i++)
                    ServerEntities[i].Index = i;
            }
            else
            {
                ClientEntities.Remove(NetworkObjectId);
                if (m_Relevant) ClientVisibleCount--;
            }
        }

        // ───────────── сервер: изменение состояния с пометкой «грязных» полей ─────────────

        public void SetPosition(Vector3 p)
        {
            if (p == State.Position) return;
            State.Position = p;
            DirtyMask |= RepField.Position;
        }

        /// <summary>Позиция меняется предсказуемо (снаряд летит по прямой) — клиент досчитает её сам, поле не грязное.</summary>
        public void SetPositionSilently(Vector3 p) => State.Position = p;

        public void SetYaw(float yaw)
        {
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, State.Yaw)) < 0.01f) return;
            State.Yaw = yaw;
            DirtyMask |= RepField.Yaw;
        }

        public void SetVelocity(Vector3 v)
        {
            if (v == State.Velocity) return;
            State.Velocity = v;
            DirtyMask |= RepField.Velocity;
        }

        public void SetHp(float hp)
        {
            if (Mathf.Approximately(hp, State.Hp)) return;
            State.Hp = hp;
            DirtyMask |= RepField.Hp;
        }

        public void SetActive(bool active, bool newSpawn = false)
        {
            if (active == State.Active && !newSpawn) return;
            State.Active = active;
            if (newSpawn) State.SpawnSeq++;
            DirtyMask |= RepField.Flags;
        }

        // ───────────── клиент: применение записей ─────────────

        public void ApplyRecord(RepField mask, in EntityState incoming, float now)
        {
            if ((mask & RepField.Close) != 0)
            {
                if (m_Relevant) ClientVisibleCount--;
                m_Relevant = false;
                m_Snapshots.Clear();
                return;
            }

            bool snap = !m_Relevant || ((mask & RepField.Flags) != 0 && incoming.SpawnSeq != m_Known.SpawnSeq);
            if (!m_Relevant) ClientVisibleCount++;
            m_Relevant = true;
            m_Known = incoming;
            m_LastRecvTime = now;

            if (snap)
                m_Snapshots.Clear();
            if (snap || (mask & (RepField.Position | RepField.Yaw)) != 0)
            {
                // После долгой паузы (актор спал/стоял) не тянем интерполяцию через весь интервал.
                if (m_Snapshots.Count > 0 && now - m_Snapshots[m_Snapshots.Count - 1].Time > 0.3f)
                {
                    var prev = m_Snapshots[m_Snapshots.Count - 1];
                    prev.Time = now - 0.07f;
                    m_Snapshots.Add(prev);
                }
                m_Snapshots.Add(new Snapshot { Time = now, Position = incoming.Position, Yaw = incoming.Yaw });
                if (m_Snapshots.Count > 8)
                    m_Snapshots.RemoveAt(0);
            }
        }

        /// <summary>Текущее известное клиенту состояние (для чтения поля, которое пришло раньше).</summary>
        public EntityState Known => m_Known;

        void Update()
        {
            if (!IsSpawned)
                return;

            if (IsServer)
            {
                // Хост рисует авторитетное состояние напрямую.
                ShowState(State.Active, State.Position, State.Yaw, State.Hp);
                return;
            }

            if (!m_Relevant || !m_Known.Active)
            {
                SetVisible(false);
                return;
            }

            Vector3 pos;
            float yaw;
            if (Kind == EntityKind.Projectile)
            {
                // Снаряд: экстраполяция по скорости от последней полученной записи.
                pos = m_Known.Position + m_Known.Velocity * (Time.time - m_LastRecvTime);
                yaw = m_Known.Yaw;
            }
            else
            {
                Interpolate(Time.time - InterpolationDelay, out pos, out yaw);
            }
            ShowState(true, pos, yaw, m_Known.Hp);
        }

        void Interpolate(float renderTime, out Vector3 pos, out float yaw)
        {
            int n = m_Snapshots.Count;
            if (n == 0)
            {
                pos = m_Known.Position;
                yaw = m_Known.Yaw;
                return;
            }
            var last = m_Snapshots[n - 1];
            if (renderTime >= last.Time)
            {
                // новых снимков нет (актор спит или обновляется редко) — держим последнее состояние
                pos = last.Position;
                yaw = last.Yaw;
                return;
            }
            for (int i = n - 1; i > 0; i--)
            {
                var a = m_Snapshots[i - 1];
                var b = m_Snapshots[i];
                if (renderTime >= a.Time)
                {
                    float t = b.Time > a.Time ? Mathf.Clamp01((renderTime - a.Time) / (b.Time - a.Time)) : 1f;
                    pos = Vector3.Lerp(a.Position, b.Position, t);
                    yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
                    return;
                }
            }
            pos = m_Snapshots[0].Position;
            yaw = m_Snapshots[0].Yaw;
        }

        void ShowState(bool active, Vector3 pos, float yaw, float hp)
        {
            SetVisible(active);
            if (!active)
                return;

            if (Kind == EntityKind.Pickup)
                yaw += Time.time * 90f; // вращение аптечки — чисто косметическое, считается локально
            Visual.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

            if (Kind == EntityKind.Crate)
            {
                // Повреждённый ящик темнеет — видно, что HP пришло по сети.
                if (!m_HasBaseColor && m_Renderers.Length > 0)
                {
                    m_BaseColor = m_Renderers[0].sharedMaterial.color;
                    m_HasBaseColor = true;
                }
                m_Block.SetColor("_BaseColor", Color.Lerp(Color.black, m_BaseColor, 0.35f + 0.65f * Mathf.Clamp01(hp / 100f)));
                foreach (var r in m_Renderers)
                    r.SetPropertyBlock(m_Block);
            }
        }

        void SetVisible(bool visible)
        {
            foreach (var r in m_Renderers)
                if (r.enabled != visible)
                    r.enabled = visible;
        }
    }
}
