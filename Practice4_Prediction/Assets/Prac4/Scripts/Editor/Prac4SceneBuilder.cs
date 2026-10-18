using System.IO;
using Unity.Multiplayer.Tools.NetworkSimulator.Runtime;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Prac4.Editor
{
    /// <summary>
    /// Собирает демонстрационную сцену, префаб игрока и NetworkManager одной командой меню:
    /// Prac4 → Собрать сцену и префаб.
    /// </summary>
    public static class Prac4SceneBuilder
    {
        const string Root = "Assets/Prac4";
        const string PrefabPath = Root + "/Prefabs/Player.prefab";
        const string ScenePath = Root + "/Scenes/Prac4_Prediction.unity";

        [MenuItem("Prac4/Собрать сцену и префаб")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Directory.CreateDirectory(Root + "/Prefabs");
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Materials");
            AssetDatabase.Refresh();

            var matGround = MakeMaterial("Ground", new Color(0.55f, 0.58f, 0.6f));
            var matObstacle = MakeMaterial("Obstacle", new Color(0.35f, 0.4f, 0.55f));
            var matWall = MakeMaterial("Wall", new Color(0.25f, 0.27f, 0.3f));
            var matNose = MakeMaterial("Nose", new Color(0.1f, 0.1f, 0.1f));
            var matGhost = MakeMaterial("ServerGhost", new Color(0.2f, 0.5f, 1f));

            GameObject prefab = BuildPlayerPrefab(matNose, matGhost);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 12f, -10f);
                cam.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -11f, 9f));
            }

            BuildLevel(matGround, matObstacle, matWall);
            BuildNetworkManager(prefab);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            // Для запуска двух экземпляров на одном ПК: оба окна должны работать в фоне.
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            EditorUserBuildSettings.development = true; // Network Simulator работает в Editor и Development Build

            AssetDatabase.SaveAssets();
            Debug.Log($"[Prac4] Сцена собрана: {ScenePath}, префаб игрока: {PrefabPath}");
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

        static GameObject BuildPlayerPrefab(Material matNose, Material matGhost)
        {
            var root = new GameObject("Player");

            var cc = root.AddComponent<CharacterController>();
            cc.height = 2f;
            cc.radius = 0.5f;
            cc.center = Vector3.zero;
            cc.slopeLimit = 45f;
            cc.stepOffset = 0.4f;

            root.AddComponent<NetworkObject>();
            root.AddComponent<PlayerInputReader>();
            var movement = root.AddComponent<PredictedMovement>();
            var character = root.AddComponent<PlayerCharacter>();

            var visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(visual, false);

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Nose";
            Object.DestroyImmediate(nose.GetComponent<Collider>());
            nose.transform.SetParent(visual, false);
            nose.transform.localPosition = new Vector3(0f, 0.45f, 0.45f);
            nose.transform.localScale = new Vector3(0.35f, 0.2f, 0.3f);
            nose.GetComponent<Renderer>().sharedMaterial = matNose;

            var ghost = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ghost.name = "ServerGhost";
            Object.DestroyImmediate(ghost.GetComponent<Collider>());
            ghost.transform.SetParent(root.transform, false);
            ghost.transform.localPosition = new Vector3(0f, -0.95f, 0f);
            ghost.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
            ghost.GetComponent<Renderer>().sharedMaterial = matGhost;

            movement.Visual = visual;
            character.BodyRenderer = body.GetComponent<Renderer>();
            character.ServerGhost = ghost.transform;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static void BuildLevel(Material ground, Material obstacle, Material wall)
        {
            var level = new GameObject("Level").transform;

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Ground";
            floor.transform.SetParent(level);
            floor.transform.localScale = new Vector3(4f, 1f, 4f); // 40 x 40 м
            floor.GetComponent<Renderer>().sharedMaterial = ground;

            // ограждение арены
            Box(level, "Wall_N", new Vector3(0f, 1f, 20f), new Vector3(40f, 2f, 1f), wall);
            Box(level, "Wall_S", new Vector3(0f, 1f, -20f), new Vector3(40f, 2f, 1f), wall);
            Box(level, "Wall_E", new Vector3(20f, 1f, 0f), new Vector3(1f, 2f, 40f), wall);
            Box(level, "Wall_W", new Vector3(-20f, 1f, 0f), new Vector3(1f, 2f, 40f), wall);

            // препятствие для сценария «рывок в стену»
            Box(level, "DashWall", new Vector3(0f, 1.5f, 9f), new Vector3(10f, 3f, 1f), wall);

            // разрозненные препятствия
            Box(level, "Crate_1", new Vector3(-8f, 0.75f, 3f), new Vector3(1.5f, 1.5f, 1.5f), obstacle);
            Box(level, "Crate_2", new Vector3(-6f, 0.75f, -6f), new Vector3(1.5f, 1.5f, 1.5f), obstacle);
            Box(level, "Crate_3", new Vector3(7f, 0.75f, -4f), new Vector3(1.5f, 1.5f, 1.5f), obstacle);

            // приподнятая платформа с пандусом — для проверки «рывок в воздухе запрещён»
            Box(level, "Platform", new Vector3(12f, 1f, 10f), new Vector3(6f, 2f, 6f), obstacle);
            var ramp = Box(level, "Ramp", new Vector3(12f, 0.95f, 4.6f), new Vector3(3f, 0.3f, 5.5f), obstacle);
            ramp.transform.rotation = Quaternion.Euler(-22f, 0f, 0f);
        }

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent);
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        static void BuildNetworkManager(GameObject playerPrefab)
        {
            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();
            go.AddComponent<NetworkSimulator>();
            go.AddComponent<GameRules>();
            var hud = go.AddComponent<NetLabHud>();

            if (nm.NetworkConfig == null)
                nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.PlayerPrefab = playerPrefab;
            nm.NetworkConfig.ConnectionApproval = true;
            nm.NetworkConfig.TickRate = MovementSettings.TickRate; // пакеты уходят каждый сетевой тик

            transport.SetConnectionData("127.0.0.1", 7777, "0.0.0.0");

            hud.Network = nm;
            hud.Simulator = go.GetComponent<NetworkSimulator>();
            EditorUtility.SetDirty(nm);
        }
    }
}
