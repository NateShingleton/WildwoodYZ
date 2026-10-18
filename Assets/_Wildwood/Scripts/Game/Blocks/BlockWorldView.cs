using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Wildwood.Core.Blocks;
using Wildwood.Core.Meshing;
using Wildwood.Game.Persistence;
using Wildwood.Net;

namespace Wildwood.Game.Blocks
{
    /// <summary>
    /// The scene object that owns this machine's copy of the world. It creates the network sync, loads and saves
    /// the world when hosting, announces the world seed (terrain listens for it), and turns changed block chunks
    /// into meshes and colliders a few per frame.
    /// </summary>
    public class BlockWorldView : MonoBehaviour
    {
        public static BlockWorldView Instance { get; private set; }

        [Tooltip("URP Lit material for blocks. Its base map is replaced at runtime with the generated color palette.")]
        [SerializeField] Material blockMaterial;

        [Tooltip("Unlit material for the aiming outline.")]
        [SerializeField] Material outlineMaterial;

        [Tooltip("Chunk meshes rebuilt per frame at most. Higher = changes appear sooner, but bigger hitches.")]
        [SerializeField] int chunkRebuildsPerFrame = 8;

        [Tooltip("When hosting a brand new world, place the demo builds (pyramid, ramp, boulder, wall).")]
        [SerializeField] bool buildDemoStructures = true;

        [Tooltip("When hosting, save the world this often (seconds). It also saves when the session ends.")]
        [SerializeField] float autosaveSeconds = 60f;

        public BlockWorld World { get; private set; }

        /// <summary>The current world's seed (valid while <see cref="HasWorld"/>).</summary>
        public int Seed { get; private set; }

        public bool HasWorld { get; private set; }

        /// <summary>A world became available on this machine (hosting, or joined and got the seed).</summary>
        public event Action<int> WorldStarted;

        /// <summary>The session ended and the world was unloaded.</summary>
        public event Action WorldEnded;

        public Material OutlineMaterial => outlineMaterial;

        /// <summary>The material blocks draw with (palette already applied). Falling pieces share it.</summary>
        public Material BlockMaterial => _runtimeMaterial;

        /// <summary>The network sync (null until Start). Falling pieces listen to it.</summary>
        public BlockWorldSync Sync => _sync;

        /// <summary>True while in a session, so edits can be sent.</summary>
        public bool CanEdit => _sync != null && _sync.IsRunning;

        sealed class ChunkView
        {
            public GameObject Object;
            public Mesh Mesh;
            public MeshCollider Collider;
        }

        readonly Dictionary<int3, ChunkView> _views = new Dictionary<int3, ChunkView>();
        readonly List<int3> _dirty = new List<int3>();
        readonly Queue<int3> _rebuildQueue = new Queue<int3>();
        readonly HashSet<int3> _queued = new HashSet<int3>();
        readonly MeshBuffers _buffers = new MeshBuffers();

        BlockWorldSync _sync;
        Material _runtimeMaterial;
        Texture2D _palette;
        bool _isHostSession;
        float _nextAutosave;

        void Awake()
        {
            Instance = this;
            World = new BlockWorld();
            World.Cleared += DestroyAllChunkViews;

            _palette = BlockPalette.CreateTexture();
            if (blockMaterial == null)
            {
                Debug.LogError("[Wildwood] BlockWorldView has no block material. Run Wildwood > Build Sandbox Scene.");
                _runtimeMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            }
            else
            {
                _runtimeMaterial = new Material(blockMaterial);
            }
            _runtimeMaterial.name = "Blocks (runtime)";
            _runtimeMaterial.SetTexture("_BaseMap", _palette);
            _runtimeMaterial.SetColor("_BaseColor", Color.white);
        }

        void Start()
        {
            var manager = NetworkManager.Singleton;
            if (manager == null)
            {
                Debug.LogError("[Wildwood] BlockWorldView needs a NetworkManager in the scene.");
                return;
            }
            _sync = new BlockWorldSync(manager, World);
            _sync.HostStarted += OnHostStarted;
            _sync.WorldInfoReceived += OnWorldInfoReceived;
            _sync.Stopping += OnSessionStopping;
        }

        void OnDestroy()
        {
            if (_sync != null)
            {
                _sync.HostStarted -= OnHostStarted;
                _sync.WorldInfoReceived -= OnWorldInfoReceived;
                _sync.Stopping -= OnSessionStopping;
                _sync.Dispose();
            }
            if (World != null) World.Cleared -= DestroyAllChunkViews;
            DestroyAllChunkViews();
            if (_runtimeMaterial != null) Destroy(_runtimeMaterial);
            if (_palette != null) Destroy(_palette);
            if (Instance == this) Instance = null;
        }

        /// <summary>Send an edit (applied on the host, then on everyone).</summary>
        public void RequestEdit(in BlockEdit edit)
        {
            _sync?.RequestEdit(edit);
        }

