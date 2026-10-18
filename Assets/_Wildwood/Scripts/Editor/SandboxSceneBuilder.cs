using System.IO;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Wildwood.Game.Blocks;
using Wildwood.Game.Player;
using Wildwood.Game.UI;

namespace Wildwood.EditorTools
{
    /// <summary>
    /// Builds the networked test scene and the player prefab from code, so nothing has to be wired up by hand.
    /// Menu: Wildwood > Build Sandbox Scene. Safe to run again; it rebuilds both from scratch.
    /// </summary>
    public static class SandboxSceneBuilder
    {
        const string Root = "Assets/_Wildwood";
        const string ScenePath = Root + "/Scenes/Sandbox.unity";
        const string PrefabPath = Root + "/Prefabs/Player.prefab";
        const string MaterialFolder = Root + "/Materials";
        const string NetworkPrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";

        [MenuItem("Wildwood/Build Sandbox Scene", priority = 0)]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Wildwood", "Exit Play Mode first.", "OK");
                return;
            }
            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("Rebuild sandbox?",
                    "This replaces Sandbox.unity and Player.prefab with fresh copies.", "Rebuild", "Cancel"))
            {
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EnsureFolder(Root + "/Scenes");
            EnsureFolder(Root + "/Prefabs");
            EnsureFolder(MaterialFolder);

            var groundMat = GetOrCreateMaterial("Ground", new Color(0.42f, 0.55f, 0.27f));
            var stoneMat = GetOrCreateMaterial("Stone", new Color(0.62f, 0.60f, 0.56f));
            var woodMat = GetOrCreateMaterial("Wood", new Color(0.66f, 0.46f, 0.26f));
            var bodyMat = GetOrCreateMaterial("PlayerBody", Color.white);
            var visorMat = GetOrCreateMaterial("PlayerVisor", new Color(0.12f, 0.13f, 0.16f));
            var blocksMat = GetOrCreateMaterial("Blocks", Color.white);
            blocksMat.SetFloat("_Smoothness", 0.05f);
            var outlineMat = GetOrCreateMaterial("BlockOutline", new Color(1f, 1f, 1f), "Universal Render Pipeline/Unlit");

            // Make the new scene first so the temporary objects below never touch any other scene.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var playerPrefab = BuildPlayerPrefab(bodyMat, visorMat);
            var prefabList = RegisterNetworkPrefab(playerPrefab);

            SetUpCameraAndLight(scene);
            BuildEnvironment(groundMat, stoneMat, woodMat);
            BuildNetworkManager(playerPrefab, prefabList);
            BuildBlockWorld(blocksMat, outlineMat);
            new GameObject("SessionMenu").AddComponent<SessionMenu>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();

            Debug.Log("[Wildwood] Sandbox built. Open Window > Multiplayer > Multiplayer Play Mode, " +
                      "tick Player 2, then press Play. Host in one window and Join in the other.");
        }

        // ---------- Player ----------

        static GameObject BuildPlayerPrefab(Material bodyMaterial, Material visorMaterial)
        {
            var root = new GameObject("Player") { tag = "Player" };

            var controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.stepOffset = 0.35f; // climbs up to 3 blocks (30 cm) without jumping
            controller.skinWidth = 0.03f;
            controller.slopeLimit = 50f;

            root.AddComponent<NetworkObject>();

            var netTransform = root.AddComponent<NetworkTransform>();
            netTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            netTransform.SyncRotAngleX = false; // characters only turn around the vertical axis
            netTransform.SyncRotAngleZ = false;
            netTransform.SyncScaleX = false;
            netTransform.SyncScaleY = false;
            netTransform.SyncScaleZ = false;

            var player = root.AddComponent<PlayerController>();

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            var bodyRenderer = body.GetComponent<MeshRenderer>();
            bodyRenderer.sharedMaterial = bodyMaterial;

            // A dark visor so you can tell which way each player is facing.
            var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "Visor";
            Object.DestroyImmediate(visor.GetComponent<Collider>());
            visor.transform.SetParent(root.transform, false);
            visor.transform.localPosition = new Vector3(0f, 1.45f, 0.28f);
            visor.transform.localScale = new Vector3(0.42f, 0.14f, 0.14f);
            visor.GetComponent<MeshRenderer>().sharedMaterial = visorMaterial;

            var cameraTarget = new GameObject("CameraTarget").transform;
            cameraTarget.SetParent(root.transform, false);
            cameraTarget.localPosition = new Vector3(0f, 1.6f, 0f);

            var serialized = new SerializedObject(player);
            serialized.FindProperty("cameraTarget").objectReferenceValue = cameraTarget;
            serialized.FindProperty("bodyRenderer").objectReferenceValue = bodyRenderer;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var interactor = root.AddComponent<BlockInteractor>();
            var interactorSerialized = new SerializedObject(interactor);
            interactorSerialized.FindProperty("eye").objectReferenceValue = cameraTarget;
            interactorSerialized.ApplyModifiedPropertiesWithoutUndo();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
            Object.DestroyImmediate(root);
            if (!saved) throw new IOException("[Wildwood] Couldn't save " + PrefabPath);

            return EnsurePrefabHash(prefab);
        }

        /// <summary>
        /// Netcode identifies network prefabs by a hash it normally fills in when the prefab is imported.
        /// If that hasn't happened yet, reimport; as a last resort, store a stable hash of the asset's GUID.
        /// </summary>
        static GameObject EnsurePrefabHash(GameObject prefab)
        {
            if (prefab.GetComponent<NetworkObject>().PrefabIdHash != 0) return prefab;

            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject.PrefabIdHash != 0) return prefab;

            var serialized = new SerializedObject(networkObject);
            var hash = serialized.FindProperty("GlobalObjectIdHash");
            if (hash == null)
            {
                Debug.LogWarning("[Wildwood] Couldn't set the player prefab's network hash. If joining fails, " +
                                 "select Player.prefab once in the Project window and run the build again.");
                return prefab;
            }
            hash.uintValue = StableHash(AssetDatabase.AssetPathToGUID(PrefabPath));
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(networkObject);
            AssetDatabase.SaveAssets();
            return prefab;
        }

        static uint StableHash(string text)
        {
            uint hash = 2166136261; // FNV-1a
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash == 0 ? 1u : hash;
        }

        static NetworkPrefabsList RegisterNetworkPrefab(GameObject prefab)
        {
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                AssetDatabase.CreateAsset(list, NetworkPrefabsListPath);
            }
            if (!list.Contains(prefab))
            {
                list.Add(new NetworkPrefab { Prefab = prefab });
                EditorUtility.SetDirty(list);
                AssetDatabase.SaveAssets();
            }
            return list;
        }

        // ---------- Scene ----------

        static void SetUpCameraAndLight(Scene scene)
        {
            Camera mainCamera = null;
            Light sun = null;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (mainCamera == null) mainCamera = go.GetComponent<Camera>();
                if (sun == null) sun = go.GetComponent<Light>();
            }

            var overview = new Vector3(0f, 8f, -18f);
            var overviewRotation = Quaternion.Euler(20f, 0f, 0f);

            if (mainCamera != null)
            {
                mainCamera.transform.SetPositionAndRotation(overview, overviewRotation);
                mainCamera.gameObject.AddComponent<CinemachineBrain>();
            }

            if (sun != null)
            {
                sun.transform.rotation = Quaternion.Euler(38f, -40f, 0f); // warm, low-ish sun
                sun.color = new Color(1f, 0.92f, 0.80f);
                sun.intensity = 1.6f;
                sun.shadows = LightShadows.Soft;
            }

            // The third-person camera. Before you spawn it just shows the overview.
            var cameraObject = new GameObject("PlayerFollowCamera");
            cameraObject.transform.SetPositionAndRotation(overview, overviewRotation);
            var followCamera = cameraObject.AddComponent<CinemachineCamera>();
            followCamera.Lens.FieldOfView = 55f;
            followCamera.Lens.NearClipPlane = 0.05f; // lets the camera get close to 10 cm blocks without clipping
            followCamera.Lens.FarClipPlane = 1000f;

            var thirdPerson = cameraObject.AddComponent<CinemachineThirdPersonFollow>();
            thirdPerson.ShoulderOffset = new Vector3(0.55f, 0.15f, 0f);
            thirdPerson.VerticalArmLength = 0.25f;
            thirdPerson.CameraSide = 1f;
            thirdPerson.CameraDistance = 4.5f;
            thirdPerson.Damping = new Vector3(0.1f, 0.3f, 0.2f);
            thirdPerson.AvoidObstacles.Enabled = true;
            thirdPerson.AvoidObstacles.CollisionFilter = ~0;
            thirdPerson.AvoidObstacles.IgnoreTag = "Player";
            thirdPerson.AvoidObstacles.CameraRadius = 0.2f;

            var rig = cameraObject.AddComponent<PlayerCameraRig>();
            var serialized = new SerializedObject(rig);
            serialized.FindProperty("followCamera").objectReferenceValue = followCamera;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildEnvironment(Material ground, Material stone, Material wood)
        {
            var environment = new GameObject("Environment").transform;

            CreateBox(environment, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(200f, 1f, 200f), ground);

            // Step test: rises of 10, 20, 30 and 40 cm. You should walk up the first three and need to jump the last.
            for (int i = 0; i < 4; i++)
            {
                float height = 0.1f * (i + 1);
                CreateBox(environment, $"Step {(i + 1) * 10} cm", new Vector3(-5f + i * 2f, height * 0.5f, 3f),
                    new Vector3(1.5f, height, 1.5f), stone);
            }

            // A 20 degree ramp up to a platform.
            var ramp = CreateBox(environment, "Ramp", new Vector3(8f, 0.9f, 4f), new Vector3(3f, 0.2f, 6f), wood);
            ramp.transform.rotation = Quaternion.Euler(-20f, 0f, 0f);
            CreateBox(environment, "Platform", new Vector3(8f, 1.0f, 9f), new Vector3(5f, 2f, 5f), stone);

            // Scattered pillars give a sense of speed and scale.
            var random = new System.Random(7);
            for (int i = 0; i < 24; i++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float distance = 14f + (float)random.NextDouble() * 40f;
                float height = 1f + (float)random.NextDouble() * 5f;
                float width = 0.6f + (float)random.NextDouble() * 1.5f;
                var position = new Vector3(Mathf.Cos(angle) * distance, height * 0.5f, Mathf.Sin(angle) * distance);
                CreateBox(environment, $"Pillar {i}", position, new Vector3(width, height, width), i % 3 == 0 ? wood : stone);
            }
        }

        static GameObject CreateBox(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
            box.isStatic = true;
            return box;
        }

        static void BuildNetworkManager(GameObject playerPrefab, NetworkPrefabsList prefabList)
        {
            var managerObject = new GameObject("NetworkManager");
            var transport = managerObject.AddComponent<UnityTransport>();
            var manager = managerObject.AddComponent<NetworkManager>();

            if (manager.NetworkConfig == null) manager.NetworkConfig = new NetworkConfig();
            manager.NetworkConfig.NetworkTransport = transport;
            manager.NetworkConfig.PlayerPrefab = playerPrefab;

            var lists = manager.NetworkConfig.Prefabs.NetworkPrefabsLists;
            lists.RemoveAll(l => l == null);
            if (!lists.Contains(prefabList)) lists.Add(prefabList);

            EditorUtility.SetDirty(manager);
        }

        static void BuildBlockWorld(Material blocksMaterial, Material outlineMaterial)
        {
            var blockWorld = new GameObject("BlockWorld");
            var view = blockWorld.AddComponent<BlockWorldView>();
            var serialized = new SerializedObject(view);
            serialized.FindProperty("blockMaterial").objectReferenceValue = blocksMaterial;
            serialized.FindProperty("outlineMaterial").objectReferenceValue = outlineMaterial;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---------- Assets ----------

        static Material GetOrCreateMaterial(string name, Color color, string shaderName = "Universal Render Pipeline/Lit")
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogError($"[Wildwood] Couldn't find the shader '{shaderName}'.");
                    shader = Shader.Find("Hidden/InternalErrorShader");
                }
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
