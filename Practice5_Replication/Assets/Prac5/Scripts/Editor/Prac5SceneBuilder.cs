using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Prac5.Editor
{
    /// <summary>
    /// Собирает нагруженную сцену практики 5 одной командой меню: Prac5 → Собрать сцену и префабы.
    /// Префаб игрока берётся из практики 4 (Assets/Prac4/Prefabs/Player.prefab).
    /// Для замеров без редактора: Prac5 → Собрать Windows-билд (или -executeMethod Prac5.Editor.Prac5SceneBuilder.BuildWindows).
    /// </summary>
    public static class Prac5SceneBuilder
    {
        const string Root = "Assets/Prac5";
        const string ScenePath = Root + "/Scenes/Prac5_Replication.unity";
        const string PlayerPrefabPath = "Assets/Prac4/Prefabs/Player.prefab";
        const string BuildPath = "Build/Prac5/prac5_replication.exe";

        [MenuItem("Prac5/Собрать сцену и префабы")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Directory.CreateDirectory(Root + "/Prefabs");
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Materials");
            AssetDatabase.Refresh();

            var ground = MakeMaterial("Ground", new Color(0.5f, 0.56f, 0.5f));
            var trunk = MakeMaterial("Trunk", new Color(0.4f, 0.27f, 0.15f));
            var leaves = MakeMaterial("Leaves", new Color(0.2f, 0.55f, 0.25f));
            var crate = MakeMaterial("Crate", new Color(0.75f, 0.55f, 0.3f));
            var pickup = MakeMaterial("Pickup", new Color(0.9f, 0.2f, 0.25f));
            var bot = MakeMaterial("Bot", new Color(0.55f, 0.3f, 0.8f));
            var projectile = MakeMaterial("Projectile", new Color(1f, 0.85f, 0.2f));
            var dark = MakeMaterial("Dark", new Color(0.1f, 0.1f, 0.1f));

            var prefabTree = MakeEntityPrefab("Tree", EntityKind.Tree, v =>
            {
                Part(v, PrimitiveType.Cylinder, new Vector3(0f, 1.5f, 0f), new Vector3(0.45f, 1.5f, 0.45f), trunk);
                Part(v, PrimitiveType.Sphere, new Vector3(0f, 3.6f, 0f), new Vector3(2.6f, 2.6f, 2.6f), leaves);
            });
            var prefabCrate = MakeEntityPrefab("Crate", EntityKind.Crate, v =>
                Part(v, PrimitiveType.Cube, new Vector3(0f, 0.75f, 0f), Vector3.one * 1.5f, crate));
            var prefabPickup = MakeEntityPrefab("Pickup", EntityKind.Pickup, v =>
            {
                Part(v, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(0.7f, 0.25f, 0.25f), pickup);
                Part(v, PrimitiveType.Cube, new Vector3(0f, 0.6f, 0f), new Vector3(0.25f, 0.25f, 0.7f), pickup);
            });
            var prefabBot = MakeEntityPrefab("Bot", EntityKind.Bot, v =>
            {
                Part(v, PrimitiveType.Capsule, new Vector3(0f, 1f, 0f), new Vector3(0.9f, 1f, 0.9f), bot);
                Part(v, PrimitiveType.Cube, new Vector3(0f, 1.45f, 0.4f), new Vector3(0.3f, 0.18f, 0.25f), dark);
            });
            var prefabProjectile = MakeEntityPrefab("Projectile", EntityKind.Projectile, v =>
                Part(v, PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.35f, projectile));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 60f, -50f);
                cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -60f, 50f));
                cam.farClipPlane = 400f;
            }

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Ground";
            floor.transform.localScale = new Vector3(17f, 1f, 17f); // 170 × 170 м
            floor.GetComponent<Renderer>().sharedMaterial = ground;
            floor.isStatic = true;

            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<CountingTransport>();
            var rules = go.AddComponent<Prac4.GameRules>();
            go.AddComponent<ReplicationServer>();
            go.AddComponent<ReplicationClient>();
            var director = go.AddComponent<ArenaDirector>();
            go.AddComponent<TrafficProfiler>();
            go.AddComponent<Prac5Bootstrap>();

            if (nm.NetworkConfig == null)
                nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.PlayerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            nm.NetworkConfig.ConnectionApproval = true;
            nm.NetworkConfig.TickRate = Prac4.MovementSettings.TickRate;
            transport.SetConnectionData("127.0.0.1", 7777, "0.0.0.0");

            rules.SpawnPoints = new[] { Autopilot.SpawnPoint(0), Autopilot.SpawnPoint(1), Autopilot.SpawnPoint(2), Autopilot.SpawnPoint(3) };
            director.TreePrefab = prefabTree;
            director.CratePrefab = prefabCrate;
            director.PickupPrefab = prefabPickup;
            director.BotPrefab = prefabBot;
            director.ProjectilePrefab = prefabProjectile;
            EditorUtility.SetDirty(nm);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;

            AssetDatabase.SaveAssets();
            Debug.Log($"[Prac5] Сцена собрана: {ScenePath}");
        }

        [MenuItem("Prac5/Собрать Windows-билд")]
        public static void BuildWindows()
        {
            Build();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = BuildPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[Prac5] Билд: {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} МБ → {BuildPath}");
            if (Application.isBatchMode)
                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        static GameObject MakeEntityPrefab(string name, EntityKind kind, System.Action<Transform> buildVisual)
        {
            var root = new GameObject(name);
            root.AddComponent<NetworkObject>();
            var entity = root.AddComponent<ReplicatedEntity>();
            entity.Kind = kind;
            var visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);
            buildVisual(visual);
            entity.Visual = visual;

            string path = $"{Root}/Prefabs/{name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Debug.Log($"[Prac5] Префаб {name}: PrefabIdHash = {prefab.GetComponent<NetworkObject>().PrefabIdHash}");
            return prefab;
        }

        static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        static Material MakeMaterial(string name, Color color)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Shader shader = GraphicsSettings.currentRenderPipeline != null
                    ? GraphicsSettings.currentRenderPipeline.defaultMaterial.shader
                    : Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
