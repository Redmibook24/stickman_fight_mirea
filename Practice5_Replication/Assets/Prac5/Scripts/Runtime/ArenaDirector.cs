using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Нагруженная сцена: на сервере создаёт реплицируемые акторы (деревья, ящики-укрытия, аптечки,
    /// боты, снаряды) и симулирует их каждый сетевой тик, после чего запускает кадр репликации.
    /// Генерация детерминирована (seed), поэтому замеры «до» и «после» идут на одинаковой сцене.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class ArenaDirector : MonoBehaviour
    {
        public GameObject TreePrefab;
        public GameObject CratePrefab;
        public GameObject PickupPrefab;
        public GameObject BotPrefab;
        public GameObject ProjectilePrefab;

        public int TreeCount = 70;
        public int CrateCount = 30;
        public int PickupCount = 20;
        public int BotCount = 24;
        public int ProjectilePool = 24;
        public float ArenaHalfSize = 75f;
        public int Seed = 5;

        [Header("Боты")]
        public float BotSpeed = 3.5f;
        public float BotFireRange = 14f;
        public float ProjectileSpeed = 18f;
        public float ProjectileLife = 1.2f;
        public float ProjectileDamage = 25f;
        public float CrateRespawn = 8f;
        public float PickupRespawn = 6f;

        NetworkManager m_Net;
        ReplicationServer m_Replication;
        System.Random m_Rng;
        readonly List<ReplicatedEntity> m_Bots = new List<ReplicatedEntity>();
        readonly List<ReplicatedEntity> m_Crates = new List<ReplicatedEntity>();
        readonly List<ReplicatedEntity> m_Pickups = new List<ReplicatedEntity>();
        readonly List<ReplicatedEntity> m_Projectiles = new List<ReplicatedEntity>();
        readonly List<Vector3> m_PlayerPositions = new List<Vector3>();

        public int SpawnedCount { get; private set; }

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
            m_Replication = GetComponent<ReplicationServer>();
            // Префабы акторов регистрируются одинаково на сервере и клиенте до запуска сети.
            foreach (var prefab in new[] { TreePrefab, CratePrefab, PickupPrefab, BotPrefab, ProjectilePrefab })
                if (prefab != null)
                    m_Net.AddNetworkPrefab(prefab);
        }

        void OnEnable()
        {
            m_Net.OnServerStarted += OnServerStarted;
            m_Net.OnServerStopped += OnServerStopped;
        }

        void OnDisable()
        {
            m_Net.OnServerStarted -= OnServerStarted;
            m_Net.OnServerStopped -= OnServerStopped;
        }

        void OnServerStarted()
        {
            m_Rng = new System.Random(Seed);
            ReplicatedEntity.ServerEntities.Clear();

            for (int i = 0; i < TreeCount; i++)
                Spawn(TreePrefab, RandomPoint(), RandomYaw());
            for (int i = 0; i < CrateCount; i++)
            {
                var crate = Spawn(CratePrefab, RandomPoint(), RandomYaw());
                crate.State.Hp = 100f;
                m_Crates.Add(crate);
            }
            for (int i = 0; i < PickupCount; i++)
            {
                // половина аптечек — на маршрутах игроков, чтобы их действительно подбирали
                Vector3 p = i % 2 == 0 ? Autopilot.PointOnRoute(i / 2, (float)m_Rng.NextDouble() * 360f) : RandomPoint();
                m_Pickups.Add(Spawn(PickupPrefab, p, 0f));
            }
            for (int i = 0; i < BotCount; i++)
            {
                var bot = Spawn(BotPrefab, RandomPoint(), RandomYaw());
                bot.State.Hp = 100f;
                bot.Target = RandomPoint();
                bot.ActionAt = 1f + (float)m_Rng.NextDouble() * 2f;
                m_Bots.Add(bot);
            }
            for (int i = 0; i < ProjectilePool; i++)
            {
                var proj = Spawn(ProjectilePrefab, new Vector3(0f, 1f, 0f), 0f);
                proj.State.Active = false;
                m_Projectiles.Add(proj);
            }

            m_Net.NetworkTickSystem.Tick += OnTick;
            Prac4.NetLab.Log($"[СЕРВЕР] на арене создано реплицируемых акторов: {SpawnedCount}");
        }

        void OnServerStopped(bool wasHost)
        {
            if (m_Net.NetworkTickSystem != null)
                m_Net.NetworkTickSystem.Tick -= OnTick;
            m_Bots.Clear();
            m_Crates.Clear();
            m_Pickups.Clear();
            m_Projectiles.Clear();
            SpawnedCount = 0;
        }

        ReplicatedEntity Spawn(GameObject prefab, Vector3 position, float yaw)
        {
            var go = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f));
            var entity = go.GetComponent<ReplicatedEntity>();
            go.GetComponent<NetworkObject>().Spawn(true);
            entity.State.Active = true;
            SpawnedCount++;
            return entity;
        }

        Vector3 RandomPoint()
        {
            float x = ((float)m_Rng.NextDouble() * 2f - 1f) * ArenaHalfSize;
            float z = ((float)m_Rng.NextDouble() * 2f - 1f) * ArenaHalfSize;
            return new Vector3(x, 0f, z);
        }

        float RandomYaw() => (float)m_Rng.NextDouble() * 360f;

        void OnTick()
        {
            float dt = 1f / m_Net.NetworkTickSystem.TickRate;
            float now = m_Net.ServerTime.TimeAsFloat;

            m_PlayerPositions.Clear();
            foreach (var client in m_Net.ConnectedClientsList)
                if (client.PlayerObject != null)
                    m_PlayerPositions.Add(client.PlayerObject.transform.position);

            SimulateBots(dt, now);
            SimulateProjectiles(dt);
            SimulateCrates(now);
            SimulatePickups(now);

            if (m_Replication != null)
                m_Replication.Replicate(now);
        }

        void SimulateBots(float dt, float now)
        {
            foreach (var bot in m_Bots)
            {
                Vector3 pos = bot.State.Position;
                Vector3 to = bot.Target - pos;
                to.y = 0f;
                if (bot.RespawnAt > now)
                {
                    bot.SetVelocity(Vector3.zero); // стоит на месте
                }
                else if (to.magnitude < 1f)
                {
                    bot.Target = RandomPoint();
                    bot.RespawnAt = now + (float)m_Rng.NextDouble() * 1.5f; // пауза перед следующей точкой
                    bot.SetVelocity(Vector3.zero);
                }
                else
                {
                    Vector3 v = to.normalized * BotSpeed;
                    bot.SetVelocity(v);
                    bot.SetPosition(pos + v * dt);
                    bot.SetYaw(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg);
                }

                if (now >= bot.ActionAt)
                {
                    bot.ActionAt = now + 1.5f + (float)m_Rng.NextDouble() * 2f;
                    if (FindTarget(bot.State.Position, out Vector3 target))
                        Fire(bot.State.Position + Vector3.up, target);
                }
            }
        }

        bool FindTarget(Vector3 from, out Vector3 target)
        {
            float best = BotFireRange * BotFireRange;
            target = default;
            bool found = false;
            foreach (var p in m_PlayerPositions)
            {
                float d = (p - from).sqrMagnitude;
                if (d < best) { best = d; target = p; found = true; }
            }
            foreach (var crate in m_Crates)
            {
                if (!crate.State.Active) continue;
                float d = (crate.State.Position - from).sqrMagnitude;
                if (d < best) { best = d; target = crate.State.Position + Vector3.up * 0.75f; found = true; }
            }
            return found;
        }

        void Fire(Vector3 from, Vector3 target)
        {
            foreach (var proj in m_Projectiles)
            {
                if (proj.State.Active) continue;
                Vector3 dir = target - from;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) return;
                dir.Normalize();
                proj.SetPosition(from);
                proj.SetVelocity(dir * ProjectileSpeed);
                proj.SetYaw(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
                proj.SetActive(true, newSpawn: true);
                proj.LifeLeft = ProjectileLife;
                return;
            }
        }

        void SimulateProjectiles(float dt)
        {
            foreach (var proj in m_Projectiles)
            {
                if (!proj.State.Active) continue;
                // Полёт по прямой предсказуем: клиент экстраполирует сам, позиция не помечается грязной.
                Vector3 pos = proj.State.Position + proj.State.Velocity * dt;
                proj.SetPositionSilently(pos);
                proj.LifeLeft -= dt;

                bool hit = false;
                foreach (var crate in m_Crates)
                {
                    if (!crate.State.Active) continue;
                    Vector3 d = crate.State.Position - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude < 1.1f * 1.1f)
                    {
                        crate.SetHp(Mathf.Max(0f, crate.State.Hp - ProjectileDamage));
                        hit = true;
                        break;
                    }
                }
                if (hit || proj.LifeLeft <= 0f)
                {
                    proj.SetVelocity(Vector3.zero);
                    proj.SetPosition(pos);
                    proj.SetActive(false);
                }
            }
        }

        void SimulateCrates(float now)
        {
            foreach (var crate in m_Crates)
            {
                if (crate.State.Active && crate.State.Hp <= 0f)
                {
                    crate.SetActive(false);
                    crate.RespawnAt = now + CrateRespawn;
                }
                else if (!crate.State.Active && now >= crate.RespawnAt)
                {
                    crate.SetHp(100f);
                    crate.SetActive(true, newSpawn: true);
                }
            }
        }

        void SimulatePickups(float now)
        {
            foreach (var pickup in m_Pickups)
            {
                if (pickup.State.Active)
                {
                    foreach (var p in m_PlayerPositions)
                    {
                        Vector3 d = p - pickup.State.Position;
                        d.y = 0f;
                        if (d.sqrMagnitude < 1.6f * 1.6f)
                        {
                            pickup.SetActive(false);
                            pickup.RespawnAt = now + PickupRespawn;
                            break;
                        }
                    }
                }
                else if (now >= pickup.RespawnAt)
                {
                    pickup.SetActive(true, newSpawn: true);
                }
            }
        }
    }
}
