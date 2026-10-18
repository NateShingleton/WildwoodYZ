using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Wildwood.Core.Meshing;
using Wildwood.Core.Terrain;
using Wildwood.Game.Blocks;
using Debug = UnityEngine.Debug;

namespace Wildwood.Game.Terrain
{
    /// <summary>
    /// Streams the smooth ground, trees and grass around the camera. Everything is generated from the world seed
    /// on each machine, so nothing here is sent over the network. Work is spread across frames with a time budget,
    /// nearest columns first. A "column" is a 16 x 16 m area covering every vertical terrain chunk at that spot.
    /// </summary>
    public class TerrainView : MonoBehaviour
    {
        [SerializeField] Material groundMaterial;
        [SerializeField] Material treeMaterial;
        [SerializeField] Material grassMaterial;

        [Tooltip("How far terrain loads, in 16 m columns (8 = 128 m). Fog hides the edge.")]
        [SerializeField] int viewDistanceColumns = 8;

        [Tooltip("Milliseconds per frame spent building terrain.")]
        [SerializeField] float buildBudgetMs = 6f;

        [Tooltip("Grass is drawn this far from the camera (meters).")]
        [SerializeField] float grassDistance = 45f;

        sealed class Column
        {
            public int2 Coord;
            public GameObject Root;
            public readonly List<Mesh> Meshes = new List<Mesh>();
        }

        readonly Dictionary<int2, Column> _columns = new Dictionary<int2, Column>();
        readonly List<int2> _buildQueue = new List<int2>();
        readonly List<int2> _toUnload = new List<int2>();
        readonly List<TreeSpawn> _trees = new List<TreeSpawn>();
        readonly MeshBuffers _buffers = new MeshBuffers();
        readonly TerrainMesher _mesher = new TerrainMesher();
        readonly Stopwatch _stopwatch = new Stopwatch();

        TerrainGenerator _generator;
        GrassField _grass;
        Material _groundRuntime, _treeRuntime, _grassRuntime;
        Texture2D _groundPalette, _treePalette, _grassPalette;
        int2 _queueCenter = new int2(int.MinValue, int.MinValue);
        BlockWorldView _world;

        public static TerrainView Instance { get; private set; }

        public TerrainGenerator Generator => _generator;

        void Awake()
        {
            Instance = this;
            _groundPalette = NaturePalettes.CreateGroundTexture();
            _treePalette = NaturePalettes.CreateTreeTexture();
            _grassPalette = NaturePalettes.CreateGrassTexture();
            _groundRuntime = MakeRuntimeMaterial(groundMaterial, _groundPalette, "Ground (runtime)");
            _treeRuntime = MakeRuntimeMaterial(treeMaterial, _treePalette, "Trees (runtime)");
            _grassRuntime = MakeRuntimeMaterial(grassMaterial, _grassPalette, "Grass (runtime)");
            _grassRuntime.enableInstancing = true;
            _grass = new GrassField(_grassRuntime);
        }

        void Start()
        {
            _world = BlockWorldView.Instance;
            if (_world == null)
            {
                Debug.LogError("[Wildwood] TerrainView needs a BlockWorldView in the scene.");
                return;
            }
            _world.WorldStarted += Begin;
            _world.WorldEnded += End;
            if (_world.HasWorld) Begin(_world.Seed);
        }

        void OnDestroy()
        {
            if (_world != null)
            {
                _world.WorldStarted -= Begin;
                _world.WorldEnded -= End;
            }
            End();
            _grass?.Dispose();
            DestroyIfSet(_groundRuntime);
            DestroyIfSet(_treeRuntime);
            DestroyIfSet(_grassRuntime);
            DestroyIfSet(_groundPalette);
            DestroyIfSet(_treePalette);
            DestroyIfSet(_grassPalette);
            if (Instance == this) Instance = null;
        }

        /// <summary>Start generating the world for <paramref name="seed"/>.</summary>
        public void Begin(int seed)
        {
            End();
            _generator = new TerrainGenerator(seed);

            // Build the ground under the spawn point right away, so nobody waits in the air at the start.
            for (int z = -2; z <= 1; z++)
            for (int x = -2; x <= 1; x++)
                BuildColumn(new int2(x, z));
        }

        /// <summary>Unload everything (the session ended).</summary>
        public void End()
        {
            foreach (var column in _columns.Values) DestroyColumn(column);
            _columns.Clear();
            _buildQueue.Clear();
            _grass?.Clear();
            _generator = null;
            _queueCenter = new int2(int.MinValue, int.MinValue);
        }

        void Update()
        {
            if (_generator == null) return;

            Vector3 focus = FocusPoint();
            int2 center = ColumnOf(focus);

            if (!center.Equals(_queueCenter))
            {
                _queueCenter = center;
                RefreshQueue(center);
                UnloadFarColumns(center);
                _grass.Prune(center, (int)math.ceil(grassDistance / TerrainSettings.ChunkWorldSize) + 1);
            }

            _stopwatch.Restart();
            while (_buildQueue.Count > 0 && _stopwatch.Elapsed.TotalMilliseconds < buildBudgetMs)
            {
                int2 coord = _buildQueue[_buildQueue.Count - 1]; // the queue is sorted farthest first
                _buildQueue.RemoveAt(_buildQueue.Count - 1);
                if (!_columns.ContainsKey(coord)) BuildColumn(coord);
            }

            // Grass patches are cheap, but only worth making close by.
            int grassRadius = (int)math.ceil(grassDistance / TerrainSettings.ChunkWorldSize);
            for (int z = -grassRadius; z <= grassRadius; z++)
            for (int x = -grassRadius; x <= grassRadius; x++)
            {
                var coord = center + new int2(x, z);
                if (_columns.ContainsKey(coord)) _grass.EnsurePatch(_generator, coord);
            }
            _grass.Draw(focus, grassDistance);
        }

