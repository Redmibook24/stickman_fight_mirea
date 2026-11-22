using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using Unity.Netcode;
using UnityEngine;

namespace Prac7
{
    /// <summary>
    /// Транспорт Netcode for GameObjects поверх Steam Sockets (ISteamNetworkingSockets) для Steam-профиля.
    /// В IP-профиле работает обычный UnityTransport; клиент и сервер обязаны использовать один и тот
    /// же транспорт, поэтому клиент из Steam-профиля не подключится к серверу IP-профиля и наоборот.
    ///
    /// Сервер слушает через SteamGameServerNetworkingSockets (API выделенного сервера, Steam-клиент
    /// на сервере не нужен), клиент подключается через SteamNetworkingSockets запущенного Steam.
    /// Соединение прямое по IP (CreateListenSocketIP / ConnectByIPAddress): Steam Sockets шифрует
    /// трафик и сам отвечает за надёжную доставку. Relay (Steam Datagram Relay) здесь не включается,
    /// он требует размещения сервера в сети Valve; фактический маршрут пишется в лог при подключении.
    /// </summary>
    public class SteamSocketsTransport : NetworkTransport
    {
        /// <summary>Порт, на котором сервер принимает Steam Sockets соединения.</summary>
        public ushort ListenPort = 7777;

        /// <summary>Адрес сервера для клиента: результат GetResolvedConnectString, например 203.0.113.10:7777.</summary>
        public string ConnectAddress;

        const int MaxMessagesPerPoll = 256;

        public override ulong ServerClientId => 0;

        readonly Dictionary<ulong, HSteamNetConnection> m_ClientToConn = new();
        readonly Dictionary<HSteamNetConnection, ulong> m_ConnToClient = new();
        readonly Queue<(NetworkEvent type, ulong client)> m_Events = new();
        readonly Queue<(ulong client, byte[] data, float time)> m_Incoming = new();
        readonly IntPtr[] m_MessageBuffer = new IntPtr[MaxMessagesPerPoll];

        bool m_IsServer;
        bool m_Started;
        ulong m_NextClientId = 1;   // 0 занят: это ServerClientId
        HSteamListenSocket m_Listen = HSteamListenSocket.Invalid;
        HSteamNetPollGroup m_Poll = HSteamNetPollGroup.Invalid;
        HSteamNetConnection m_ServerConn = HSteamNetConnection.Invalid;
        Callback<SteamNetConnectionStatusChangedCallback_t> m_StatusCallback;

        /// <summary>Текстовое описание маршрута последнего соединения (для лога и отчёта).</summary>
        public string LastRouteInfo { get; private set; }

        /// <summary>
        /// Как закрылось соединение с сервером (для сообщения игроку): ClosedByPeer значит, что сервер
        /// сам закрыл соединение, ProblemDetectedLocally значит тайм-аут или обрыв связи.
        /// </summary>
        public ESteamNetworkingConnectionState LastCloseState { get; private set; }

        NetworkManager m_Net;

        public override void Initialize(NetworkManager networkManager = null) => m_Net = networkManager;

        // ───────────────────────────── сервер ─────────────────────────────

        /// <summary>
        /// Требует уже инициализированного Steam GameServer (SteamGameServerHost.Begin):
        /// без него SteamGameServerNetworkingSockets недоступен.
        /// </summary>
        public override bool StartServer()
        {
            m_IsServer = true;
            var addr = new SteamNetworkingIPAddr { m_ipv6 = new byte[16] };   // массив обязателен для передачи в нативный код
            addr.Clear();               // все интерфейсы
            addr.m_port = ListenPort;

            m_StatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnStatusChanged);
            m_Listen = SteamGameServerNetworkingSockets.CreateListenSocketIP(ref addr, 0, null);
            m_Poll = SteamGameServerNetworkingSockets.CreatePollGroup();
            m_Started = m_Listen != HSteamListenSocket.Invalid && m_Poll != HSteamNetPollGroup.Invalid;

            Debug.Log($"[SteamSockets] сервер: listen socket на UDP {ListenPort} {(m_Started ? "создан" : "НЕ создан")}");
            return m_Started;
        }

        // ───────────────────────────── клиент ─────────────────────────────

        /// <summary>Требует SteamAPI.Init на клиенте (запущенный и вошедший в аккаунт Steam).</summary>
        public override bool StartClient()
        {
            m_IsServer = false;
            LastCloseState = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
            var addr = new SteamNetworkingIPAddr { m_ipv6 = new byte[16] };   // массив обязателен для передачи в нативный код
            if (string.IsNullOrEmpty(ConnectAddress) || !addr.ParseString(ConnectAddress))
            {
                Debug.LogError($"[SteamSockets] неверный адрес сервера «{ConnectAddress}»");
                return false;
            }

            m_StatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
            m_ServerConn = SteamNetworkingSockets.ConnectByIPAddress(ref addr, 0, null);
            m_Started = m_ServerConn != HSteamNetConnection.Invalid;
            Debug.Log($"[SteamSockets] клиент: подключение к {ConnectAddress} {(m_Started ? "начато" : "не удалось начать")}");
            return m_Started;
        }

