using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace Prac7
{
    /// <summary>Найденная сессия: то, что показывает меню.</summary>
    public sealed class SessionInfo
    {
        public string Name;
        public string Map;
        public string Mode;
        public string BuildId;
        public int Players;
        public int MaxPlayers;
        public int Ping;
        public uint Ip;
        public ushort QueryPort;
        public ushort GamePort;
        public string Address => servernetadr_t.ToString(Ip, GamePort);
    }

    /// <summary>
    /// Steam-часть клиентской сессии: поиск выделенных серверов через ISteamMatchmakingServers,
    /// проверка выбранной сессии перед подключением и переход на сервер через Steam Sockets.
    ///
    /// Все операции Steam асинхронные: результат приходит в колбэки ответа, а не из вызова.
    /// Каждый колбэк проверяет, что относится к текущему запросу: поздний ответ отменённого
    /// поиска не должен переводить меню в состояние завершённого запроса.
    /// </summary>
    public partial class SessionClient
    {
        /// <summary>Сколько ждать завершения поиска, после этого запрос отменяется.</summary>
        public float SearchTimeoutSeconds = 15f;

        public IReadOnlyList<SessionInfo> Results => m_Results;
        public bool LastSearchWasLan { get; private set; }

        readonly List<SessionInfo> m_Results = new();
        HServerListRequest m_Request = HServerListRequest.Invalid;
        HServerQuery m_PingQuery = HServerQuery.Invalid;
        ISteamMatchmakingServerListResponse m_ListResponse;   // ссылка держит объект ответа живым для Steam
        ISteamMatchmakingPingResponse m_PingResponse;
        SessionInfo m_PendingJoin;
        float m_SearchDeadline;

        /// <summary>
        /// Поиск сессий. По умолчанию в интернете (мастер-сервер Steam с фильтрами проекта и сборки);
        /// lan = true ищет только в локальной сети и нужен для диагностики стенда без внешнего IP.
        /// </summary>
        public bool SearchSessions(bool lan = false)
        {
            if (IsBusy || State == SessionState.InGame)
                return false;   // повторное нажатие во время незавершённого запроса
            if (!SteamClientApi.EnsureInitialized())
            {
                Fail("Steam недоступен: " + SteamClientApi.Error);
                return false;
            }

            ReleaseSearch();
            m_Results.Clear();
            LastSearchWasLan = lan;
            m_ListResponse ??= new ISteamMatchmakingServerListResponse(OnServerResponded, OnServerFailedToRespond, OnRefreshComplete);

            var appId = new AppId_t(SessionMetadata.AppId);
            if (lan)
            {
                m_Request = SteamMatchmakingServers.RequestLANServerList(appId, m_ListResponse);
            }
            else
            {
                // Фильтры выполняет мастер-сервер Steam: наш каталог игры и теги проекта и сборки.
                var filters = new[]
                {
                    new MatchMakingKeyValuePair_t { m_szKey = "gamedir", m_szValue = SessionMetadata.GameDir },
                    new MatchMakingKeyValuePair_t { m_szKey = "gametagsand", m_szValue = SessionMetadata.RequiredTags },
                };
                m_Request = SteamMatchmakingServers.RequestInternetServerList(appId, filters, (uint)filters.Length, m_ListResponse);
            }

            if (m_Request == HServerListRequest.Invalid)
            {
                Fail("Steam не начал поиск сессий");
                return false;
            }

            m_SearchDeadline = Time.unscaledTime + SearchTimeoutSeconds;
            SetState(SessionState.Searching);
            Log($"поиск сессий {(lan ? "в локальной сети" : "в интернете")}: gamedir={SessionMetadata.GameDir}, теги «{SessionMetadata.RequiredTags}»");
            return true;
        }

        /// <summary>Сервер ответил на запрос списка: проверяем его и добавляем в результаты.</summary>
        void OnServerResponded(HServerListRequest request, int index)
        {
            if (request != m_Request || State != SessionState.Searching)
                return;   // поздний ответ старого запроса

            gameserveritem_t item = SteamMatchmakingServers.GetServerDetails(request, index);
            string tags = item.GetGameTags();
            // Не каждый фильтр гарантированно выполняется на мастер-сервере (а LAN-поиск фильтров
            // не имеет вовсе), поэтому проект и сборку перепроверяем на клиенте.
            if (item.GetGameDir() != SessionMetadata.GameDir
                || SessionMetadata.Tag(tags, "proj") != SessionMetadata.ProjectTag
                || SessionMetadata.Tag(tags, "build") != SessionMetadata.BuildId)
                return;

            m_Results.Add(ToSession(item));
        }

        void OnServerFailedToRespond(HServerListRequest request, int index)
        {
            // Сервер есть в списке мастер-сервера, но не ответил на прямой запрос (закрыт порт, NAT).
            if (request == m_Request)
                Log($"сервер #{index} из списка не ответил на запрос");
        }

        void OnRefreshComplete(HServerListRequest request, EMatchMakingServerResponse response)
        {
            if (request != m_Request || State != SessionState.Searching)
                return;
            ReleaseSearch();
            Log($"поиск завершён ({response}): найдено сессий {m_Results.Count}");
            SetState(SessionState.ResultsReady);   // пустой список меню покажет как «сессий не найдено»
        }

        /// <summary>
        /// Тайм-аут интерфейса отменяет и сам запрос (CancelQuery): иначе Steam продолжал бы присылать
        /// ответы для поиска, который меню уже считает завершённым.
        /// </summary>
        void UpdateSteamSearch()
        {
            if (State != SessionState.Searching || Time.unscaledTime < m_SearchDeadline)
                return;
            if (m_Request != HServerListRequest.Invalid)
                SteamMatchmakingServers.CancelQuery(m_Request);
            ReleaseSearch();
            if (m_Results.Count > 0)
                SetState(SessionState.ResultsReady);
            else
                Fail($"поиск не завершился за {SearchTimeoutSeconds:F0} с, сессий не найдено");
        }

        /// <summary>Освобождает запрос списка серверов: без этого Steam держит его ресурсы.</summary>
        void ReleaseSearch()
        {
            if (m_Request != HServerListRequest.Invalid)
                SteamMatchmakingServers.ReleaseRequest(m_Request);
            m_Request = HServerListRequest.Invalid;
        }

        /// <summary>
        /// Присоединение к найденной сессии. Сначала сервер опрашивается ещё раз (PingServer):
        /// пока игрок смотрел на список, сессия могла заполниться или исчезнуть.
        /// </summary>
        public bool JoinSession(SessionInfo session)
        {
            if (IsBusy || State == SessionState.InGame || session == null)
                return false;

            m_PendingJoin = session;
            ConnectedTo = session.Address;
            SetState(SessionState.Joining);
            m_JoinDeadline = Time.unscaledTime + JoinTimeoutSeconds;
            Log($"проверка сессии «{session.Name}» перед подключением");

            m_PingResponse ??= new ISteamMatchmakingPingResponse(OnPingResponded, OnPingFailed);
            m_PingQuery = SteamMatchmakingServers.PingServer(session.Ip, session.QueryPort, m_PingResponse);
            if (m_PingQuery == HServerQuery.Invalid)
            {
                Fail("Steam не начал проверку сессии");
                return false;
            }
            return true;
        }

        void OnPingResponded(gameserveritem_t item)
        {
            m_PingQuery = HServerQuery.Invalid;
            if (State != SessionState.Joining || m_PendingJoin == null)
                return;   // игрок уже отменил подключение

            if (item.m_nPlayers >= item.m_nMaxPlayers)
            {
                Fail($"сессия «{item.GetServerName()}» заполнена: {item.m_nPlayers}/{item.m_nMaxPlayers}");
                return;
            }

            // Аналог GetResolvedConnectString: адрес и игровой порт из ответа Steam о сервере.
            string connectString = item.m_NetAdr.GetConnectionAddressString();
            Log($"строка подключения от Steam: {connectString}");

            // Steam-профиль: клиент обязан использовать тот же транспорт, что и сервер (Steam Sockets).
            var steam = GetComponent<SteamSocketsTransport>();
            steam.ConnectAddress = connectString;
            m_Net.NetworkConfig.NetworkTransport = steam;
            m_PendingJoin = null;
            BeginJoin(connectString);   // аналог ClientTravel: Netcode подключится и загрузит карту сервера
        }

        void OnPingFailed()
        {
            m_PingQuery = HServerQuery.Invalid;
            if (State == SessionState.Joining && m_PendingJoin != null)
                Fail($"сессия «{m_PendingJoin.Name}» исчезла: сервер не отвечает");
            m_PendingJoin = null;
        }

        static SessionInfo ToSession(gameserveritem_t item) => new SessionInfo
        {
            Name = item.GetServerName(),
            Map = item.GetMap(),
            Mode = SessionMetadata.Tag(item.GetGameTags(), "mode"),
            BuildId = SessionMetadata.Tag(item.GetGameTags(), "build"),
            Players = item.m_nPlayers,
            MaxPlayers = item.m_nMaxPlayers,
            Ping = item.m_nPing,
            Ip = item.m_NetAdr.GetIP(),
            QueryPort = item.m_NetAdr.GetQueryPort(),
            GamePort = item.m_NetAdr.GetConnectionPort(),
        };

        void OnDestroy()
        {
            ReleaseSearch();
            if (m_PingQuery != HServerQuery.Invalid)
                SteamMatchmakingServers.CancelServerQuery(m_PingQuery);
        }
    }
}
