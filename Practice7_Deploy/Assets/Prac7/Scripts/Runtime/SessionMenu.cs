using System.Globalization;
using UnityEngine;

namespace Prac7
{
    /// <summary>
    /// Меню сессий на клиенте (IMGUI, как HUD в ПР4-ПР5). Только отображает состояние
    /// SessionClient и передаёт ему нажатия, сетевой логики здесь нет.
    /// </summary>
    [RequireComponent(typeof(SessionClient))]
    public class SessionMenu : MonoBehaviour
    {
        SessionClient m_Session;
        string m_Host = "127.0.0.1";
        string m_Port = "7777";

        void Awake()
        {
            m_Session = GetComponent<SessionClient>();
            // Кнопки Host/Client из ПР5 скрываем: на выделенном сервере игрок не может быть хостом.
            Prac5.Prac5Bootstrap.HideConnectButtons = true;
        }

        void OnGUI()
        {
            // Сервер без графики OnGUI не вызывает, но в редакторе компонент может оказаться на сервере.
            if (Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsServer)
                return;

            const float width = 380f;
            GUILayout.BeginArea(new Rect(Screen.width - width - 10, 10, width, Screen.height - 20), GUI.skin.box);
            GUILayout.Label($"<b>Сессия:</b> {StateTitle(m_Session.State)}", Rich());

            switch (m_Session.State)
            {
                case SessionState.InGame:
                    GUILayout.Label($"Сервер: {m_Session.ConnectedTo}");
                    // Фактический транспорт и маршрут: для отчёта о Steam Sockets и relay.
                    var transport = Unity.Netcode.NetworkManager.Singleton.NetworkConfig.NetworkTransport;
                    GUILayout.Label($"Транспорт: {transport.GetType().Name}");
                    if (transport is SteamSocketsTransport steam && !string.IsNullOrEmpty(steam.LastRouteInfo))
                        GUILayout.Label($"Маршрут: {steam.LastRouteInfo}", Rich());
                    if (GUILayout.Button("Выйти из сессии"))
                        m_Session.Leave();
                    break;

                case SessionState.Joining:
                    GUILayout.Label($"Подключение к {m_Session.ConnectedTo}...");
                    break;

                case SessionState.Error:
                    GUILayout.Label($"<color=#ff7070>{m_Session.LastError}</color>", Rich());
                    if (GUILayout.Button("Назад к поиску"))
                        m_Session.ClearError();
                    break;

                default:
                    DrawSteamSearch();
                    DrawDirectConnect();
                    break;
            }
            GUILayout.EndArea();
        }

        /// <summary>Основной путь: поиск выделенных серверов через Steam (Steam-профиль).</summary>
        void DrawSteamSearch()
        {
            GUILayout.Label("<b>Сессии Steam</b>", Rich());
            GUI.enabled = !m_Session.IsBusy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Найти сессии"))
                m_Session.SearchSessions(lan: false);
            if (GUILayout.Button("В локальной сети", GUILayout.Width(130)))
                m_Session.SearchSessions(lan: true);
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            if (m_Session.State == SessionState.Searching)
            {
                GUILayout.Label($"поиск... найдено: {m_Session.Results.Count}");
                return;
            }
            if (m_Session.State != SessionState.ResultsReady)
                return;

            if (m_Session.Results.Count == 0)
            {
                GUILayout.Label(m_Session.LastSearchWasLan
                    ? "В локальной сети сессий не найдено."
                    : "Сессий не найдено. Сервер запущен в Steam-профиле?");
                return;
            }

            foreach (var s in m_Session.Results)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label($"<b>{s.Name}</b>", Rich());
                GUILayout.Label($"карта {s.Map}, режим {s.Mode ?? "?"}, сборка {s.BuildId}");
                GUILayout.BeginHorizontal();
                GUILayout.Label($"игроков {s.Players}/{s.MaxPlayers}, пинг {s.Ping} мс");
                GUI.enabled = s.Players < s.MaxPlayers;
                if (GUILayout.Button(s.Players < s.MaxPlayers ? "Подключиться" : "Заполнена", GUILayout.Width(110)))
                    m_Session.JoinSession(s);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        void DrawDirectConnect()
        {
            GUILayout.Space(6);
            GUILayout.Label("<b>Прямое подключение (диагностика, IP-профиль)</b>", Rich());
            GUILayout.BeginHorizontal();
            GUILayout.Label("Адрес", GUILayout.Width(50));
            m_Host = GUILayout.TextField(m_Host);
            GUILayout.Label("Порт", GUILayout.Width(36));
            m_Port = GUILayout.TextField(m_Port, GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUI.enabled = !m_Session.IsBusy;
            if (GUILayout.Button("Подключиться по адресу"))
            {
                if (ushort.TryParse(m_Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort port) && port > 0)
                    m_Session.JoinDirect(m_Host, port);
                else
                    m_Session.Fail($"неверный порт «{m_Port}»");
            }
            GUI.enabled = true;
        }

        static string StateTitle(SessionState s) => s switch
        {
            SessionState.Idle => "нет подключения",
            SessionState.Searching => "поиск сессий...",
            SessionState.ResultsReady => "результаты поиска",
            SessionState.Joining => "подключение...",
            SessionState.InGame => "в игре",
            SessionState.Error => "ошибка",
            _ => s.ToString(),
        };

        static GUIStyle Rich() => new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
    }
}