        /// <summary>Undo this player's most recent edit.</summary>
        public void RequestUndo()
        {
            _sync?.RequestUndo();
        }

        /// <summary>Clear a fallen piece away.</summary>
        public void RequestRemovePiece(uint id)
        {
            _sync?.RequestRemovePiece(id);
        }

        /// <summary>
        /// Rebuild every changed chunk right now instead of a few per frame. Used when a piece breaks off,
        /// so the world's colliders no longer contain the piece's blocks before it starts falling.
        /// </summary>
        public void RebuildDirtyNow()
        {
            World.TakeDirty(_dirty);
            foreach (int3 coord in _dirty)
            {
                if (_queued.Add(coord)) _rebuildQueue.Enqueue(coord);
            }
            _dirty.Clear();
            while (_rebuildQueue.Count > 0)
            {
                int3 coord = _rebuildQueue.Dequeue();
                _queued.Remove(coord);
                Rebuild(coord);
            }
        }

        void OnHostStarted()
        {
            _isHostSession = true;
            bool startFresh = WorldOptions.ConsumeNewWorldRequest();

            int seed;
            if (!startFresh && WorldStorage.TryLoad(World, out seed))
            {
                Debug.Log($"[Wildwood] Loaded world (seed {seed}) from {WorldStorage.FilePath}");
            }
            else
            {
                seed = new System.Random().Next(1, int.MaxValue);
                if (buildDemoStructures) DemoStructures.Build(World);
                WorldStorage.TrySave(World, seed);
                Debug.Log($"[Wildwood] Started a new world (seed {seed}). It saves to {WorldStorage.FilePath}");
            }

            _sync.WorldSeed = seed;
            _nextAutosave = Time.unscaledTime + autosaveSeconds;
            BeginWorld(seed);
        }

        void OnWorldInfoReceived(int seed)
        {
            BeginWorld(seed);
        }

        void BeginWorld(int seed)
        {
            Seed = seed;
            HasWorld = true;
            WorldStarted?.Invoke(seed);
        }

        void OnSessionStopping(bool wasHost)
        {
            // On the host this runs twice (server and client both stop); only the first one saves.
            if (_isHostSession)
            {
                _isHostSession = false;
                if (WorldStorage.TrySave(World, Seed)) Debug.Log("[Wildwood] World saved.");
            }
            if (HasWorld)
            {
                HasWorld = false;
                WorldEnded?.Invoke();
            }
        }

        void Update()
        {
            _sync?.Tick();

            if (_isHostSession && autosaveSeconds > 0f && Time.unscaledTime >= _nextAutosave)
            {
                _nextAutosave = Time.unscaledTime + autosaveSeconds;
                WorldStorage.TrySave(World, Seed);
            }

            World.TakeDirty(_dirty);
            foreach (int3 coord in _dirty)
            {
                if (_queued.Add(coord)) _rebuildQueue.Enqueue(coord);
            }
            _dirty.Clear();

            int budget = Mathf.Max(1, chunkRebuildsPerFrame);
            while (budget-- > 0 && _rebuildQueue.Count > 0)
            {
                int3 coord = _rebuildQueue.Dequeue();
                _queued.Remove(coord);
                Rebuild(coord);
            }
        }

        void Rebuild(int3 coord)
        {
            ChunkMesher.Build(World, coord, _buffers, BlockPalette.Rows);

            if (_buffers.IsEmpty)
            {
                if (_views.TryGetValue(coord, out var old))
                {
                    DestroyView(old);
                    _views.Remove(coord);
                }
                return;
            }

            if (!_views.TryGetValue(coord, out var view)) view = CreateView(coord);

            Mesh mesh = view.Mesh;
            mesh.Clear();
            mesh.SetVertices(_buffers.Vertices);
            mesh.SetNormals(_buffers.Normals);
            mesh.SetUVs(0, _buffers.UVs);
            mesh.SetTriangles(_buffers.Indices, 0, true);

            // Reassigning makes Unity rebuild the collision shape from the new mesh.
            view.Collider.sharedMesh = null;
            view.Collider.sharedMesh = mesh;
        }

        ChunkView CreateView(int3 coord)
        {
            var go = new GameObject($"Chunk {coord.x}, {coord.y}, {coord.z}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(coord.x, coord.y, coord.z) * BlockConstants.ChunkWorldSize;

            var mesh = new Mesh { name = go.name, indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _runtimeMaterial;
            var collider = go.AddComponent<MeshCollider>();

            var view = new ChunkView { Object = go, Mesh = mesh, Collider = collider };
            _views.Add(coord, view);
            return view;
        }

        void DestroyAllChunkViews()
        {
            foreach (var view in _views.Values) DestroyView(view);
            _views.Clear();
            _rebuildQueue.Clear();
            _queued.Clear();
        }

        static void DestroyView(ChunkView view)
        {
            if (view.Object != null) Destroy(view.Object);
            if (view.Mesh != null) Destroy(view.Mesh);
        }
    }
}
