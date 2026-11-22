using System;
using System.Globalization;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Prac7
{
    /// <summary>
    /// Запуск выделенного сервера (Dedicated Server): отдельный процесс без локального игрока,
    /// все игроки подключаются к нему как удалённые клиенты. В отличие от Listen Server (Host)
    /// из ПР4-ПР5 здесь вызывается StartServer, а не StartHost.
    ///
    /// Сервер стартует сам, если процесс собран как серверный билд (define UNITY_SERVER)
    /// или запущен с ключом -netProfile. Параметры берутся из командной строки, их передаёт
    /// deploy/entrypoint.sh из переменных окружения контейнера:
    ///   -port 7777  -serverName "..."  -maxPlayers 4  -netProfile ip|steam
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class DedicatedServer : MonoBehaviour
    {
        /// <summary>Серверная частота кадров: тики симуляции 50 Гц, больше серверу не нужно.</summary>
        const int ServerFrameRate = 60;

        public static ServerSettings Settings { get; private set; }

        NetworkManager m_Net;
        SteamGameServerHost m_SteamHost;

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
        }

        void Start()
        {
            if (!ShouldRunAsServer())
                return;

            Settings = ServerSettings.FromCommandLine();
            Application.targetFrameRate = ServerFrameRate;
            Application.runInBackground = true;

            // Лимит игроков проверяет правило подключения из ПР4 (роль GameMode).
            var rules = GetComponent<Prac4.GameRules>();
            if (rules != null)
                rules.MaxPlayers = Settings.MaxPlayers;

            if (Settings.NetProfile == "steam")
            {
                // Steam-профиль: сначала регистрация выделенного сервера в Steam (без неё серверные
                // Steam Sockets недоступны), затем переключение Netcode на транспорт Steam Sockets.
                m_SteamHost = GetComponent<SteamGameServerHost>();
                if (m_SteamHost == null || !m_SteamHost.Begin(Settings))
                {
                    Log("ОШИБКА: Steam недоступен, сервер в Steam-профиле не запущен");
                    Application.Quit(1);
                    return;
                }
                var steamTransport = GetComponent<SteamSocketsTransport>();
                steamTransport.ListenPort = Settings.Port;
                m_Net.NetworkConfig.NetworkTransport = steamTransport;
            }
            else
            {
                // IP-профиль: 0.0.0.0, то есть все сетевые интерфейсы, иначе в контейнере сервер
                // не будет доступен через опубликованный порт.
                var transport = GetComponent<UnityTransport>();
                transport.SetConnectionData("0.0.0.0", Settings.Port, "0.0.0.0");
                m_Net.NetworkConfig.NetworkTransport = transport;
            }

            m_Net.OnClientConnectedCallback += OnClientConnected;
            m_Net.OnClientDisconnectCallback += OnClientDisconnected;
            Application.quitting += OnQuitting;

            if (!m_Net.StartServer())
            {
                Log($"ОШИБКА: не удалось запустить сервер на UDP {Settings.Port} (порт занят?)");
                Application.Quit(1);
                return;
            }

            // По этой строке в логах (docker logs) видно, что карта загружена и сервер принимает игроков.
            Log($"READY: сервер «{Settings.ServerName}» слушает 0.0.0.0:{Settings.Port}/udp, " +
                $"карта {SceneManager.GetActiveScene().name}, профиль {Settings.NetProfile}, " +
                $"транспорт {m_Net.NetworkConfig.NetworkTransport.GetType().Name}, " +
                $"мест {Settings.MaxPlayers}, сборка {Application.version}");
        }

        /// <summary>Серверный билд стартует сервер всегда, обычный билд только по ключу -netProfile.</summary>
        static bool ShouldRunAsServer()
        {
#if UNITY_SERVER
            return true;
#else
            return Application.isBatchMode && ServerSettings.HasArg("-netProfile");
#endif
        }

        void OnClientConnected(ulong clientId)
        {
            Log($"игрок {clientId} подключился, всего игроков: {m_Net.ConnectedClientsIds.Count}/{Settings.MaxPlayers}");
        }

        void OnClientDisconnected(ulong clientId)
        {
            // Уход одного игрока не останавливает сервер: остальные продолжают играть.
            Log($"игрок {clientId} отключился, осталось игроков: {m_Net.ConnectedClientsIds.Count}");
        }

        /// <summary>
        /// Штатная остановка: `docker stop` присылает SIGTERM, Unity завершает приложение
        /// и вызывает Application.quitting. Здесь закрываются соединения игроков.
        /// </summary>
        void OnQuitting()
        {
            Log("получен сигнал остановки, завершаем сервер");
            if (m_Net != null && m_Net.IsListening)
                m_Net.Shutdown();
            // Сессия Steam уничтожается после закрытия соединений: игроки не должны найти
            // в списке сервер, который уже не принимает подключения.
            if (m_SteamHost != null)
                m_SteamHost.End();
        }

        void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            if (m_Net == null)
                return;
            m_Net.OnClientConnectedCallback -= OnClientConnected;
            m_Net.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        static void Log(string message) =>
            Debug.Log($"[Server {DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>Параметры выделенного сервера из командной строки.</summary>
    public sealed class ServerSettings
    {
        public ushort Port { get; private set; } = 7777;
        public string ServerName { get; private set; } = "Stickman Fight PR7";
        public int MaxPlayers { get; private set; } = 4;
        /// <summary>ip: прямое подключение по адресу и порту; steam: регистрация и поиск через Steam.</summary>
        public string NetProfile { get; private set; } = "ip";

        public static ServerSettings FromCommandLine()
        {
            var s = new ServerSettings();
            if (int.TryParse(Arg("-port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                && port is > 0 and < 65536)
                s.Port = (ushort)port;
            if (int.TryParse(Arg("-maxPlayers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int max)
                && max is >= 1 and <= 16)
                s.MaxPlayers = max;
            s.ServerName = Arg("-serverName") ?? s.ServerName;
            s.NetProfile = (Arg("-netProfile") ?? s.NetProfile).ToLowerInvariant();
            return s;
        }

        public static bool HasArg(string name) => Arg(name) != null;

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