        // ──────────────────────── события соединений ────────────────────────

        void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t cb)
        {
            var state = cb.m_info.m_eState;
            if (m_IsServer)
                OnServerStatus(cb.m_hConn, cb.m_info, state);
            else
                OnClientStatus(cb.m_hConn, cb.m_info, state);
        }

        void OnServerStatus(HSteamNetConnection conn, SteamNetConnectionInfo_t info, ESteamNetworkingConnectionState state)
        {
            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Принимаем входящее соединение; решение о допуске игрока принимает GameRules (лимит мест).
                    if (SteamGameServerNetworkingSockets.AcceptConnection(conn) != EResult.k_EResultOK)
                    {
                        SteamGameServerNetworkingSockets.CloseConnection(conn, 0, "accept failed", false);
                        return;
                    }
                    SteamGameServerNetworkingSockets.SetConnectionPollGroup(conn, m_Poll);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    ulong id = m_NextClientId++;
                    m_ClientToConn[id] = conn;
                    m_ConnToClient[conn] = id;
                    LastRouteInfo = DescribeRoute(conn, info, server: true);
                    Debug.Log($"[SteamSockets] клиент {id} подключён: {LastRouteInfo}");
                    m_Events.Enqueue((NetworkEvent.Connect, id));
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (m_ConnToClient.TryGetValue(conn, out ulong gone))
                    {
                        m_ConnToClient.Remove(conn);
                        m_ClientToConn.Remove(gone);
                        m_Events.Enqueue((NetworkEvent.Disconnect, gone));
                    }
                    SteamGameServerNetworkingSockets.CloseConnection(conn, 0, null, false);
                    break;
            }
        }

        void OnClientStatus(HSteamNetConnection conn, SteamNetConnectionInfo_t info, ESteamNetworkingConnectionState state)
        {
            if (conn != m_ServerConn)
                return;
            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    LastRouteInfo = DescribeRoute(conn, info, server: false);
                    Debug.Log($"[SteamSockets] соединение с сервером установлено: {LastRouteInfo}");
                    m_Events.Enqueue((NetworkEvent.Connect, ServerClientId));
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    Debug.Log($"[SteamSockets] соединение закрыто: {state}, причина {info.m_eEndReason} {info.m_szEndDebug}");
                    LastCloseState = state;
                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    m_ServerConn = HSteamNetConnection.Invalid;
                    m_Events.Enqueue((NetworkEvent.Disconnect, ServerClientId));
                    break;
            }
        }

        /// <summary>
        /// Фактический маршрут: адрес собеседника и признак relay. Если POP relay не задан, а адрес
        /// обычный IP, соединение прямое. Подробности даёт GetDetailedConnectionStatus.
        /// </summary>
        string DescribeRoute(HSteamNetConnection conn, SteamNetConnectionInfo_t info, bool server)
        {
            info.m_addrRemote.ToString(out string remote, true);
            bool relay = info.m_idPOPRelay.m_SteamNetworkingPOPID != 0;
            string detailed = server
                ? (SteamGameServerNetworkingSockets.GetDetailedConnectionStatus(conn, out string s1, 2048) >= 0 ? s1 : "")
                : (SteamNetworkingSockets.GetDetailedConnectionStatus(conn, out string s2, 2048) >= 0 ? s2 : "");
            string firstLine = detailed.Split('\n')[0].Trim();
            return $"адрес {remote}, {(relay ? "через Steam relay" : "прямое соединение (relay не используется)")}; {firstLine}";
        }

        // ───────────────────────────── данные ─────────────────────────────

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery)
        {
            HSteamNetConnection conn = m_IsServer
                ? (m_ClientToConn.TryGetValue(clientId, out var c) ? c : HSteamNetConnection.Invalid)
                : m_ServerConn;
            if (conn == HSteamNetConnection.Invalid)
                return;

            // Ненадёжные сообщения Netcode (движение) идут без подтверждений, надёжные - с ними.
            int flags = delivery is NetworkDelivery.Unreliable or NetworkDelivery.UnreliableSequenced
                ? Constants.k_nSteamNetworkingSend_UnreliableNoNagle
                : Constants.k_nSteamNetworkingSend_ReliableNoNagle;

            // Счётчик трафика из ПР5 общий для всех транспортов: статистика оптимизаций не пропадает.
            Prac5.CountingTransport.BytesSent += payload.Count;
            Prac5.CountingTransport.SendCalls++;

            GCHandle pin = GCHandle.Alloc(payload.Array, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = pin.AddrOfPinnedObject() + payload.Offset;
                if (m_IsServer)
                    SteamGameServerNetworkingSockets.SendMessageToConnection(conn, ptr, (uint)payload.Count, flags, out _);
                else
                    SteamNetworkingSockets.SendMessageToConnection(conn, ptr, (uint)payload.Count, flags, out _);
            }
            finally
            {
                pin.Free();
            }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            receiveTime = Time.realtimeSinceStartup;
            payload = default;

            if (m_Events.Count > 0)
            {
                var (type, client) = m_Events.Dequeue();
                clientId = client;
                return type;
            }

            if (m_Incoming.Count == 0)
                ReceiveBatch();

            if (m_Incoming.Count > 0)
            {
                var (client, data, time) = m_Incoming.Dequeue();
                clientId = client;
                payload = new ArraySegment<byte>(data);
                receiveTime = time;
                return NetworkEvent.Data;
            }

            clientId = 0;
            return NetworkEvent.Nothing;
        }

        /// <summary>Забирает пачку принятых сообщений из Steam и копирует их в управляемую память.</summary>
        void ReceiveBatch()
        {
            if (!m_Started)
                return;

            int count = m_IsServer
                ? SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(m_Poll, m_MessageBuffer, MaxMessagesPerPoll)
                : m_ServerConn == HSteamNetConnection.Invalid
                    ? 0
                    : SteamNetworkingSockets.ReceiveMessagesOnConnection(m_ServerConn, m_MessageBuffer, MaxMessagesPerPoll);

            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(m_MessageBuffer[i]);
                var data = new byte[msg.m_cbSize];
                Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                ulong sender = m_IsServer
                    ? (m_ConnToClient.TryGetValue(msg.m_conn, out ulong id) ? id : ulong.MaxValue)
                    : ServerClientId;
                SteamNetworkingMessage_t.Release(m_MessageBuffer[i]);   // память сообщения принадлежит Steam
                if (sender != ulong.MaxValue)
                    m_Incoming.Enqueue((sender, data, now));
            }
        }

        // ──────────────────────── отключение и остановка ────────────────────────

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (!m_ClientToConn.TryGetValue(clientId, out var conn))
                return;
            // linger = true: перед закрытием досылаются надёжные сообщения (например, причина отказа).
            // При остановке сервера Netcode отключает игроков этим же методом, но тогда досылать нечего,
            // а отложенный пакет закрытия не успел бы уйти до GameServer.Shutdown: клиент узнал бы
            // о завершении сервера только по тайм-ауту.
            bool linger = m_Net == null || !m_Net.ShutdownInProgress;
            SteamGameServerNetworkingSockets.CloseConnection(conn, 0, "disconnected by server", linger);
            m_ClientToConn.Remove(clientId);
            m_ConnToClient.Remove(conn);
        }

        public override void DisconnectLocalClient()
        {
            if (m_ServerConn != HSteamNetConnection.Invalid)
                SteamNetworkingSockets.CloseConnection(m_ServerConn, 0, "client left", true);
            m_ServerConn = HSteamNetConnection.Invalid;
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            HSteamNetConnection conn = m_IsServer
                ? (m_ClientToConn.TryGetValue(clientId, out var c) ? c : HSteamNetConnection.Invalid)
                : m_ServerConn;
            if (conn == HSteamNetConnection.Invalid)
                return 0;

            var status = new SteamNetConnectionRealTimeStatus_t();
            var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
            EResult r = m_IsServer
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes);
            return r == EResult.k_EResultOK && status.m_nPing > 0 ? (ulong)status.m_nPing : 0;
        }

        public override void Shutdown()
        {
            if (m_IsServer)
            {
                // Без linger: при остановке сервера досылать нечего, а пакет закрытия должен уйти сразу,
                // чтобы клиенты узнали о завершении сервера, а не ждали тайм-аута.
                foreach (var conn in m_ClientToConn.Values)
                    SteamGameServerNetworkingSockets.CloseConnection(conn, 0, "server shutdown", false);
                if (m_Listen != HSteamListenSocket.Invalid)
                    SteamGameServerNetworkingSockets.CloseListenSocket(m_Listen);
                if (m_Poll != HSteamNetPollGroup.Invalid)
                    SteamGameServerNetworkingSockets.DestroyPollGroup(m_Poll);
            }
            else
            {
                DisconnectLocalClient();
            }

            m_StatusCallback?.Dispose();
            m_StatusCallback = null;
            m_ClientToConn.Clear();
            m_ConnToClient.Clear();
            m_Events.Clear();
            m_Incoming.Clear();
            m_Listen = HSteamListenSocket.Invalid;
            m_Poll = HSteamNetPollGroup.Invalid;
            m_Started = false;
        }
    }
}
