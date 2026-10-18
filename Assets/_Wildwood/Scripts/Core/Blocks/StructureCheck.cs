using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// Finds groups of blocks that are no longer held up by anything, so they can break off and fall.
    ///
    /// A block counts as resting on the ground if the terrain surface reaches its bottom anywhere under it.
    /// Blocks hold each other up only through real face contact: two faces that share some area. Two slopes that
    /// meet only along an edge, or corner to corner, don't support each other.
    ///
    /// This is the first layer of the structure system (connected or not). Weight and strength come later.
    /// </summary>
    public sealed class StructureCheck
    {
        /// <summary>Groups bigger than this are assumed to be supported (keeps a single edit from stalling the game).</summary>
        public const int MaxPieceBlocks = 40_000;

        readonly Func<float, float, float> _groundHeight;
        readonly Dictionary<int2, float> _heightCache = new Dictionary<int2, float>();
        readonly HashSet<int3> _anchored = new HashSet<int3>();
        readonly HashSet<int3> _floating = new HashSet<int3>();
        readonly HashSet<int3> _component = new HashSet<int3>();
        readonly Queue<int3> _queue = new Queue<int3>();

        static readonly bool[] FaceContactTable = BuildContactTable();

        /// <param name="groundHeight">Terrain height in meters at (x, z). Comes from the terrain generator.</param>
        public StructureCheck(Func<float, float, float> groundHeight)
        {
            _groundHeight = groundHeight;
        }

        /// <summary>
        /// Starting from <paramref name="seeds"/> (blocks near something that just changed), adds every group of
        /// connected blocks that no longer reaches the ground to <paramref name="floatingGroups"/>.
        /// </summary>
        public void FindFloating(BlockWorld world, IEnumerable<int3> seeds, List<List<int3>> floatingGroups)
        {
            _anchored.Clear();
            _floating.Clear();
            _heightCache.Clear();

            foreach (int3 seed in seeds)
            {
                if (_anchored.Contains(seed) || _floating.Contains(seed)) continue;
                if (world.GetBlock(seed).IsAir) continue;

                bool grounded = Explore(world, seed);
                if (grounded)
                {
                    foreach (int3 p in _component) _anchored.Add(p);
                }
                else
                {
                    var group = new List<int3>(_component);
                    group.Sort(ComparePositions); // stable order, so every machine builds the piece the same way
                    foreach (int3 p in group) _floating.Add(p);
                    floatingGroups.Add(group);
                }
            }
        }

        /// <summary>Flood-fills from <paramref name="start"/>. Returns true as soon as the group reaches the ground.</summary>
        bool Explore(BlockWorld world, int3 start)
        {
            _component.Clear();
            _queue.Clear();
            _component.Add(start);
            _queue.Enqueue(start);

            while (_queue.Count > 0)
            {
                int3 p = _queue.Dequeue();
                Block block = world.GetBlock(p);
                if (IsGrounded(p)) return true;
                if (_component.Count > MaxPieceBlocks) return true; // too big to drop; treat as held up

                CornerShape shape = CornerShapes.Get(block.Corners);
                for (int face = 0; face < BlockFaces.Count; face++)
                {
                    int3 q = p + BlockFaces.Directions[face];
                    if (_component.Contains(q)) continue;
                    if (_anchored.Contains(q))
                    {
                        if (Touches(shape, face, world.GetBlock(q))) return true;
                        continue;
                    }
                    Block neighbor = world.GetBlock(q);
                    if (neighbor.IsAir || !Touches(shape, face, neighbor)) continue;
                    _component.Add(q);
                    _queue.Enqueue(q);
                }
            }
            return false;
        }

        static bool Touches(CornerShape shape, int face, Block neighbor)
        {
            if (neighbor.IsAir) return false;
            byte mine = shape.FaceCoverage[face];
            byte theirs = CornerShapes.Get(neighbor.Corners).FaceCoverage[BlockFaces.Opposite(face)];
            return FaceContactTable[mine * 16 + theirs];
        }

        /// <summary>
        /// How far above the ground a block's bottom may be and still rest on it, in meters. The terrain you see
        /// is a smoothed mesh that can sit a few centimeters off the exact height, so this has some slack
        /// (but stays well under one block, so a real gap still counts as a gap).
        /// </summary>
        public const float GroundTolerance = 0.06f;

        /// <summary>True if the terrain surface reaches the bottom of this block anywhere under it.</summary>
        public bool IsGrounded(int3 block)
        {
            float size = BlockConstants.BlockSize;
            float bottom = block.y * size;
            // Check the block's 4 bottom corners; the ground is smooth enough at 10 cm that this is plenty.
            for (int i = 0; i < 4; i++)
            {
                var corner = new int2(block.x + (i & 1), block.z + (i >> 1));
                if (GroundAt(corner) >= bottom - GroundTolerance) return true;
            }
            return false;
        }

        float GroundAt(int2 vertex)
        {
            if (_heightCache.TryGetValue(vertex, out float height)) return height;
            float size = BlockConstants.BlockSize;
            height = _groundHeight(vertex.x * size, vertex.y * size);
            _heightCache[vertex] = height;
            return height;
        }

        /// <summary>
        /// Whether two face coverages (4-bit corner masks on the same square) share real area.
        /// They do unless one is empty, or they're the two opposite half-triangles that only meet along the diagonal.
        /// </summary>
        public static bool FacesTouch(byte a, byte b) => FaceContactTable[a * 16 + b];

        static bool[] BuildContactTable()
        {
            var table = new bool[256];
            for (int a = 0; a < 16; a++)
            for (int b = 0; b < 16; b++)
            {
                bool touch;
                if (a == 0 || b == 0) touch = false;
                else if (a == 15 || b == 15) touch = true;
                else
                {
                    int missingA = 15 & ~a;
                    int missingB = 15 & ~b;
                    int both = missingA | missingB;
                    touch = missingA == missingB || (both != 0b1001 && both != 0b0110);
                }
                table[a * 16 + b] = touch;
            }
            return table;
        }

        static int ComparePositions(int3 a, int3 b)
        {
            if (a.y != b.y) return a.y.CompareTo(b.y);
            if (a.z != b.z) return a.z.CompareTo(b.z);
            return a.x.CompareTo(b.x);
        }
    }
}
