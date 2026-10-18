using Unity.Multiplayer.Tools.NetworkSimulator.Runtime;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Prac4
{
    /// <summary>
    /// Панель эксперимента: запуск Host / Client, профили эмуляции сети (Network Simulator из
    /// Multiplayer Tools), переключение предсказания и «читов», вывод метрик и журнала событий.
    /// </summary>
    public class NetLabHud : MonoBehaviour
    {
        public NetworkManager Network;
        public NetworkSimulator Simulator;

        string m_Address = "127.0.0.1";
        string m_Port = "7777";
        bool m_ShowHelp = true;

        void Awake()
        {
            if (Network == null) Network = GetComponent<NetworkManager>();
            if (Simulator == null) Simulator = GetComponent<NetworkSimulator>();
        }

        void Start()
        {
            ApplyProfile(0);
        }

        // ─────────────────────────────── профили сети ───────────────────────────────

        static readonly (string name, int delay, int jitter, int loss)[] Profiles =
        {
            ("T1: без эмуляции", 0, 0, 0),
            ("T2/T3: 100 мс, 0% потерь", 100, 0, 0),
            ("T4/T5: 175±25 мс, 4% потерь", 175, 25, 4),
        };

        void ApplyProfile(int index)
        {
            var p = Profiles[index];
            NetLab.ProfileName = p.name;
            if (Simulator != null)
            {
                Simulator.ConnectionPreset = NetworkSimulatorPreset.Create(p.name, "Prac4",
                    packetDelayMs: p.delay, packetJitterMs: p.jitter, packetLossPercent: p.loss);
            }
            NetLab.Log($"Профиль сети: {p.name} (PacketDelay={p.delay} мс, Jitter={p.jitter} мс, Loss={p.loss}%)");
        }

        // ─────────────────────────────── горячие клавиши ───────────────────────────────

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;

            if (kb.f1Key.wasPressedThisFrame) ApplyProfile(0);
            if (kb.f2Key.wasPressedThisFrame) ApplyProfile(1);
            if (kb.f3Key.wasPressedThisFrame) ApplyProfile(2);
            if (kb.hKey.wasPressedThisFrame) m_ShowHelp = !m_ShowHelp;

            if (kb.digit1Key.wasPressedThisFrame)
            {
                NetLab.PredictDash = !NetLab.PredictDash;
                ResetLocalPrediction();
                NetLab.Log($"Предсказание РЫВКА: {(NetLab.PredictDash ? "ВКЛ" : "ВЫКЛ (рывок только после ответа сервера)")}");
            }
            if (kb.digit2Key.wasPressedThisFrame)
            {
                NetLab.PredictMovement = !NetLab.PredictMovement;
                ResetLocalPrediction();
                NetLab.Log($"Предсказание ДВИЖЕНИЯ: {(NetLab.PredictMovement ? "ВКЛ" : "ВЫКЛ (позиция только от сервера)")}");
            }
            if (kb.cKey.wasPressedThisFrame)
            {
                NetLab.CheatIgnoreCooldown = !NetLab.CheatIgnoreCooldown;
                NetLab.Log($"Расхождение «клиент игнорирует кулдаун»: {(NetLab.CheatIgnoreCooldown ? "ВКЛ" : "ВЫКЛ")}");
            }
            if (kb.xKey.wasPressedThisFrame)
            {
                NetLab.CheatSpeed = !NetLab.CheatSpeed;
                NetLab.Log($"Расхождение «клиент ускоряет себя ×1.8»: {(NetLab.CheatSpeed ? "ВКЛ" : "ВЫКЛ")}");
            }
            if (kb.rKey.wasPressedThisFrame)
            {
                NetLab.ResetStats();
                NetLab.Log("Статистика сброшена");
            }
        }

        void ResetLocalPrediction()
        {
            if (Network == null || Network.LocalClient?.PlayerObject == null)
                return;
            var mv = Network.LocalClient.PlayerObject.GetComponent<PredictedMovement>();
            if (mv != null)
                mv.ResetPrediction();
        }

        // ─────────────────────────────────── GUI ───────────────────────────────────

        void OnGUI()
        {
            float scale = Mathf.Max(1f, Screen.height / 900f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale;

            GUILayout.BeginArea(new Rect(10, 10, 430, Screen.height / scale - 20), GUI.skin.box);
            GUILayout.Label("<b>Практика 4 — Client-Side Prediction / Reconciliation</b>", Rich(14));

            if (Network == null)
            {
                GUILayout.Label("NetworkManager не найден");
                GUILayout.EndArea();
                return;
            }

            if (!Network.IsClient && !Network.IsServer)
                DrawConnectPanel();
            else
                DrawSessionPanel();

            GUILayout.EndArea();

            DrawLog(w);
        }

        void DrawConnectPanel()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(24));
            m_Address = GUILayout.TextField(m_Address, GUILayout.Width(140));
            GUILayout.Label("порт", GUILayout.Width(34));
            m_Port = GUILayout.TextField(m_Port, GUILayout.Width(60));
            GUILayout.EndHorizontal();

            var transport = Network.GetComponent<UnityTransport>();
            if (GUILayout.Button("Запустить Listen Server (Host)", GUILayout.Height(30)))
            {
                if (transport != null) transport.SetConnectionData(m_Address, ParsePort(), "0.0.0.0");
                Network.StartHost();
                NetLab.Log("Запущен Host (Listen Server)");
            }
            if (GUILayout.Button("Подключиться как Client", GUILayout.Height(30)))
            {
                if (transport != null) transport.SetConnectionData(m_Address, ParsePort());
                Network.StartClient();
                NetLab.Log($"Подключение к {m_Address}:{m_Port}…");
            }
            GUILayout.Label("Сначала запустите Host в одном окне, затем Client в другом " +
                            "(Multiplayer Play Mode или собранный Development Build).", Rich(11));
            DrawHelp();
        }

        ushort ParsePort() => ushort.TryParse(m_Port, out var p) ? p : (ushort)7777;

        void DrawSessionPanel()
        {
            string role = Network.IsHost ? "HOST (Listen Server)" : Network.IsServer ? "SERVER" : "CLIENT";
            GUILayout.Label($"Роль: <b>{role}</b>   id: {Network.LocalClientId}   игроков: " +
                            (Network.IsServer ? Network.ConnectedClientsIds.Count.ToString() : "-"), Rich(12));
            GUILayout.Label($"Профиль сети: <b>{NetLab.ProfileName}</b>", Rich(12));

            if (!Network.IsServer && Network.IsConnectedClient)
            {
                ulong rtt = Network.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId);
                GUILayout.Label($"RTT транспорта: {rtt} мс   ввод→подтверждение: {NetLab.LastAckLatencyMs:F0} мс", Rich(12));
            }

            GUILayout.Space(4);
            GUILayout.Label($"[1] Предсказание рывка: {OnOff(NetLab.PredictDash)}    " +
                            $"[2] Предсказание движения: {OnOff(NetLab.PredictMovement)}", Rich(12));
            GUILayout.Label($"[C] Чит «нет кулдауна»: {OnOff(NetLab.CheatIgnoreCooldown)}    " +
                            $"[X] Чит «скорость ×1.8»: {OnOff(NetLab.CheatSpeed)}", Rich(12));

            var mv = Network.LocalClient?.PlayerObject != null
                ? Network.LocalClient.PlayerObject.GetComponent<PredictedMovement>()
                : null;
            if (mv != null)
            {
                var s = mv.State;
                float cd = s.CooldownTicksLeft * MovementSettings.Dt;
                GUILayout.Label($"Рывок: {(s.IsDashing ? "<color=yellow>выполняется</color>" : cd > 0 ? $"кулдаун {cd:F2} с" : "<color=lime>готов</color>")}" +
                                $"   неподтв. вводов: {NetLab.PendingInputs}", Rich(12));
            }

            string reaction = NetLab.LastDashReactionMs < 0 ? "-" : $"{NetLab.LastDashReactionMs:F0} мс";
            GUILayout.Label($"Задержка «нажатие → рывок»: <b>{reaction}</b>", Rich(13));
            GUILayout.Label($"Коррекций: {NetLab.Corrections} (последняя {NetLab.LastCorrectionError:F2} м)   " +
                            $"отказов сервера: {(Network.IsServer ? NetLab.ServerRejects : NetLab.ServerRejectsSeen)}", Rich(12));
            if (Network.IsServer)
                GUILayout.Label($"Сервер: потеряно вводов {NetLab.ServerLostInputs}", Rich(12));

            if (GUILayout.Button("Отключиться"))
            {
                Network.Shutdown();
                NetLab.Log("Сессия завершена");
            }
            DrawHelp();
        }

        void DrawHelp()
        {
            if (!m_ShowHelp)
            {
                GUILayout.Label("[H] — показать подсказку", Rich(11));
                return;
            }
            GUILayout.Label(
                "WASD / стрелки — движение,  Space / Shift — рывок (кулдаун 1.5 с)\n" +
                "F1 — без эмуляции,  F2 — 100 мс,  F3 — 175±25 мс + 4% потерь\n" +
                "1 — вкл/выкл предсказание рывка,  2 — вкл/выкл предсказание движения\n" +
                "C — клиент игнорирует кулдаун,  X — клиент ускоряет себя\n" +
                "R — сбросить статистику,  H — скрыть подсказку\n" +
                "Синий диск под персонажем — последняя позиция, подтверждённая сервером.", Rich(11));
            if (!string.IsNullOrEmpty(NetLab.LogFilePath))
                GUILayout.Label($"Лог: {NetLab.LogFilePath}", Rich(10));
        }

        void DrawLog(float screenW)
        {
            float width = Mathf.Min(620f, screenW - 460f);
            if (width < 250f)
                return;
            GUILayout.BeginArea(new Rect(screenW - width - 10, 10, width, 330), GUI.skin.box);
            GUILayout.Label("<b>Журнал событий</b>", Rich(12));
            foreach (var line in NetLab.Lines)
                GUILayout.Label(line, Rich(11));
            GUILayout.EndArea();
        }

        static string OnOff(bool v) => v ? "<color=lime>ВКЛ</color>" : "<color=orange>ВЫКЛ</color>";

        static GUIStyle Rich(int size)
        {
            var st = new GUIStyle(GUI.skin.label) { richText = true, fontSize = size, wordWrap = true };
            st.normal.textColor = Color.white;
            return st;
        }
    }
}
