using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Prac4
{
    /// <summary>
    /// Сетевое движение персонажа — аналог UCharacterMovementComponent:
    ///  • клиент-владелец (Autonomous Proxy) сразу применяет ввод локально (Client-Side Prediction)
    ///    и хранит сохранённые ходы (Saved Moves);
    ///  • на сервер уходит только ввод с порядковым номером, сервер сам симулирует и проверяет его;
    ///  • сервер возвращает авторитетное состояние и номер последнего обработанного ввода;
    ///  • при расхождении клиент перематывается к состоянию сервера и повторно применяет
    ///    неподтверждённые вводы (Server Reconciliation);
    ///  • чужие персонажи (Simulated Proxy) интерполируются по снимкам сервера (Network smoothing).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PredictedMovement : NetworkBehaviour
    {
        public MovementSettings Settings = new MovementSettings();
        [Tooltip("Дочерний объект с моделью — его сглаживаем, корневой объект всегда в точном состоянии.")]
        public Transform Visual;
        [Tooltip("Расхождение позиции (м), начиная с которого клиент выполняет коррекцию.")]
        public float CorrectionThreshold = 0.05f;
        public float VisualSmoothing = 12f;
        public float InterpolationDelay = 0.1f;
        [Tooltip("Сколько последних неподтверждённых вводов дублируется в каждом пакете (защита от потерь).")]
        public int RedundantInputs = 6;
        public float ProxySendRate = 20f;

        struct SavedMove
        {
            public InputCmd Cmd;
            public MoveState After;   // предсказанное состояние после применения этого ввода
            public float SentTime;
        }

        struct Snapshot
        {
            public float Time;
            public Vector3 Position;
            public Vector3 Facing;
            public bool Dashing;
        }

        CharacterController m_CC;
        PlayerInputReader m_Input;
        MoveState m_State;
        float m_Accumulator;

        // клиент-владелец
        uint m_NextSeq = 1;
        uint m_LastAck;
        readonly List<SavedMove> m_Saved = new List<SavedMove>();
        Vector3 m_VisualOffset;
        bool m_WaitingDashReaction;
        float m_DashPressTime;

        // сервер
        uint m_LastProcessed;
        float m_InputTokens = MaxInputTokens;
        float m_NextProxySend;
        float m_LastClampLogTime = -10f;
        const float MaxInputTokens = 10f;

        // Simulated Proxy
        readonly List<Snapshot> m_Snapshots = new List<Snapshot>();
        Vector3 m_ProxyFacing = Vector3.forward;

        public MoveState State => m_State;
        public bool HasServerState { get; private set; }
        public MoveState LastServerState { get; private set; }
        public bool IsDashingVisual { get; private set; }
        public Vector3 VisualFacing { get; private set; } = Vector3.forward;

        void Awake()
        {
            m_CC = GetComponent<CharacterController>();
            m_Input = GetComponent<PlayerInputReader>();
        }

        public override void OnNetworkSpawn()
        {
            m_State = new MoveState { Position = transform.position, Facing = Vector3.forward };
            if (m_Input != null)
                m_Input.enabled = IsOwner;
            NetLab.Log($"Персонаж игрока {OwnerClientId} появился. Роль: {RoleName()}");
        }

        public string RoleName()
        {
            if (IsServer && IsOwner) return "сервер + владелец (хост)";
            if (IsOwner) return "Autonomous Proxy (владелец)";
            if (IsServer) return "серверная копия (авторитет)";
            return "Simulated Proxy";
        }

        /// <summary>Сброс буферов при переключении режимов эксперимента.</summary>
        public void ResetPrediction()
        {
            m_Saved.Clear();
            if (HasServerState)
            {
                m_State = LastServerState;
                MovementSim.Teleport(m_CC, m_State.Position);
            }
        }

        void Update()
        {
            if (!IsSpawned)
                return;

            if (IsOwner && m_Input != null)
                m_Input.Poll();

            m_Accumulator += Time.deltaTime;
            int steps = 0;
            while (m_Accumulator >= MovementSettings.Dt && steps < 5)
            {
                m_Accumulator -= MovementSettings.Dt;
                Tick();
                steps++;
            }
            if (steps == 5)
                m_Accumulator = 0f;

            if (IsServer && Time.time >= m_NextProxySend)
            {
                m_NextProxySend = Time.time + 1f / ProxySendRate;
                ProxyStateRpc(m_State.Position, m_State.Facing, m_State.IsDashing);
            }

            if (!IsServer && !IsOwner)
                UpdateProxyInterpolation();

            UpdateVisual();
        }

        void Tick()
        {
            if (IsServer)
            {
                m_InputTokens = Mathf.Min(m_InputTokens + 1f, MaxInputTokens);
                if (IsOwner)
                {
                    // Персонаж хоста: ввод сразу применяется авторитетно, сеть не нужна.
                    var cmd = m_Input.BuildCmd(m_NextSeq++);
                    ServerApply(cmd);
                    m_LastProcessed = cmd.Seq;
                }
            }
            else if (IsOwner)
            {
                ClientOwnerTick();
            }
        }

        // ───────────────────────────── клиент-владелец ─────────────────────────────

        MovementSim.ClientOverrides ClientOverrides => new MovementSim.ClientOverrides
        {
            AllowDashStart = NetLab.PredictDash,
            IgnoreCooldown = NetLab.CheatIgnoreCooldown,
            SpeedMultiplier = NetLab.CheatSpeed ? 1.8f : 1f,
        };

        void ClientOwnerTick()
        {
            var cmd = m_Input.BuildCmd(m_NextSeq++);
            var saved = new SavedMove { Cmd = cmd, SentTime = Time.realtimeSinceStartup };

            if (cmd.Dash)
            {
                m_WaitingDashReaction = true;
                m_DashPressTime = m_Input.LastDashPressTime;
                NetLab.Log($"Клиент: нажат рывок (seq {cmd.Seq}), запрос отправлен на сервер");
            }

            if (NetLab.PredictMovement)
            {
                bool wasDashing = m_State.IsDashing;
                DashResult res = MovementSim.Step(m_CC, ref m_State, cmd, Settings, ClientOverrides);
                saved.After = m_State;

                if (cmd.Dash && NetLab.PredictDash && res != DashResult.Started)
                    NetLab.Log($"Клиент: локальная проверка не пропускает рывок — {MovementSim.Describe(res)}");
                CheckDashReaction(wasDashing);
            }

            m_Saved.Add(saved);
            if (m_Saved.Count > 256)
                m_Saved.RemoveAt(0);
            NetLab.PendingInputs = m_Saved.Count;

            SendInputs();
        }

        void SendInputs()
        {
            int count = Mathf.Min(RedundantInputs, InputBatch.MaxCount, m_Saved.Count);
            var cmds = new InputCmd[count];
            for (int i = 0; i < count; i++)
                cmds[i] = m_Saved[m_Saved.Count - count + i].Cmd;
            SubmitInputsRpc(new InputBatch { Cmds = cmds });
        }

        void CheckDashReaction(bool wasDashing)
        {
            if (!m_WaitingDashReaction || wasDashing || !m_State.IsDashing)
                return;
            m_WaitingDashReaction = false;
            NetLab.LastDashReactionMs = (Time.realtimeSinceStartup - m_DashPressTime) * 1000f;
            string how = NetLab.PredictDash && NetLab.PredictMovement
                ? "локальное предсказание"
                : "после подтверждения сервером";
            NetLab.Log($"Клиент: рывок начался через {NetLab.LastDashReactionMs:F0} мс после нажатия ({how})");
        }

        /// <summary>Авторитетное состояние от сервера для владельца (аналог ClientAdjustPosition / ClientAckGoodMove).</summary>
        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        void OwnerStateRpc(uint ackSeq, MoveState serverState)
        {
            if (IsServer)
                return;
            if (HasServerState && ackSeq <= m_LastAck)
                return; // устаревший пакет пришёл не по порядку

            m_LastAck = ackSeq;
            LastServerState = serverState;
            HasServerState = true;

            int idx = m_Saved.FindIndex(s => s.Cmd.Seq == ackSeq);
            if (idx >= 0)
                NetLab.LastAckLatencyMs = (Time.realtimeSinceStartup - m_Saved[idx].SentTime) * 1000f;

            Vector3 renderedBefore = transform.position + m_VisualOffset;
            bool wasDashing = m_State.IsDashing;

            if (!NetLab.PredictMovement)
            {
                // Вариант без предсказания: просто показываем то, что подтвердил сервер.
                m_Saved.RemoveAll(s => s.Cmd.Seq <= ackSeq);
                m_State = serverState;
                MovementSim.Teleport(m_CC, serverState.Position);
            }
            else
            {
                MoveState predicted = idx >= 0 ? m_Saved[idx].After : m_State;
                float error = Vector3.Distance(predicted.Position, serverState.Position);
                string reason = null;
                if (idx < 0) reason = "нет сохранённого хода";
                else if (predicted.DashTicksLeft != serverState.DashTicksLeft) reason = "состояние рывка";
                else if (predicted.CooldownTicksLeft != serverState.CooldownTicksLeft) reason = "кулдаун";
                else if (error > CorrectionThreshold) reason = "позиция";

                m_Saved.RemoveAll(s => s.Cmd.Seq <= ackSeq);

                if (reason != null)
                {
                    // Server Reconciliation: перемотка к авторитетному состоянию + повтор неподтверждённых вводов.
                    m_State = serverState;
                    MovementSim.Teleport(m_CC, serverState.Position);
                    var ov = ClientOverrides;
                    for (int i = 0; i < m_Saved.Count; i++)
                    {
                        var sm = m_Saved[i];
                        MovementSim.Step(m_CC, ref m_State, sm.Cmd, Settings, ov);
                        sm.After = m_State;
                        m_Saved[i] = sm;
                    }

                    NetLab.Corrections++;
                    NetLab.LastCorrectionError = error;
                    NetLab.Log($"[КОРРЕКЦИЯ] seq {ackSeq}: расхождение {error:F2} м ({reason}); " +
                               $"повторно применено вводов: {m_Saved.Count}");
                }
            }

            NetLab.PendingInputs = m_Saved.Count;
            CheckDashReaction(wasDashing);

            // Сглаживание: модель плавно догоняет исправленную позицию, а не телепортируется.
            m_VisualOffset = renderedBefore - transform.position;
            if (m_VisualOffset.sqrMagnitude > 9f)
                m_VisualOffset = Vector3.zero;
        }

        [Rpc(SendTo.Owner)]
        void DashRejectedRpc(uint seq, int result)
        {
            m_WaitingDashReaction = false;
            NetLab.ServerRejectsSeen++;
            NetLab.Log($"Клиент: СЕРВЕР ОТКЛОНИЛ рывок seq {seq} — {MovementSim.Describe((DashResult)result)}");
        }

        // ───────────────────────────────── сервер ─────────────────────────────────

        /// <summary>Клиент передаёт только ввод (аналог ServerMove). Принимается лишь от владельца.</summary>
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitInputsRpc(InputBatch batch, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId || batch.Cmds == null)
                return;

            bool processed = false;
            foreach (var cmd in batch.Cmds)
            {
                if (cmd.Seq <= m_LastProcessed)
                    continue; // дубликат, уже обработан
                if (m_LastProcessed != 0 && cmd.Seq > m_LastProcessed + 1)
                {
                    uint lost = cmd.Seq - m_LastProcessed - 1;
                    NetLab.ServerLostInputs += (int)lost;
                    NetLab.Log($"[СЕРВЕР] игрок {OwnerClientId}: потеряно вводов {lost} (все копии пакета не дошли)");
                }
                if (m_InputTokens < 1f)
                {
                    NetLab.Log($"[СЕРВЕР] игрок {OwnerClientId}: слишком частый ввод — лишние команды отброшены");
                    break;
                }
                m_InputTokens -= 1f;
                ServerApply(cmd);
                m_LastProcessed = cmd.Seq;
                processed = true;
            }

            if (processed)
                OwnerStateRpc(m_LastProcessed, m_State);
        }

        /// <summary>Авторитетное применение ввода сервером с проверками.</summary>
        void ServerApply(InputCmd cmd)
        {
            if (cmd.Move.magnitude > 1.01f)
            {
                NetLab.ServerRejects++;
                if (Time.time - m_LastClampLogTime > 1f)
                {
                    m_LastClampLogTime = Time.time;
                    NetLab.Log($"[СЕРВЕР] игрок {OwnerClientId}: длина вектора движения {cmd.Move.magnitude:F2} > 1 — " +
                               "обрезана (попытка ускорения отклонена)");
                }
                cmd.Move = Vector2.ClampMagnitude(cmd.Move, 1f);
            }

            DashResult res = MovementSim.Step(m_CC, ref m_State, cmd, Settings, MovementSim.ClientOverrides.Server);

            if (!cmd.Dash)
                return;
            if (res == DashResult.Started)
            {
                NetLab.Log($"[СЕРВЕР] игрок {OwnerClientId}: рывок seq {cmd.Seq} проверен и выполнен");
            }
            else
            {
                NetLab.ServerRejects++;
                NetLab.Log($"[СЕРВЕР] игрок {OwnerClientId}: рывок seq {cmd.Seq} ОТКЛОНЁН — {MovementSim.Describe(res)}");
                if (!IsOwner)
                    DashRejectedRpc(cmd.Seq, (int)res);
            }
        }

        // ──────────────────────────── Simulated Proxy ─────────────────────────────

        [Rpc(SendTo.NotServer, Delivery = RpcDelivery.Unreliable)]
        void ProxyStateRpc(Vector3 position, Vector3 facing, bool dashing)
        {
            if (IsOwner)
                return;
            m_Snapshots.Add(new Snapshot { Time = Time.time, Position = position, Facing = facing, Dashing = dashing });
            if (m_Snapshots.Count > 32)
                m_Snapshots.RemoveAt(0);
        }

        void UpdateProxyInterpolation()
        {
            if (m_Snapshots.Count == 0)
                return;

            float renderTime = Time.time - InterpolationDelay;
            Snapshot a = m_Snapshots[0], b = m_Snapshots[0];
            for (int i = 0; i < m_Snapshots.Count; i++)
            {
                if (m_Snapshots[i].Time <= renderTime)
                    a = m_Snapshots[i];
                b = m_Snapshots[i];
                if (m_Snapshots[i].Time > renderTime)
                    break;
            }

            float t = b.Time > a.Time ? Mathf.Clamp01((renderTime - a.Time) / (b.Time - a.Time)) : 1f;
            transform.position = Vector3.Lerp(a.Position, b.Position, t);
            m_ProxyFacing = b.Facing;
            IsDashingVisual = a.Dashing || b.Dashing;

            while (m_Snapshots.Count > 2 && m_Snapshots[1].Time < renderTime)
                m_Snapshots.RemoveAt(0);

            // чтобы локальная симуляция владельца сразу видела новую позицию чужого коллайдера
            Physics.SyncTransforms();
        }

        // ───────────────────────────────── визуал ─────────────────────────────────

        void UpdateVisual()
        {
            if (Visual == null)
                return;

            float k = 1f - Mathf.Exp(-VisualSmoothing * Time.deltaTime);
            if (IsOwner)
            {
                m_VisualOffset = Vector3.Lerp(m_VisualOffset, Vector3.zero, k);
                Visual.position = transform.position + m_VisualOffset;
                VisualFacing = m_State.Facing;
                IsDashingVisual = m_State.IsDashing;
            }
            else if (IsServer)
            {
                // на сервере чужой ввод приходит пачками — сглаживаем только картинку
                Visual.position = Vector3.Lerp(Visual.position, transform.position, k);
                VisualFacing = m_State.Facing;
                IsDashingVisual = m_State.IsDashing;
            }
            else
            {
                Visual.position = transform.position;
                VisualFacing = m_ProxyFacing;
            }

            if (VisualFacing.sqrMagnitude > 0.01f)
                Visual.rotation = Quaternion.Slerp(Visual.rotation, Quaternion.LookRotation(VisualFacing), k);
        }
    }
}
