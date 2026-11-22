using UnityEngine;

namespace Prac7
{
    /// <summary>
    /// Метаданные сессии, общие для сервера (публикует) и клиента (фильтрует).
    ///
    /// Учебный App ID 480 (Spacewar) общий для всех разработчиков, поэтому в списке серверов
    /// встречаются чужие сессии. Наши отличаются каталогом игры (gamedir) и тегами проекта и сборки:
    /// по ним фильтрует мастер-сервер Steam, а клиент дополнительно перепроверяет теги у себя.
    /// </summary>
    public static class SessionMetadata
    {
        /// <summary>Учебный App ID Steamworks (Spacewar).</summary>
        public const uint AppId = 480;

        /// <summary>Каталог игры: публикуется через SetModDir, фильтр мастер-сервера «gamedir».</summary>
        public const string GameDir = "stickman_pr7";

        public const string ProjectTag = "MultiplayerCourse_Stickman";
        public const string GameMode = "Deathmatch";

        /// <summary>Порт ответов на запросы списка серверов Steam (A2S query).</summary>
        public const ushort QueryPort = 27015;

        /// <summary>Совместимость сборок: клиент видит только серверы своей версии.</summary>
        public static string BuildId => Application.version;

        /// <summary>
        /// Теги в строке SetGameTags (до 127 символов, через запятую). Ключевые поля задания:
        /// PROJECT_TAG, BUILD_ID, GAME_MODE. MAP_NAME и MAX_PLAYERS публикуются штатными полями
        /// сервера (SetMapName, SetMaxPlayerCount), их Steam отдаёт в gameserveritem_t.
        /// </summary>
        public static string Tags => $"proj:{ProjectTag},build:{BuildId},mode:{GameMode}";

        /// <summary>Фильтр gametagsand: сервер должен иметь ВСЕ перечисленные теги.</summary>
        public static string RequiredTags => $"proj:{ProjectTag},build:{BuildId}";

        /// <summary>Значение тега по ключу, например Tag(tags, "mode") → Deathmatch.</summary>
        public static string Tag(string tags, string key)
        {
            if (string.IsNullOrEmpty(tags))
                return null;
            foreach (string part in tags.Split(','))
                if (part.StartsWith(key + ":"))
                    return part.Substring(key.Length + 1);
            return null;
        }
    }
}
