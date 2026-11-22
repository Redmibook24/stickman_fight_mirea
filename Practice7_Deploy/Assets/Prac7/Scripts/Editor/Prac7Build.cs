using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Prac7.Editor
{
    /// <summary>
    /// Сборка ПР7: сцена ПР5 + компоненты выделенного сервера и сессий, затем два билда:
    ///   Builds/LinuxServer/StickmanServer.x86_64  серверный Linux-билд (Dedicated Server) для Docker;
    ///   Builds/WindowsClient/StickmanClient.exe   клиент для игроков.
    ///
    /// Из меню: Prac7 → ..., из командной строки (deploy/build_server.ps1):
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod Prac7.Editor.Prac7Build.BuildAll
    /// </summary>
    public static class Prac7Build
    {
        const string ScenePath = "Assets/Prac5/Scenes/Prac5_Replication.unity";
        const string ServerPath = "Builds/LinuxServer/StickmanServer.x86_64";
        const string ClientPath = "Builds/WindowsClient/StickmanClient.exe";

        /// <summary>
        /// Пересобирает сцену ПР5 и добавляет на объект NetworkManager компоненты ПР7.
        /// Сцена генерируется кодом (так было в ПР4-ПР5), поэтому и дополнения вносятся кодом,
        /// а не руками в редакторе: результат одинаков на любой машине.
        /// </summary>
        [MenuItem("Prac7/Собрать сцену")]
        public static void PrepareScene()
        {
            Prac5.Editor.Prac5SceneBuilder.Build();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var net = Object.FindFirstObjectByType<NetworkManager>();
            if (net == null)
                throw new BuildFailedException("В сцене ПР5 не найден NetworkManager");

            AddOnce<DedicatedServer>(net.gameObject);
            AddOnce<SessionClient>(net.gameObject);
            AddOnce<SessionMenu>(net.gameObject);
            // Steam-профиль: транспорт Steam Sockets (включается вместо UnityTransport при запуске)
            // и регистрация выделенного сервера в Steam.
            AddOnce<SteamSocketsTransport>(net.gameObject);
            AddOnce<SteamGameServerHost>(net.gameObject);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[Prac7] Сцена подготовлена: {ScenePath}");
        }

        /// <summary>
        /// Серверный Linux-билд. StandaloneBuildSubtarget.Server включает define UNITY_SERVER
        /// и выбрасывает графику, звук и окно: это самостоятельная серверная цель сборки,
        /// а не клиентский билд, запущенный без экрана.
        /// </summary>
        [MenuItem("Prac7/Собрать Linux Dedicated Server")]
        public static void BuildLinuxServer()
        {
            PrepareScene();
            Build(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ServerPath,
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None,
            });
        }

        [MenuItem("Prac7/Собрать Windows-клиент")]
        public static void BuildWindowsClient()
        {
            PrepareScene();
            Build(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ClientPath,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });
        }

        /// <summary>Оба билда подряд, точка входа для batch-режима.</summary>
        public static void BuildAll()
        {
            BuildLinuxServer();
            BuildWindowsClient();
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        static void Build(BuildPlayerOptions options)
        {
            // Старый билд удаляется целиком: иначе в образ могли бы попасть файлы прошлой сборки.
            string dir = Path.GetDirectoryName(options.locationPathName);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, true);

            BuildReport report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[Prac7] Билд {options.target}/{(StandaloneBuildSubtarget)options.subtarget}: {summary.result}, " +
                      $"{summary.totalSize / (1024 * 1024)} МБ, ошибок {summary.totalErrors} → {options.locationPathName}");

            if (summary.result != BuildResult.Succeeded)
            {
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                throw new BuildFailedException($"Сборка {options.target} не удалась");
            }

            // steam_appid.txt рядом с исполняемым файлом: по нему Steam API узнаёт App ID, когда игра
            // и сервер запущены не через сам Steam. Учебный App ID 480 (Spacewar).
            File.WriteAllText(Path.Combine(dir ?? ".", "steam_appid.txt"), Prac7.SessionMetadata.AppId.ToString());
        }

        // Явная проверка, а не `??`: в редакторе отсутствующий компонент возвращается
        // «фальшивым null», который `??` не распознаёт.
        static T AddOnce<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }
    }
}
