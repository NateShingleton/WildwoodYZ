using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// All the blocks in the world, stored in chunks. Positions are integer block coordinates:
    /// block (x, y, z) fills the world-space box from (x, y, z) * 0.1 m to (x + 1, y + 1, z + 1) * 0.1 m.
    /// Pure data with no Unity objects, so the host, clients, tests and (later) a dedicated server share it.
    /// </summary>
    public sealed class BlockWorld
    {
        const int Size = BlockConstants.ChunkSize;
        const int Shift = 5; // log2(ChunkSize)
        const int LocalMask = Size - 1;

        readonly Dictionary<int3, ChunkData> _chunks = new Dictionary<int3, ChunkData>();
        readonly HashSet<int3> _dirty = new HashSet<int3>();

        /// <summary>Raised after <see cref="Clear"/>, so views can drop everything they built.</summary>
        public event Action Cleared;

        public int ChunkCount => _chunks.Count;

        public IEnumerable<ChunkData> Chunks => _chunks.Values;

        public static int3 ChunkOf(int3 block) => new int3(block.x >> Shift, block.y >> Shift, block.z >> Shift);

        public static int3 LocalOf(int3 block) => new int3(block.x & LocalMask, block.y & LocalMask, block.z & LocalMask);

        public bool TryGetChunk(int3 coord, out ChunkData chunk) => _chunks.TryGetValue(coord, out chunk);

        public Block GetBlock(int3 position)
        {
            if (!_chunks.TryGetValue(ChunkOf(position), out var chunk)) return Block.Air;
            int3 local = LocalOf(position);
            return chunk.Get(local.x, local.y, local.z);
        }

        /// <summary>Sets one block. Returns true if it changed. Unknown materials and flat shapes become air.</summary>
        public bool SetBlock(int3 position, Block block)
        {
            block = Sanitize(block);
            int3 coord = ChunkOf(position);
            if (!_chunks.TryGetValue(coord, out var chunk))
            {
                if (block.IsAir) return false;
                chunk = new ChunkData(coord);
                _chunks.Add(coord, chunk);
            }

            int3 local = LocalOf(position);
            if (!chunk.Set(local.x, local.y, local.z, block)) return false;
            if (chunk.IsEmpty) _chunks.Remove(coord);

            _dirty.Add(coord);
            // A block on a chunk border can change which faces the neighbor chunk needs to draw.
            if (local.x == 0) _dirty.Add(coord + new int3(-1, 0, 0));
            if (local.x == LocalMask) _dirty.Add(coord + new int3(1, 0, 0));
            if (local.y == 0) _dirty.Add(coord + new int3(0, -1, 0));
            if (local.y == LocalMask) _dirty.Add(coord + new int3(0, 1, 0));
            if (local.z == 0) _dirty.Add(coord + new int3(0, 0, -1));
            if (local.z == LocalMask) _dirty.Add(coord + new int3(0, 0, 1));
            return true;
        }

        /// <summary>Replaces a whole chunk with received data. The array is sanitized and then owned by the world.</summary>
        public void LoadChunk(int3 coord, Block[] blocks)
        {
            if (blocks == null || blocks.Length != BlockConstants.ChunkVolume)
                throw new ArgumentException("A chunk needs exactly ChunkVolume blocks.", nameof(blocks));

            for (int i = 0; i < blocks.Length; i++) blocks[i] = Sanitize(blocks[i]);

            var chunk = new ChunkData(coord);
            chunk.Load(blocks);
            if (chunk.IsEmpty) _chunks.Remove(coord);
            else _chunks[coord] = chunk;

            _dirty.Add(coord);
            for (int face = 0; face < BlockFaces.Count; face++) _dirty.Add(coord + BlockFaces.Directions[face]);
        }

        /// <summary>Moves the set of chunks that need re-meshing into <paramref name="into"/>.</summary>
        public void TakeDirty(List<int3> into)
        {
            into.AddRange(_dirty);
            _dirty.Clear();
        }

        public void Clear()
        {
            _chunks.Clear();
            _dirty.Clear();
            Cleared?.Invoke();
        }

        public static Block Sanitize(Block block)
        {
            if (!BlockMaterials.IsValid(block.Material) || !CornerShapes.IsSolid(block.Corners)) return Block.Air;
            return block;
        }
    }
}
