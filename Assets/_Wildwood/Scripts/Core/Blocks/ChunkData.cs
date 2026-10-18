using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// The blocks of one 32x32x32 chunk. Storage is only allocated once the chunk holds something,
    /// so empty space costs nothing. Edit through <see cref="BlockWorld"/>, which keeps rendering in sync.
    /// </summary>
    public sealed class ChunkData
    {
        const int Size = BlockConstants.ChunkSize;

        public readonly int3 Coord;

        Block[] _blocks; // null while the chunk is all air

        public ChunkData(int3 coord)
        {
            Coord = coord;
        }

        /// <summary>Number of non-air blocks.</summary>
        public int SolidCount { get; private set; }

        public bool IsEmpty => SolidCount == 0;

        /// <summary>The raw array (x fastest, then z, then y), or null when empty. Read only.</summary>
        public Block[] RawBlocks => _blocks;

        public static int Index(int x, int y, int z) => x + Size * (z + Size * y);

        public Block Get(int x, int y, int z) => _blocks == null ? default : _blocks[Index(x, y, z)];

        /// <summary>Writes one block. The block must already be sanitized. Returns true if anything changed.</summary>
        internal bool Set(int x, int y, int z, Block block)
        {
            Block old = Get(x, y, z);
            if (old == block) return false;

            if (_blocks == null)
            {
                if (block.IsAir) return false;
                _blocks = new Block[BlockConstants.ChunkVolume];
            }
            _blocks[Index(x, y, z)] = block;

            if (old.IsAir && !block.IsAir) SolidCount++;
            else if (!old.IsAir && block.IsAir) SolidCount--;

            if (SolidCount == 0) _blocks = null;
            return true;
        }

        /// <summary>Takes ownership of a full, sanitized block array (used when loading snapshots).</summary>
        internal void Load(Block[] blocks)
        {
            int solid = 0;
            for (int i = 0; i < blocks.Length; i++)
                if (!blocks[i].IsAir) solid++;
            SolidCount = solid;
            _blocks = solid > 0 ? blocks : null;
        }
    }
}
