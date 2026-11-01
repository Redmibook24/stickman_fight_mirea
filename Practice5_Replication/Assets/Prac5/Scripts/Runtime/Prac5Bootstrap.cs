using System;
using System.Collections;
using System.Globalization;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Prac5
{
    /// <summary>
    /// Запуск эксперимента и HUD.
    ///
    /// Интерактивно: кнопки Host / Client, переключатели оптимизаций (на сервере), F5 — netprofile enable/disable.
    /// Из командной строки (замеры без участия человека):
    ///   -p5role host|client  -p5opt baseline|all|everything|grid,push,...  -p5label имя
    ///   -p5clients 3  -p5warmup 8  -p5duration 60  -p5out папка  -p5port 7777
    /// Сервер ждёт нужное число синхронизированных клиентов, прогревается, пишет профиль заданное
    /// время, сохраняет итог и завершает процесс; клиенты завершаются вслед за отключением.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class Prac5Bootstrap : MonoBehaviour
    {
        NetworkManager m_Net;
        ReplicationServer m_Replication;
        TrafficProfiler m_Profiler;
        ArenaDirector m_Director;
        bool m_ShowHud = true;
        string m_Status = "";

        static readonly (RepOptimizations flag, string name)[] Toggles =
        {
            (RepOptimizations.Grid, "Replication Graph (сетка + дистанция)"),
            (RepOptimizations.Dormancy, "Net Dormancy"),
            (RepOptimizations.Push, "Push-Based (только грязные поля)"),
            (RepOptimizations.Quantize, "Квантование (NetQuantize)"),
            (RepOptimizations.Frequency, "NetUpdateFrequency по классам"),
            (RepOptimizations.Priority, "доп.: NetPriority + бюджет"),
            (RepOptimizations.ViewRelevancy, "доп.: IsNetRelevantFor по взгляду"),
        };

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
            m_Replication = GetComponent<ReplicationServer>();
            m_Profiler = GetComponent<TrafficProfiler>();
            m_Director = GetComponent<ArenaDirector>();
            Application.runInBackground = true;
            Application.targetFrameRate = 100;
        }

        void Start()
        {
            string role = Arg("-p5role");
            if (string.IsNullOrEmpty(role))
                return;

            m_ShowHud = !Application.isBatchMode;
            Autopilot.SetEnabled(true);
            ushort port = (ushort)ArgInt("-p5port", 7777);
            m_Net.GetComponent<UnityTransport>().SetConnectionData("127.0.0.1", port, "0.0.0.0");
            string outDir = Arg("-p5out");
            if (!string.IsNullOrEmpty(outDir))
                m_Profiler.OutputDirectory = outDir;

            if (role == "host")
            {
                m_Replication.Optimizations = RepPolicies.Parse(Arg("-p5opt"));
                StartCoroutine(RunServerExperiment());
            }
            else
            {
                StartCoroutine(RunClient());
            }
        }

        IEnumerator RunServerExperiment()
        {
            int clients = ArgInt("-p5clients", 3);
            float warmup = ArgInt("-p5warmup", 8);
            float duration = ArgInt("-p5duration", 60);
            string label = Arg("-p5label") ?? RepPolicies.Describe(m_Replication.Optimizations);

            m_Net.StartHost();
            Log($"Хост запущен, режим {RepPolicies.Describe(m_Replication.Optimizations)}, ждём клиентов: {clients}");

            float deadline = Time.unscaledTime + 120f;
            while (m_Replication.ConnectionCount < clients && Time.unscaledTime < deadline)
                yield return null;
            Log($"Клиентов подключено: {m_Replication.ConnectionCount}. Прогрев {warmup} с");
            yield return new WaitForSecondsRealtime(warmup);

            m_Profiler.Enable(label);
            yield return new WaitForSecondsRealtime(duration + 0.5f);
            string summary = m_Profiler.Disable();
            Log($"Профиль сохранён: {summary}");

            m_Net.Shutdown();
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        IEnumerator RunClient()
        {
            float warmup = ArgInt("-p5warmup", 8);
            string label = Arg("-p5label") ?? "client";

            // сервер может стартовать чуть позже — пробуем подключиться несколько раз
            for (int attempt = 0; attempt < 30 && !m_Net.IsConnectedClient; attempt++)
            {
                if (!m_Net.IsListening)
                    m_Net.StartClient();
                float until = Time.unscaledTime + 2f;
                while (!m_Net.IsConnectedClient && Time.unscaledTime < until)
                    yield return null;
                if (!m_Net.IsConnectedClient)
                {
                    m_Net.Shutdown();
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }
            if (!m_Net.IsConnectedClient)
            {
                Log("Не удалось подключиться к серверу");
                Application.Quit();
                yield break;
            }

            yield return new WaitForSecondsRealtime(warmup);
            m_Profiler.Enable(label);
            while (m_Net.IsConnectedClient)
                yield return null;
            m_Profiler.Disable();
            yield return new WaitForSecondsRealtime(0.5f);
            Application.Quit();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;
            if (kb.f5Key.wasPressedThisFrame && m_Net.IsListening)
            {
                if (m_Profiler.Capturing) m_Status = "Сохранено: " + m_Profiler.Disable();
                else m_Profiler.Enable(RepPolicies.Describe(m_Replication.Optimizations) + DateTime.Now.ToString("_HHmmss"));
            }
            if (kb.pKey.wasPressedThisFrame)
                Autopilot.SetEnabled(!Autopilot.Enabled);
            if (kb.hKey.wasPressedThisFrame)
                m_ShowHud = !m_ShowHud;
        }

        void OnGUI()
        {
            if (!m_ShowHud)
                return;

            GUILayout.BeginArea(new Rect(10, 10, 430, Screen.height - 20), GUI.skin.box);
            GUILayout.Label("<b>Практика 5 — оптимизация репликации</b>", new GUIStyle(GUI.skin.label) { richText = true });

            if (!m_Net.IsListening)
            {
                if (GUILayout.Button("Запустить Listen Server (Host)")) m_Net.StartHost();
                if (GUILayout.Button("Подключиться как Client")) m_Net.StartClient();
                GUILayout.EndArea();
                return;
            }

            var s = m_Profiler.Last;
            var inv = CultureInfo.InvariantCulture;
            if (m_Net.IsServer)
            {
                GUILayout.Label($"Режим: {RepPolicies.Describe(m_Replication.Optimizations)}");
                foreach (var (flag, name) in Toggles)
                {
                    bool on = (m_Replication.Optimizations & flag) != 0;
                    bool now = GUILayout.Toggle(on, name);
                    if (now != on)
                        m_Replication.Optimizations ^= flag;
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Baseline")) m_Replication.Optimizations = RepOptimizations.None;
                if (GUILayout.Button("Все обязательные")) m_Replication.Optimizations = RepOptimizations.AllRequired;
                if (GUILayout.Button("Все + доп.")) m_Replication.Optimizations = RepOptimizations.Everything;
                GUILayout.EndHorizontal();

                GUILayout.Label($"Акторов на арене: {m_Director.SpawnedCount}, клиентов: {s.Clients}");
                GUILayout.Label($"Исходящий трафик сервера: {s.TotalKBps.ToString("F1", inv)} КБ/с " +
                                $"(репликация акторов {s.RepKBps.ToString("F1", inv)} КБ/с)");
                GUILayout.Label($"  деревья {s.KindKBps?[0].ToString("F1", inv)} | ящики {s.KindKBps?[1].ToString("F1", inv)} | " +
                                $"аптечки {s.KindKBps?[2].ToString("F1", inv)} | боты {s.KindKBps?[3].ToString("F1", inv)} | " +
                                $"снаряды {s.KindKBps?[4].ToString("F1", inv)}");
                GUILayout.Label($"Записей/с: {s.RecordsPerSec:F0}, релевантно на клиента: {s.RelevantPerClient:F1}");
                GUILayout.Label($"Акторов обработано/с: {s.ConsideredPerSec:F0}, спят: {s.DormantSkippedPerSec:F0}/с, " +
                                $"кадр репликации {s.RepMsPerTick.ToString("F3", inv)} мс");
            }
            else
            {
                GUILayout.Label($"Принято репликации: {s.ClientRepKBps.ToString("F1", inv)} КБ/с, записей/с: {s.ClientRecordsPerSec:F0}");
                GUILayout.Label($"Видимых акторов: {s.ClientVisible} из {ReplicatedEntity.ClientEntities.Count}");
            }

            GUILayout.Label(m_Profiler.Capturing ? $"● netprofile: запись → {m_Profiler.CapturePath}" : "netprofile: выкл (F5)");
            if (!string.IsNullOrEmpty(m_Status))
                GUILayout.Label(m_Status);
            GUILayout.Label($"Автопилот: {(Autopilot.Enabled ? "вкл" : "выкл")} (P), H — скрыть HUD");
            GUILayout.EndArea();
        }

        static void Log(string message)
        {
            Prac4.NetLab.Log("[P5] " + message);
        }

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static int ArgInt(string name, int fallback) =>
            int.TryParse(Arg(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }
}
