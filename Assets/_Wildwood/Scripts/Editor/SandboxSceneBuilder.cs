using System.IO;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Wildwood.Game.Blocks;
using Wildwood.Game.Player;
using Wildwood.Game.Terrain;
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

            var stoneMat = GetOrCreateMaterial("Stone", new Color(0.62f, 0.60f, 0.56f));
            var bodyMat = GetOrCreateMaterial("PlayerBody", Color.white);
            var blocksMat = GetOrCreateMaterial("Blocks", Color.white);
            blocksMat.SetFloat("_Smoothness", 0.05f);
            var outlineMat = GetOrCreateMaterial("BlockOutline", new Color(1f, 1f, 1f), "Universal Render Pipeline/Unlit");
            var terrainMat = GetOrCreateMaterial("TerrainGround", Color.white);
            terrainMat.SetFloat("_Smoothness", 0f);
            var treeMat = GetOrCreateMaterial("Trees", Color.white);
            treeMat.SetFloat("_Smoothness", 0f);
            var grassMat = GetOrCreateMaterial("Grass", Color.white);
            grassMat.SetFloat("_Smoothness", 0f);
            grassMat.SetFloat("_Cull", 0f); // draw both sides of each blade
            grassMat.enableInstancing = true;
            var skyMat = GetOrCreateMaterial("Sky", Color.white, "Skybox/Procedural");

            // Make the new scene first so the temporary objects below never touch any other scene.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var playerPrefab = BuildPlayerPrefab(bodyMat);
            var prefabList = RegisterNetworkPrefab(playerPrefab);

            var sun = SetUpCameraAndLight(scene);
            SetUpAtmosphere(sun, skyMat);
            BuildEnvironment(stoneMat);
            BuildNetworkManager(playerPrefab, prefabList);
            BuildBlockWorld(blocksMat, outlineMat);
            BuildTerrain(terrainMat, treeMat, grassMat);
            new GameObject("SessionMenu").AddComponent<SessionMenu>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();

            Debug.Log("[Wildwood] Sandbox built. Open Window > Multiplayer > Multiplayer Play Mode, " +
                      "tick Player 2, then press Play. Host in one window and Join in the other.");
        }

        // ---------- Player ----------

        static GameObject BuildPlayerPrefab(Material bodyMaterial)
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

            // The low-poly character builds its own body when the game starts (see PlayerAvatar).
            var avatar = root.AddComponent<PlayerAvatar>();
            var avatarSerialized = new SerializedObject(avatar);
            avatarSerialized.FindProperty("baseMaterial").objectReferenceValue = bodyMaterial;
            avatarSerialized.ApplyModifiedPropertiesWithoutUndo();

            // Eye height of the character (first person looks from here; third person orbits it).
            var cameraTarget = new GameObject("CameraTarget").transform;
            cameraTarget.SetParent(root.transform, false);
            cameraTarget.localPosition = new Vector3(0f, 1.66f, 0f);

            var serialized = new SerializedObject(player);
            serialized.FindProperty("cameraTarget").objectReferenceValue = cameraTarget;
            serialized.FindProperty("bodyRenderer").objectReferenceValue = null;
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

        static Light SetUpCameraAndLight(Scene scene)
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
                mainCamera.farClipPlane = 600f;
                mainCamera.gameObject.AddComponent<CinemachineBrain>();
                var cameraData = mainCamera.GetUniversalAdditionalCameraData();
                cameraData.renderPostProcessing = true;
                cameraData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            }

            if (sun != null)
            {
                // Low golden-hour sun, in front of the players when they spawn (backlit, like the reference image).
                sun.transform.rotation = Quaternion.Euler(20f, 165f, 0f);
                sun.color = new Color(1f, 0.84f, 0.64f);
                sun.intensity = 2.0f;
                sun.shadows = LightShadows.Soft;
            }

            // The third-person camera. Before you spawn it just shows the overview.
            var cameraObject = new GameObject("PlayerFollowCamera");
            cameraObject.transform.SetPositionAndRotation(overview, overviewRotation);
            var followCamera = cameraObject.AddComponent<CinemachineCamera>();
            followCamera.Lens.FieldOfView = 55f;
            followCamera.Lens.NearClipPlane = 0.05f; // lets the camera get close to 10 cm blocks without clipping
            followCamera.Lens.FarClipPlane = 600f;

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
            return sun;
        }

        /// <summary>Golden-hour sky, warm haze and color grading.</summary>
        static void SetUpAtmosphere(Light sun, Material sky)
        {
            sky.SetFloat("_SunSize", 0.06f);
            sky.SetFloat("_SunSizeConvergence", 4f);
            sky.SetFloat("_AtmosphereThickness", 1.3f);
            sky.SetColor("_SkyTint", new Color(0.56f, 0.58f, 0.62f));
            sky.SetColor("_GroundColor", new Color(0.80f, 0.74f, 0.62f));
            sky.SetFloat("_Exposure", 1.2f);
            RenderSettings.skybox = sky;
            if (sun != null) RenderSettings.sun = sun;

            // Fixed warm ambient light (doesn't need a lighting bake).
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.66f, 0.74f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.58f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.26f, 0.20f);

            // Haze. It also hides where the terrain stops loading (about 130 m out).
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.84f, 0.77f, 0.63f);
            RenderSettings.fogStartDistance = 30f;
            RenderSettings.fogEndDistance = 140f;

            var volumeObject = new GameObject("PostProcessing");
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = CreatePostProcessingProfile();
        }

        static VolumeProfile CreatePostProcessingProfile()
        {
            EnsureFolder(Root + "/Settings");
            string path = Root + "/Settings/PostProcessing.asset";
            AssetDatabase.DeleteAsset(path);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);

            var tonemapping = AddOverride<Tonemapping>(profile);
            tonemapping.mode.Override(TonemappingMode.Neutral);

            var bloom = AddOverride<Bloom>(profile);
            bloom.intensity.Override(0.6f);
            bloom.threshold.Override(0.95f);
            bloom.scatter.Override(0.75f);
            bloom.tint.Override(new Color(1f, 0.88f, 0.72f));

            var color = AddOverride<ColorAdjustments>(profile);
            color.postExposure.Override(0.15f);
            color.contrast.Override(10f);
            color.saturation.Override(12f);
            color.colorFilter.Override(new Color(1f, 0.96f, 0.9f));

            var vignette = AddOverride<Vignette>(profile);
            vignette.intensity.Override(0.22f);
            vignette.smoothness.Override(0.45f);

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        static T AddOverride<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var component = profile.Add<T>(false);
            component.name = typeof(T).Name;
            component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        static void BuildTerrain(Material ground, Material trees, Material grass)
        {
            var terrainObject = new GameObject("Terrain");
            var view = terrainObject.AddComponent<TerrainView>();
            var serialized = new SerializedObject(view);
            serialized.FindProperty("groundMaterial").objectReferenceValue = ground;
            serialized.FindProperty("treeMaterial").objectReferenceValue = trees;
            serialized.FindProperty("grassMaterial").objectReferenceValue = grass;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildEnvironment(Material stone)
        {
            // The ground itself is generated terrain now; the spawn clearing is flat at height 0.
            var environment = new GameObject("Environment").transform;

            // Step test: rises of 10, 20, 30 and 40 cm. You should walk up the first three and need to jump the last.
            for (int i = 0; i < 4; i++)
            {
                float height = 0.1f * (i + 1);
                CreateBox(environment, $"Step {(i + 1) * 10} cm", new Vector3(-5f + i * 2f, height * 0.5f, 3f),
                    new Vector3(1.5f, height, 1.5f), stone);
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

            // Pieces that lose support break off and fall as physics objects.
            blockWorld.AddComponent<FallingPieces>();
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
