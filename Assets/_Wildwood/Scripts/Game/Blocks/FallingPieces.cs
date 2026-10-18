using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Wildwood.Core.Blocks;
using Wildwood.Core.Meshing;
using Wildwood.Net;

namespace Wildwood.Game.Blocks
{
    /// <summary>
    /// Turns pieces that broke off the world into physics objects. On the host they're real rigidbodies;
    /// their positions are streamed to everyone else, whose copies just glide along (kinematic).
    /// For now fallen pieces stay as debris (the oldest vanish past a limit, and players can click one to clear it).
    /// Shattering into rubble and pickups is the next step.
    /// </summary>
    public class FallingPieces : MonoBehaviour
    {
        [Tooltip("How often the host sends piece positions (seconds).")]
        [SerializeField] float sendInterval = 0.05f;

        [Tooltip("Pieces that fall below this height are removed.")]
        [SerializeField] float removeBelowHeight = -60f;

        [Tooltip("Pieces needing more collision boxes than this use one rough box instead.")]
        [SerializeField] int maxColliderBoxes = 96;

        readonly Dictionary<uint, FallingPiece> _pieces = new Dictionary<uint, FallingPiece>();
        readonly List<BlockPieceState> _states = new List<BlockPieceState>();
        readonly List<uint> _toRemove = new List<uint>();
        readonly List<int3> _dirty = new List<int3>();
        readonly List<(int3 min, int3 max)> _boxes = new List<(int3 min, int3 max)>();
        readonly MeshBuffers _buffers = new MeshBuffers();

        BlockWorldSync _sync;
        float _nextSend;

        void Update()
        {
            if (_sync == null) TrySubscribe();
            if (_sync == null || !_sync.IsRunning || !_sync.IsHost) return;
            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + sendInterval;

            _states.Clear();
            _toRemove.Clear();
            foreach (var pair in _pieces)
            {
                FallingPiece piece = pair.Value;
                if (piece.transform.position.y < removeBelowHeight)
                {
                    _toRemove.Add(pair.Key);
                    continue;
                }
                if (!piece.NeedsSync()) continue;
                _states.Add(new BlockPieceState
                {
                    Id = pair.Key,
                    Position = piece.transform.position,
                    Rotation = piece.transform.rotation,
                });
            }
            _sync.BroadcastPieceStates(_states);
            foreach (uint id in _toRemove) _sync.RemovePiece(id);
        }

        void OnDestroy()
        {
            if (_sync != null)
            {
                _sync.PieceAdded -= OnPieceAdded;
                _sync.PieceRemoved -= OnPieceRemoved;
                _sync.PiecesCleared -= OnPiecesCleared;
                _sync.PieceStatesReceived -= OnPieceStates;
            }
            OnPiecesCleared();
        }

        void TrySubscribe()
        {
            var view = BlockWorldView.Instance;
            if (view == null || view.Sync == null) return;
            _sync = view.Sync;
            _sync.PieceAdded += OnPieceAdded;
            _sync.PieceRemoved += OnPieceRemoved;
            _sync.PiecesCleared += OnPiecesCleared;
            _sync.PieceStatesReceived += OnPieceStates;
        }

        void OnPieceAdded(BlockPiece piece)
        {
            var view = BlockWorldView.Instance;
            if (view == null) return;

            // Make sure the world's own colliders no longer include these blocks before the piece starts to move.
            view.RebuildDirtyNow();

            if (_pieces.TryGetValue(piece.Id, out var existing))
            {
                Destroy(existing.gameObject);
                _pieces.Remove(piece.Id);
            }
            _pieces.Add(piece.Id, CreatePiece(piece, view.BlockMaterial, _sync.IsHost));
        }

        void OnPieceRemoved(uint id)
        {
            if (!_pieces.TryGetValue(id, out var piece)) return;
            if (piece != null) Destroy(piece.gameObject);
            _pieces.Remove(id);
        }

        void OnPiecesCleared()
        {
            foreach (var piece in _pieces.Values)
            {
                if (piece != null) Destroy(piece.gameObject);
            }
            _pieces.Clear();
        }

        void OnPieceStates(List<BlockPieceState> states)
        {
            foreach (var state in states)
            {
                if (_pieces.TryGetValue(state.Id, out var piece) && piece != null)
                    piece.SetTarget(state.Position, state.Rotation);
            }
        }

        FallingPiece CreatePiece(BlockPiece data, Material material, bool simulate)
        {
            float size = BlockConstants.BlockSize;
            var root = new GameObject($"Falling piece {data.Id}");
            root.transform.SetPositionAndRotation(data.Position, data.Rotation);
            var piece = root.AddComponent<FallingPiece>();

            // Mesh it with the same mesher as the world: copy the blocks into a small scratch world.
            var local = new BlockWorld();
            for (int y = 0; y < data.Size.y; y++)
            for (int z = 0; z < data.Size.z; z++)
            for (int x = 0; x < data.Size.x; x++)
            {
                Block block = data.Get(x, y, z);
                if (!block.IsAir) local.SetBlock(new int3(x, y, z), block);
            }
            _dirty.Clear();
            local.TakeDirty(_dirty);
            foreach (int3 coord in _dirty)
            {
                ChunkMesher.Build(local, coord, _buffers, BlockPalette.Rows, data.Origin);
                if (_buffers.IsEmpty) continue;

                var part = new GameObject("Mesh");
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = new Vector3(coord.x, coord.y, coord.z) * BlockConstants.ChunkWorldSize;
                var mesh = new Mesh { name = root.name, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(_buffers.Vertices);
                mesh.SetNormals(_buffers.Normals);
                mesh.SetUVs(0, _buffers.UVs);
                mesh.SetTriangles(_buffers.Indices, 0, true);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                part.AddComponent<MeshRenderer>().sharedMaterial = material;
                piece.Meshes.Add(mesh);
            }

            // Collision: a handful of boxes (each a little smaller than the blocks, so neighbors don't snag).
            const float shrink = 0.006f;
            if (data.BuildBoxes(_boxes, maxColliderBoxes))
            {
                foreach (var (min, max) in _boxes)
                {
                    var box = root.AddComponent<BoxCollider>();
                    Vector3 low = new Vector3(min.x, min.y, min.z) * size;
                    Vector3 high = new Vector3(max.x + 1, max.y + 1, max.z + 1) * size;
                    box.center = (low + high) * 0.5f;
                    box.size = high - low - Vector3.one * shrink;
                }
            }
            else
            {
                var box = root.AddComponent<BoxCollider>();
                Vector3 extent = new Vector3(data.Size.x, data.Size.y, data.Size.z) * size;
                box.center = extent * 0.5f;
                box.size = extent - Vector3.one * shrink;
            }

            var body = root.AddComponent<Rigidbody>();
            body.mass = Mathf.Max(1f, data.Mass());
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = simulate ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.Discrete;
            body.linearDamping = 0.05f;
            body.angularDamping = 0.2f;
            body.isKinematic = !simulate; // only the host simulates; everyone else follows its positions

            piece.Initialize(data.Id, body, simulate);
            return piece;
        }
    }
}
