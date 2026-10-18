using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Wildwood.Core.Blocks;
using Wildwood.Core.Meshing;
using Wildwood.Net;

namespace Wildwood.Game.Blocks
{
    /// <summary>
    /// The scene object that owns this machine's copy of the block world. It creates the network sync,
    /// and turns changed chunks into meshes and colliders a few per frame.
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

        [Tooltip("When hosting, place the demo builds (pyramid, ramp, boulder, wall) at the start of a session.")]
        [SerializeField] bool buildDemoStructures = true;

        public BlockWorld World { get; private set; }

        public Material OutlineMaterial => outlineMaterial;

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
        }

        void OnDestroy()
        {
            if (_sync != null)
            {
                _sync.HostStarted -= OnHostStarted;
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

        void OnHostStarted()
        {
            if (buildDemoStructures) DemoStructures.Build(World);
        }

        void Update()
        {
            _sync?.Tick();

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
