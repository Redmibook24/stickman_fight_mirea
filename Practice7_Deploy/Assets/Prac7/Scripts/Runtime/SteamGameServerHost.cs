using System;
using System.Collections.Generic;
using Steamworks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Prac7
{
    /// <summary>
    /// Серверная сторона сессии в Steam-профиле: регистрация выделенного сервера в Steam,
    /// публикация параметров сессии и её завершение при штатной остановке.
    ///
    /// Используется API игрового сервера Steam (SteamGameServer), а не Steam Lobby: лобби принадлежит
    /// игроку и живёт, пока он в сети, а выделенный сервер регистрируется сам, без пользователя и
    /// без PlayerController на сервере. Вход анонимный (LogOnAnonymous): серверу не нужен аккаунт.
    /// Сессия не LAN: сервер публикуется на мастер-сервере Steam для поиска через интернет.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public class SteamGameServerHost : MonoBehaviour
    {
        /// <summary>Версия сервера для Steam в формате x.y.z.w.</summary>
        const string ServerVersion = "1.0.0.0";

        public bool Initialized { get; private set; }

        NetworkManager m_Net;
        readonly Dictionary<ulong, CSteamID> m_SteamUsers = new();
        Callback<SteamServersConnected_t> m_OnConnected;
        Callback<SteamServerConnectFailure_t> m_OnConnectFailure;
        Callback<SteamServersDisconnected_t> m_OnDisconnected;

        void Awake() => m_Net = GetComponent<NetworkManager>();

        /// <summary>
        /// Инициализирует Steam GameServer и публикует сессию. Вызывается ДО StartServer: Steam Sockets
        /// на сервере работают только после инициализации GameServer.
        /// </summary>
        public bool Begin(ServerSettings settings)
        {
            // eServerModeAuthentication: сервер попадает в список серверов Steam. Режим без аутентификации
            // в список не попадает, а VAC (AuthenticationAndSecure) учебному проекту не нужен.
            var result = GameServer.InitEx(0, settings.Port, SessionMetadata.QueryPort,
                EServerMode.eServerModeAuthentication, ServerVersion, out string error);
            if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                Log($"ОШИБКА: Steam GameServer не инициализирован ({result}): {error}. " +
                    "Нужны steamclient.so и steam_appid.txt рядом с сервером.");
                return false;
            }
            Initialized = true;

            m_OnConnected = Callback<SteamServersConnected_t>.CreateGameServer(OnSteamConnected);
            m_OnConnectFailure = Callback<SteamServerConnectFailure_t>.CreateGameServer(OnSteamConnectFailure);
            m_OnDisconnected = Callback<SteamServersDisconnected_t>.CreateGameServer(OnSteamDisconnected);

            // Параметры dedicated-сессии (не перенесённые настройки лобби/presence).
            SteamGameServer.SetProduct("SteamworksExample");
            SteamGameServer.SetModDir(SessionMetadata.GameDir);
            SteamGameServer.SetGameDescription("Stickman Fight PR7");
            SteamGameServer.SetDedicatedServer(true);
            SteamGameServer.SetServerName(settings.ServerName);
            SteamGameServer.SetMapName(SceneManager.GetActiveScene().name);
            SteamGameServer.SetMaxPlayerCount(settings.MaxPlayers);
            SteamGameServer.SetPasswordProtected(false);
            SteamGameServer.SetGameTags(SessionMetadata.Tags);
            // Ключи-значения видны в подробностях сервера (запрос rules), дублируют поля задания.
            SteamGameServer.SetKeyValue("PROJECT_TAG", SessionMetadata.ProjectTag);
            SteamGameServer.SetKeyValue("BUILD_ID", SessionMetadata.BuildId);
            SteamGameServer.SetKeyValue("GAME_MODE", SessionMetadata.GameMode);

            SteamGameServer.LogOnAnonymous();
            SteamGameServer.SetAdvertiseServerActive(true);

            m_Net.OnClientConnectedCallback += OnPlayerJoined;
            m_Net.OnClientDisconnectCallback += OnPlayerLeft;

            Log($"Steam GameServer инициализирован: App {SessionMetadata.AppId}, игровой порт {settings.Port}/udp, " +
                $"query-порт {SessionMetadata.QueryPort}/udp, теги «{SessionMetadata.Tags}». Ждём регистрации в Steam...");
            return true;
        }

        void Update()
        {
            // Колбэки Steam приходят только при явном вызове RunCallbacks.
            if (Initialized)
                GameServer.RunCallbacks();
        }

        void OnSteamConnected(SteamServersConnected_t _)
        {
            Log($"сессия опубликована в Steam: SteamID сервера {SteamGameServer.GetSteamID()}, " +
                $"публичный IP {SteamGameServer.GetPublicIP()}");
        }

        void OnSteamConnectFailure(SteamServerConnectFailure_t cb) =>
            Log($"ОШИБКА регистрации в Steam: {cb.m_eResult}, повтор {(cb.m_bStillRetrying ? "идёт" : "прекращён")}");

        void OnSteamDisconnected(SteamServersDisconnected_t cb) =>
            Log($"соединение с серверами Steam потеряно: {cb.m_eResult}");

        /// <summary>
        /// Число игроков в списке серверов Steam берётся из зарегистрированных на сервере пользователей,
        /// поэтому каждому подключённому игроку заводится запись (без проверки Steam-билета: в учебной
        /// версии игроки не проходят аутентификацию Steam, сервер проверяет их команды сам).
        /// </summary>
        void OnPlayerJoined(ulong clientId)
        {
            if (!Initialized || clientId == NetworkManager.ServerClientId)
                return;
            CSteamID id = SteamGameServer.CreateUnauthenticatedUserConnection();
            SteamGameServer.BUpdateUserData(id, $"Player {clientId}", 0);
            m_SteamUsers[clientId] = id;
        }

        void OnPlayerLeft(ulong clientId)
        {
            if (Initialized && m_SteamUsers.Remove(clientId, out CSteamID id))
                SteamGameServer.SendUserDisconnect_DEPRECATED(id);
        }

        /// <summary>
        /// Завершение и уничтожение сессии владельцем: сервер снимается с публикации, выходит из Steam
        /// и освобождает GameServer. Вызывается при штатной остановке после закрытия соединений.
        /// </summary>
        public void End()
        {
            if (!Initialized)
                return;

            m_Net.OnClientConnectedCallback -= OnPlayerJoined;
            m_Net.OnClientDisconnectCallback -= OnPlayerLeft;
            foreach (CSteamID id in m_SteamUsers.Values)
                SteamGameServer.SendUserDisconnect_DEPRECATED(id);
            m_SteamUsers.Clear();

            // Сетевой поток Steam отправляет пакеты закрытия соединений асинхронно: даём ему
            // немного времени, иначе GameServer.Shutdown оборвёт их, и клиенты узнают о
            // завершении сервера только по тайм-ауту.
            for (int i = 0; i < 10; i++)
            {
                GameServer.RunCallbacks();
                System.Threading.Thread.Sleep(20);
            }

            SteamGameServer.SetAdvertiseServerActive(false);
            SteamGameServer.LogOff();
            m_OnConnected?.Dispose();
            m_OnConnectFailure?.Dispose();
            m_OnDisconnected?.Dispose();
            GameServer.Shutdown();
            Initialized = false;
            Log("сессия снята с публикации и уничтожена, Steam GameServer остановлен");
        }

        static void Log(string message) =>
            Debug.Log($"[Steam {DateTime.Now:HH:mm:ss}] {message}");
    }
}