        static Vector3 FocusPoint()
        {
            var camera = Camera.main;
            return camera != null ? camera.transform.position : Vector3.zero;
        }

        static int2 ColumnOf(Vector3 position)
        {
            float size = TerrainSettings.ChunkWorldSize;
            return new int2((int)math.floor(position.x / size), (int)math.floor(position.z / size));
        }

        void RefreshQueue(int2 center)
        {
            _buildQueue.Clear();
            int r = viewDistanceColumns;
            for (int z = -r; z <= r; z++)
            for (int x = -r; x <= r; x++)
            {
                if (x * x + z * z > r * r) continue;
                var coord = center + new int2(x, z);
                if (!_columns.ContainsKey(coord)) _buildQueue.Add(coord);
            }
            // Farthest first, so the nearest is at the end and can be popped cheaply.
            _buildQueue.Sort((a, b) => DistanceSquared(b, center).CompareTo(DistanceSquared(a, center)));
        }

        static int DistanceSquared(int2 a, int2 b)
        {
            int2 d = a - b;
            return d.x * d.x + d.y * d.y;
        }

        void UnloadFarColumns(int2 center)
        {
            int keep = viewDistanceColumns + 2;
            _toUnload.Clear();
            foreach (var coord in _columns.Keys)
            {
                int2 d = coord - center;
                if (d.x * d.x + d.y * d.y > keep * keep) _toUnload.Add(coord);
            }
            foreach (var coord in _toUnload)
            {
                DestroyColumn(_columns[coord]);
                _columns.Remove(coord);
            }
        }

        void BuildColumn(int2 coord)
        {
            if (_columns.ContainsKey(coord)) return;
            float size = TerrainSettings.ChunkWorldSize;
            var column = new Column { Coord = coord, Root = new GameObject($"Terrain {coord.x}, {coord.y}") };
            column.Root.transform.SetParent(transform, false);
            column.Root.isStatic = true;
            _columns.Add(coord, column);

            for (int y = TerrainSettings.MinChunkY; y <= TerrainSettings.MaxChunkY; y++)
            {
                var chunk = new int3(coord.x, y, coord.y);
                if (!_mesher.Build(_generator, chunk, _buffers)) continue;

                var go = new GameObject($"Ground {y}");
                go.transform.SetParent(column.Root.transform, false);
                go.transform.position = new Vector3(chunk.x, chunk.y, chunk.z) * size;
                var mesh = CreateMesh(go.name);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = _groundRuntime;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                column.Meshes.Add(mesh);
            }

            BuildTrees(column, coord, size);
        }

        void BuildTrees(Column column, int2 coord, float size)
        {
            _trees.Clear();
            _generator.GetTrees(new float2(coord.x, coord.y) * size, size, _trees);
            if (_trees.Count == 0) return;

            var origin = new Vector3(coord.x * size, 0f, coord.y * size);
            var go = new GameObject("Trees");
            go.transform.SetParent(column.Root.transform, false);
            go.transform.position = origin;

            _buffers.Clear();
            foreach (var tree in _trees)
            {
                TreeMeshBuilder.AddTree(_buffers, tree, origin);

                // A collider on each trunk so players and the camera bump into trees.
                var trunk = go.AddComponent<CapsuleCollider>();
                float trunkHeight = TreeMeshBuilder.TrunkHeight(tree);
                trunk.direction = 1;
                trunk.radius = tree.TrunkRadius * 1.1f;
                trunk.height = trunkHeight;
                trunk.center = new Vector3(tree.Position.x, tree.Position.y, tree.Position.z) - origin + Vector3.up * (trunkHeight * 0.5f);
            }

            var mesh = CreateMesh("Trees");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _treeRuntime;
            column.Meshes.Add(mesh);
        }

        Mesh CreateMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(_buffers.Vertices);
            mesh.SetNormals(_buffers.Normals);
            mesh.SetUVs(0, _buffers.UVs);
            mesh.SetTriangles(_buffers.Indices, 0, true);
            return mesh;
        }

        static void DestroyIfSet(Object target)
        {
            if (target != null) Destroy(target);
        }

        static void DestroyColumn(Column column)
        {
            if (column.Root != null) Destroy(column.Root);
            foreach (var mesh in column.Meshes) Destroy(mesh);
        }

        static Material MakeRuntimeMaterial(Material source, Texture texture, string name)
        {
            Material material = source != null ? new Material(source) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (source == null) Debug.LogError($"[Wildwood] TerrainView is missing a material for {name}. Run Wildwood > Build Sandbox Scene.");
            material.name = name;
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            return material;
        }
    }
}
