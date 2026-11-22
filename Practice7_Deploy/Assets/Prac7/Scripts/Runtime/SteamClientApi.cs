using System;
using Steamworks;
using UnityEngine;

namespace Prac7
{
    /// <summary>
    /// Клиентская Steam-подсистема: инициализация SteamAPI, прокачка колбэков и завершение.
    /// Проверка доступности Steam выполняется один раз и кэшируется: повторный Init после неудачи
    /// бессмысленен, пока игрок не запустит Steam и не перезапустит игру.
    /// </summary>
    public static class SteamClientApi
    {
        static bool s_Tried;

        public static bool Available { get; private set; }
        public static string Error { get; private set; }

        /// <summary>
        /// Инициализирует SteamAPI при первом вызове. Нужен запущенный Steam с вошедшим пользователем
        /// и steam_appid.txt (480) рядом с exe (кладётся при сборке).
        /// </summary>
        public static bool EnsureInitialized()
        {
            if (s_Tried)
                return Available;
            s_Tried = true;

            try
            {
                var result = SteamAPI.InitEx(out string message);
                Available = result == ESteamAPIInitResult.k_ESteamAPIInitResult_OK;
                Error = Available ? null : $"{Describe(result)} ({message})";
            }
            catch (DllNotFoundException e)
            {
                Available = false;
                Error = "не найдена библиотека steam_api: " + e.Message;
            }

            if (Available)
            {
                Application.quitting += SteamAPI.Shutdown;
                Debug.Log($"[Steam] клиент: Steam API инициализирован, пользователь {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");
            }
            else
            {
                Debug.LogWarning("[Steam] клиент: Steam недоступен: " + Error);
            }
            return Available;
        }

        /// <summary>Вызывается каждый кадр: без этого ответы Steam (список серверов, сокеты) не приходят.</summary>
        public static void RunCallbacks()
        {
            if (Available)
                SteamAPI.RunCallbacks();
        }

        static string Describe(ESteamAPIInitResult r) => r switch
        {
            ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient => "Steam не запущен или пользователь не вошёл",
            ESteamAPIInitResult.k_ESteamAPIInitResult_VersionMismatch => "версия Steam-клиента устарела",
            _ => "ошибка инициализации Steam",
        };
    }
}
