using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Prac5
{
    /// <summary>
    /// Серверный слой репликации акторов арены — то, что в Unreal делают UNetDriver / UReplicationGraph.
    /// Каждый сетевой тик решает, каким клиентам, какие акторы и какие их поля отправить.
    ///
    /// Baseline (Optimizations = None): каждый актор каждый тик (50 Гц) целиком, без сжатия, всем клиентам.
    /// Оптимизации включаются флагами <see cref="RepOptimizations"/> по отдельности или вместе.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class ReplicationServer : MonoBehaviour
    {
        public RepOptimizations Optimizations = RepOptimizations.None;
        [Tooltip("Размер ячейки сетки Grid Spatialization, м.")]
        public float CellSize = 20f;
        [Tooltip("Запас дистанции, после которого уже релевантный актор закрывается (гистерезис).")]
        public float CullHysteresis = 1.1f;
        [Tooltip("Ближе этой дистанции актор релевантен независимо от направления взгляда.")]
        public float ViewNearRadius = 15f;
        [Tooltip("Акторы за спиной игрока релевантны только в этой доле дистанции отсечения.")]
        public float BehindCullFactor = 0.5f;
        [Tooltip("Бюджет байт репликации на клиента за тик (режим Priority). 1000 Б × 50 Гц ≈ 49 КБ/с.")]
        public int BudgetBytesPerTick = 1000;

        const int ChunkLimit = 1100; // одно сообщение помещается в один пакет Netcode без фрагментации

        class Connection
        {
            public ulong ClientId;
            public RepField[] Pending = new RepField[0];
            public bool[] Relevant = new bool[0];
            public float[] LastSent = new float[0];
            public Vector3 ViewPos;
            public Vector3 ViewDir = Vector3.forward;
            public bool HasView;

            public void Resize(int n)
            {
                if (Pending.Length >= n) return;
                System.Array.Resize(ref Pending, n);
                System.Array.Resize(ref Relevant, n);
                System.Array.Resize(ref LastSent, n);
            }
        }

        NetworkManager m_Net;
        readonly List<Connection> m_Connections = new List<Connection>();
        SpatialGrid m_Grid;
        readonly List<int> m_Gathered = new List<int>();
        readonly List<int> m_Candidates = new List<int>();
        bool[] m_NowRelevant = new bool[0];
        float[] m_Score = new float[0];
        RepOptimizations m_Applied;
        float m_MaxCull;
        readonly Stopwatch m_Watch = new Stopwatch();

        public int ConnectionCount => m_Connections.Count;

        bool Has(RepOptimizations o) => (Optimizations & o) != 0;

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
            m_Grid = new SpatialGrid(CellSize);
            for (int k = 0; k < RepPolicies.KindCount; k++)
                m_MaxCull = Mathf.Max(m_MaxCull, RepPolicies.Get((EntityKind)k).CullDistance);
        }

        void OnEnable()
        {
            m_Net = GetComponent<NetworkManager>();
            m_Net.OnServerStarted += OnServerStarted;
            m_Net.OnClientDisconnectCallback += OnClientDisconnect;
        }

        void OnDisable()
        {
            if (m_Net == null) return;
            m_Net.OnServerStarted -= OnServerStarted;
            m_Net.OnClientDisconnectCallback -= OnClientDisconnect;
        }

        void OnServerStarted()
        {
            m_Connections.Clear();
            m_Applied = Optimizations;
            m_Net.SceneManager.OnSceneEvent += OnSceneEvent;
        }

        void OnSceneEvent(SceneEvent e)
        {
            // Реплицируем клиенту только после того, как он закончил синхронизацию и знает все акторы.
            if (e.SceneEventType == SceneEventType.SynchronizeComplete && e.ClientId != NetworkManager.ServerClientId)
            {
                if (m_Connections.Exists(c => c.ClientId == e.ClientId))
                    return;
                var conn = new Connection { ClientId = e.ClientId };
                conn.Resize(ReplicatedEntity.ServerEntities.Count);
                m_Connections.Add(conn);
                Prac4.NetLab.Log($"[РЕПЛИКАЦИЯ] клиент {e.ClientId} синхронизирован, начинаем репликацию акторов");
            }
        }

        void OnClientDisconnect(ulong clientId)
        {
            m_Connections.RemoveAll(c => c.ClientId == clientId);
        }

        /// <summary>Сбрасывает состояние соединений при смене набора оптимизаций на лету.</summary>
        void ResetConnections()
        {
            int n = ReplicatedEntity.ServerEntities.Count;
            foreach (var c in m_Connections)
            {
                c.Resize(n);
                for (int i = 0; i < n; i++)
                {
                    // Клиент мог видеть всё — считаем всё открытым: лишнее закроется сеткой на этом же тике.
                    c.Relevant[i] = true;
                    c.Pending[i] = RepField.All;
                }
            }
            foreach (var e in ReplicatedEntity.ServerEntities)
                e.NextUpdateTime = 0f;
            m_Applied = Optimizations;
        }

        /// <summary>Один кадр репликации. Вызывается ArenaDirector после симуляции тика.</summary>
        public void Replicate(float now)
        {
            if (!m_Net.IsServer)
                return;
            if (m_Applied != Optimizations)
                ResetConnections();

            m_Watch.Restart();
            var entities = ReplicatedEntity.ServerEntities;
            int n = entities.Count;
            if (m_NowRelevant.Length < n)
            {
                m_NowRelevant = new bool[n];
                m_Score = new float[n];
            }

            bool push = Has(RepOptimizations.Push);
            bool dormancy = Has(RepOptimizations.Dormancy);
            bool frequency = Has(RepOptimizations.Frequency);
            bool grid = Has(RepOptimizations.Grid);

            // 1. Какие акторы рассматриваются в этом тике и какие их поля нужно переслать.
            for (int i = 0; i < n; i++)
            {
                var e = entities[i];
                e.EligibleThisTick = false;
                var policy = RepPolicies.Get(e.Kind);

                if (grid)
                    m_Grid.Update(i, e.State.Position);

                // Net Dormancy: спящий актор не обрабатывается вовсе, пока геймплей не «разбудит» его
                // изменением состояния (FlushNetDormancy).
                if (dormancy && policy.Dormant && e.DirtyMask == RepField.None)
                {
                    RepStats.DormantSkipped++;
                    continue;
                }
                // NetUpdateFrequency: актор рассматривается не чаще заданной частоты, изменения копятся.
                if (frequency)
                {
                    if (now < e.NextUpdateTime)
                        continue;
                    e.NextUpdateTime = Mathf.Max(e.NextUpdateTime + 1f / policy.UpdateHz, now);
                }

                e.EligibleThisTick = true;
                RepStats.ActorsConsidered++;

                // Push-based: только помеченные поля. Без него — всё состояние целиком (опрос свойств).
                RepField fields = push ? e.DirtyMask : RepField.All;
                e.DirtyMask = RepField.None;
                if (fields == RepField.None)
                    continue;
                foreach (var c in m_Connections)
                {
                    c.Resize(n);
                    c.Pending[i] |= fields;
                }
            }

            // 2. Для каждого клиента: релевантность, отбор, сериализация.
            foreach (var c in m_Connections)
            {
                c.Resize(n);
                UpdateView(c);
                ComputeRelevancy(c, n, grid, now);
                SendToConnection(c, n, now, push);
            }

            RepStats.Ticks++;
            m_Watch.Stop();
            RepStats.ReplicationMs += m_Watch.Elapsed.TotalMilliseconds;
        }

        void UpdateView(Connection c)
        {
            c.HasView = false;
            if (!m_Net.ConnectedClients.TryGetValue(c.ClientId, out var client) || client.PlayerObject == null)
                return;
            var player = client.PlayerObject;
            var movement = player.GetComponent<Prac4.PredictedMovement>();
            c.ViewPos = movement != null ? movement.State.Position : player.transform.position;
            c.ViewDir = movement != null ? movement.State.Facing : player.transform.forward;
            c.HasView = true;
        }

        void ComputeRelevancy(Connection c, int n, bool grid, float now)
        {
            var entities = ReplicatedEntity.ServerEntities;

            if (!grid || !c.HasView)
            {
                // Без Replication Graph все акторы релевантны всем клиентам.
                for (int i = 0; i < n; i++)
                    m_NowRelevant[i] = true;
            }
            else
            {
                for (int i = 0; i < n; i++)
                    m_NowRelevant[i] = false;
                m_Gathered.Clear();
                m_Grid.Gather(c.ViewPos, m_MaxCull * CullHysteresis, m_Gathered);
                bool view = Has(RepOptimizations.ViewRelevancy);
                foreach (int i in m_Gathered)
                    m_NowRelevant[i] = IsNetRelevantFor(entities[i], c, c.Relevant[i], view);
            }

            for (int i = 0; i < n; i++)
            {
                if (m_NowRelevant[i] && !c.Relevant[i])
                {
                    // Актор вошёл в зону: «открываем канал» — клиент получает полное состояние.
                    c.Relevant[i] = true;
                    c.Pending[i] = RepField.All;
                    c.LastSent[i] = now - 1f;
                    RepStats.OpenRecords++;
                }
                else if (!m_NowRelevant[i] && c.Relevant[i])
                {
                    c.Relevant[i] = false;
                    c.Pending[i] = RepField.Close;
                }
                else if (!m_NowRelevant[i])
                {
                    c.Pending[i] = RepField.None; // изменения нерелевантного актора не копим
                }
            }

            for (int i = 0; i < n; i++)
                if (c.Relevant[i])
                    RepStats.RelevantPairs++;
        }

        /// <summary>
        /// Аналог AActor::IsNetRelevantFor: дистанция отсечения класса (NetCullDistanceSquared) и, в режиме
        /// ViewRelevancy, направление взгляда игрока: за спиной дальняя декорация не нужна.
        /// </summary>
        bool IsNetRelevantFor(ReplicatedEntity e, Connection c, bool wasRelevant, bool useView)
        {
            var policy = RepPolicies.Get(e.Kind);
            Vector3 d = e.State.Position - c.ViewPos;
            d.y = 0f;
            float dist2 = d.sqrMagnitude;
            float cull = policy.CullDistance * (wasRelevant ? CullHysteresis : 1f);

            if (useView && e.Kind != EntityKind.Projectile && dist2 > ViewNearRadius * ViewNearRadius)
            {
                Vector3 facing = c.ViewDir;
                facing.y = 0f;
                if (facing.sqrMagnitude > 0.01f && Vector3.Dot(facing.normalized, d.normalized) < -0.25f)
                    cull *= BehindCullFactor; // актор позади игрока
            }
            return dist2 <= cull * cull;
        }

        void SendToConnection(Connection c, int n, float now, bool push)
        {
            var entities = ReplicatedEntity.ServerEntities;
            bool quantize = Has(RepOptimizations.Quantize);
            bool priority = Has(RepOptimizations.Priority);

            m_Candidates.Clear();
            for (int i = 0; i < n; i++)
                if (c.Pending[i] != RepField.None)
                    m_Candidates.Add(i);
            if (m_Candidates.Count == 0)
                return;

            if (priority)
            {
                // NetPriority: важность класса × время ожидания × близость к игроку.
                foreach (int i in m_Candidates)
                {
                    var e = entities[i];
                    float wait = Mathf.Max(now - c.LastSent[i], 0.02f);
                    float dist = c.HasView ? Vector3.Distance(e.State.Position, c.ViewPos) : 0f;
                    float near = dist < 15f ? 2f : 1f;
                    m_Score[i] = (c.Pending[i] & RepField.Close) != 0 ? float.MaxValue
                        : RepPolicies.Get(e.Kind).Priority * wait * near;
                }
                m_Candidates.Sort((a, b) => m_Score[b].CompareTo(m_Score[a]));
            }

            // Надёжная доставка нужна, когда клиент не получит то же состояние снова в следующем тике:
            // при дельтах (push), сне (dormancy) и закрытии каналов (grid).
            bool reliable = push || Has(RepOptimizations.Dormancy) || Has(RepOptimizations.Grid);
            var delivery = reliable ? NetworkDelivery.ReliableSequenced : NetworkDelivery.Unreliable;
            byte header = (byte)((quantize ? RepWire.HeaderQuantized : 0) | (push ? RepWire.HeaderDelta : 0));

            var writer = new FastBufferWriter(ChunkLimit + RepWire.MaxRecordSize, Allocator.Temp);
            int budgetUsed = 0;
            try
            {
                writer.WriteValueSafe(header);
                RepStats.HeaderBytes++;
                bool hasRecords = false;

                foreach (int i in m_Candidates)
                {
                    RepField pending = c.Pending[i];
                    bool close = (pending & RepField.Close) != 0;
                    if (priority && !close && budgetUsed >= BudgetBytesPerTick)
                    {
                        RepStats.StarvedRecords++;
                        continue; // останется в Pending и уйдёт в следующих тиках с более высоким приоритетом
                    }

                    if (writer.Length + RepWire.MaxRecordSize > ChunkLimit)
                    {
                        Flush(c.ClientId, ref writer, delivery);
                        writer.WriteValueSafe(header);
                        RepStats.HeaderBytes++;
                        hasRecords = false;
                    }

                    var e = entities[i];
                    RepField mask = close ? RepField.Close : (push ? pending : RepField.All);
                    int before = writer.Length;
                    RepWire.WriteRecord(ref writer, e.NetworkObjectId, mask, e.State, quantize);
                    int size = writer.Length - before;
                    budgetUsed += size;
                    hasRecords = true;

                    RepStats.BytesByKind[(int)e.Kind] += size;
                    RepStats.RecordsByKind[(int)e.Kind]++;
                    if (close) RepStats.CloseRecords++;
                    c.Pending[i] = RepField.None;
                    c.LastSent[i] = now;
                }

                if (hasRecords)
                    Flush(c.ClientId, ref writer, delivery);
                else
                    RepStats.HeaderBytes--;
            }
            finally
            {
                writer.Dispose();
            }
        }

        void Flush(ulong clientId, ref FastBufferWriter writer, NetworkDelivery delivery)
        {
            m_Net.CustomMessagingManager.SendNamedMessage(RepWire.MessageName, clientId, writer, delivery);
            writer.Seek(0);
            writer.Truncate();
        }
    }
}
