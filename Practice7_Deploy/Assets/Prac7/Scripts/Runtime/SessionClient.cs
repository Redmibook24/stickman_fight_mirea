using System;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Prac7
{
    /// <summary>Состояния клиентской сессии, их различает меню.</summary>
    public enum SessionState
    {
        Idle,          // ничего не происходит, можно искать или подключаться
        Searching,     // идёт поиск сессий
        ResultsReady,  // поиск завершён, список показан
        Joining,       // идёт подключение к выбранному серверу
        InGame,        // соединение с сервером установлено
        Error,         // операция не удалась, текст причины в LastError
    }

    /// <summary>
    /// Клиентская часть жизненного цикла сессии: подключение, игра, выход, обработка обрыва.
    /// UI (SessionMenu) только вызывает методы и показывает состояние, сетевой логики в нём нет.
    ///
    /// Переходы:
    ///   Idle → Searching → ResultsReady → Joining → InGame
    ///             ↓                          ↓
    ///           Error                      Error
    /// Из InGame клиент возвращается в Idle при выходе и в Error при обрыве связи.
    /// </summary>
    [RequireComponent(typeof(NetworkManager))]
    public partial class SessionClient : MonoBehaviour
    {
        /// <summary>Сколько ждать подключения, прежде чем сдаться и оборвать попытку.</summary>
        public float JoinTimeoutSeconds = 10f;

        public SessionState State { get; private set; } = SessionState.Idle;
        public string LastError { get; private set; }
        public string ConnectedTo { get; private set; }

        /// <summary>Вызывается при каждой смене состояния (меню, лог).</summary>
        public event Action<SessionState> StateChanged;

        NetworkManager m_Net;
        UnityTransport m_Transport;
        float m_JoinDeadline;

        void Awake()
        {
            m_Net = GetComponent<NetworkManager>();
            m_Transport = GetComponent<UnityTransport>();
        }

        void OnEnable()
        {
            m_Net.OnClientConnectedCallback += OnClientConnected;
            m_Net.OnClientDisconnectCallback += OnClientDisconnected;
        }

        void OnDisable()
        {
            // Снимаем обработчики, чтобы при повторном включении они не копились.
            m_Net.OnClientConnectedCallback -= OnClientConnected;
            m_Net.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        /// <summary>
        /// Ключи запуска для тестов без ручных кликов:
        ///   -connect 203.0.113.10:7777   сразу подключиться по адресу (диагностический IP-профиль);
        ///   -autopilot                   персонаж сам бегает по маршруту из ПР5.
        /// </summary>
        void Start()
        {
            if (m_Net.IsServer || DedicatedServer.Settings != null)
                return;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-autopilot")
                    Prac5.Autopilot.SetEnabled(true);
                if (args[i] == "-connect" && i + 1 < args.Length)
                {
                    string target = args[i + 1];
                    int colon = target.LastIndexOf(':');
                    ushort port = 7777;
                    if (colon > 0 && ushort.TryParse(target.Substring(colon + 1), out ushort p))
                    {
                        port = p;
                        target = target.Substring(0, colon);
                    }
                    JoinDirect(target, port);
                }
                if (args[i] == "-autojoin" || args[i] == "-autojoin-lan")
                {
                    // Steam-профиль без кликов: поиск и подключение к первой найденной сессии.
                    m_AutoLan = args[i] == "-autojoin-lan";
                    StateChanged += JoinFirstResultOnce;
                    SearchSessions(lan: m_AutoLan);
                }
                if (args[i] == "-rejoin")
                    m_RejoinCycles = 1;
                // Проверка серверной авторитетности: рывок чаще кулдауна (1,5 с) и клиент,
                // который нарочно игнорирует кулдаун. Сервер обязан отклонять лишние рывки.
                if (args[i] == "-autodash")
                    Prac4.PlayerInputReader.AutoDashInterval = 0.6f;
                if (args[i] == "-cheat-cooldown")
                    Prac4.NetLab.CheatIgnoreCooldown = true;
            }
        }

        bool m_AutoLan;
        int m_RejoinCycles;

        void JoinFirstResultOnce(SessionState state)
        {
            if (state is SessionState.Searching or SessionState.Joining)
                return;
            StateChanged -= JoinFirstResultOnce;
            if (state == SessionState.ResultsReady && Results.Count > 0)
            {
                JoinSession(Results[0]);
                if (m_RejoinCycles > 0)
                    StartCoroutine(RejoinAfterDelay());
            }
        }

        /// <summary>
        /// Тест -rejoin: выход из сессии и повторные поиск и подключение в том же процессе,
        /// то есть без перезапуска приложения.
        /// </summary>
        System.Collections.IEnumerator RejoinAfterDelay()
        {
            while (State == SessionState.Joining)
                yield return null;
            yield return new WaitForSecondsRealtime(8f);
            m_RejoinCycles--;
            Log("тест повторного подключения: выходим и ищем заново");
            Leave();
            yield return new WaitForSecondsRealtime(1f);
            StateChanged += JoinFirstResultOnce;
            SearchSessions(lan: m_AutoLan);
        }

        /// <summary>Можно ли начать новую операцию: повторное нажатие во время запроса игнорируется.</summary>
        public bool IsBusy => State is SessionState.Searching or SessionState.Joining;

        /// <summary>
        /// Диагностическое прямое подключение по адресу и порту (IP-профиль).
        /// Это не поиск сессий: адрес вводит сам игрок, в меню кнопка подписана отдельно.
        /// </summary>
        public bool JoinDirect(string host, ushort port)
        {
            if (IsBusy || State == SessionState.InGame)
                return false;
            if (string.IsNullOrWhiteSpace(host))
            {
                Fail("не указан адрес сервера");
                return false;
            }

            host = host.Trim();
            // IP-профиль: обычный UnityTransport, сервер должен быть запущен с -netProfile ip.
            m_Transport.SetConnectionData(host, port);
            m_Net.NetworkConfig.NetworkTransport = m_Transport;
            return BeginJoin($"{host}:{port}");
        }

        /// <summary>Общий шаг подключения для прямого адреса и для найденной сессии.</summary>
        internal bool BeginJoin(string target)
        {
            ConnectedTo = target;
            SetState(SessionState.Joining);
            m_JoinDeadline = Time.unscaledTime + JoinTimeoutSeconds;
            Log($"подключение к {target}");

            if (!m_Net.StartClient())
            {
                Fail($"не удалось начать подключение к {target}");
                return false;
            }
            return true;
        }

        /// <summary>Выход игрока: разрыв соединения и очистка локального состояния сессии.</summary>
        public void Leave()
        {
            if (m_Net.IsListening)
                m_Net.Shutdown();
            ConnectedTo = null;
            LastError = null;
            SetState(SessionState.Idle);
            Log("вышли из сессии");
        }

        /// <summary>Сброс ошибки, чтобы снова искать или подключаться без перезапуска игры.</summary>
        public void ClearError()
        {
            if (State == SessionState.Error)
                SetState(SessionState.Idle);
        }

        void Update()
        {
            SteamClientApi.RunCallbacks();
            UpdateSteamSearch();

            // Тайм-аут интерфейса отменяет и саму попытку подключения (Shutdown), иначе транспорт
            // продолжал бы стучаться к серверу, а меню уже показывало бы ошибку.
            if (State == SessionState.Joining && Time.unscaledTime > m_JoinDeadline)
            {
                m_Net.Shutdown();
                Fail($"сервер {ConnectedTo} не ответил за {JoinTimeoutSeconds:F0} с");
            }
        }

        void OnClientConnected(ulong clientId)
        {
            // Колбэк приходит и для чужих клиентов на сервере, нас интересует только своё подключение.
            if (clientId != m_Net.LocalClientId || State != SessionState.Joining)
                return;
            SetState(SessionState.InGame);
            Log($"в игре на {ConnectedTo}, id {clientId}");
        }

        void OnClientDisconnected(ulong clientId)
        {
            if (m_Net.IsServer || clientId != m_Net.LocalClientId && clientId != NetworkManager.ServerClientId)
                return;

            string reason = m_Net.DisconnectReason ?? string.Empty;
            if (State == SessionState.Joining)
                Fail(Explain(reason, joining: true));
            else if (State == SessionState.InGame)
                Fail(Explain(reason, joining: false));
        }

        /// <summary>
        /// Понятная игроку причина разрыва. DisconnectReason содержит либо текст, который прислал
        /// сервер при отказе (например, «Сервер заполнен» из GameRules), либо служебную строку
        /// транспорта: по ней отличаем падение сервера (тайм-аут) от штатной остановки.
        /// </summary>
        string Explain(string reason, bool joining)
        {
            // Steam-профиль: причину знает транспорт Steam Sockets.
            if (m_Net.NetworkConfig.NetworkTransport is SteamSocketsTransport steam)
            {
                if (steam.LastCloseState == Steamworks.ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
                    return joining ? $"сервер {ConnectedTo} отклонил подключение" : $"сервер {ConnectedTo} завершил работу";
                if (steam.LastCloseState == Steamworks.ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                    return joining
                        ? $"сервер {ConnectedTo} не отвечает"
                        : $"сервер {ConnectedTo} перестал отвечать (обрыв связи или падение сервера)";
            }

            if (reason.Contains("Timeout") || reason.Contains("timed out"))
                return joining
                    ? $"сервер {ConnectedTo} не отвечает"
                    : $"сервер {ConnectedTo} перестал отвечать (обрыв связи или падение сервера)";
            if (reason.Contains("ClosedByRemote"))
                return $"сервер {ConnectedTo} завершил работу";
            if (reason.Length > 0 && !reason.StartsWith("[Disconnect Event]"))
                return joining ? $"сервер отклонил подключение: {reason}" : $"сервер отключил игрока: {reason}";
            return joining ? $"не удалось подключиться к {ConnectedTo}" : $"соединение с {ConnectedTo} потеряно";
        }

        internal void Fail(string message)
        {
            LastError = message;
            SetState(SessionState.Error);
            Log("ошибка: " + message);
        }

        internal void SetState(SessionState state)
        {
            if (State == state)
                return;
            State = state;
            StateChanged?.Invoke(state);
        }

        static void Log(string message) =>
            Debug.Log($"[Session {DateTime.Now:HH:mm:ss}] {message}");
    }
}
