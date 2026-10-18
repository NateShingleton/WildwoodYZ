using System;
using System.Runtime.CompilerServices;

namespace Wildwood.Core.Blocks
{
    /// <summary>World-scale constants for the block grid.</summary>
    public static class BlockConstants
    {
        /// <summary>Edge length of one block, in meters.</summary>
        public const float BlockSize = 0.1f;

        /// <summary>Blocks along one edge of a chunk. A chunk holds ChunkSize^3 blocks.</summary>
        public const int ChunkSize = 32;

        /// <summary>Total blocks in one chunk.</summary>
        public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

        /// <summary>Edge length of one chunk, in meters (3.2 m).</summary>
        public const float ChunkWorldSize = BlockSize * ChunkSize;
    }

    /// <summary>
    /// Helpers for a block's 8-bit corner mask.
    /// Corner (x, y, z), where each axis is 0 or 1, is stored in bit x + 2y + 4z.
    /// 0xFF is a full cube. Deleting corners makes cut corners, slopes and spikes:
    /// the block's shape is whatever you get by connecting the corners that remain.
    /// </summary>
    public static class CornerBits
    {
        public const byte None = 0;
        public const byte All = 0xFF;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Index(int x, int y, int z) => x | (y << 1) | (z << 2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Bit(int x, int y, int z) => (byte)(1 << Index(x, y, z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Has(byte mask, int x, int y, int z) => (mask & Bit(x, y, z)) != 0;

        /// <summary>Number of corners present in the mask (0 to 8).</summary>
        public static int Count(byte mask)
        {
            int count = 0;
            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }
            return count;
        }
    }

    /// <summary>
    /// One 10 cm block. Kept at 4 bytes so a 32x32x32 chunk is 128 KB before compression,
    /// and most chunks compress to almost nothing (they're mostly air or one material).
    /// This struct is also the save format and the network format, so change it with care.
    /// </summary>
    [Serializable]
    public struct Block : IEquatable<Block>
    {
        /// <summary>Material id. 0 is air.</summary>
        public ushort Material;

        /// <summary>Which of the 8 cube corners exist. See <see cref="CornerBits"/>.</summary>
        public byte Corners;

        /// <summary>Reserved for later (half-step corners, damage). Always 0 for now.</summary>
        public byte Reserved;

        public static readonly Block Air = default;

        /// <summary>A full cube of the given material.</summary>
        public static Block Solid(ushort material) => new Block { Material = material, Corners = CornerBits.All };

        /// <summary>True when there's nothing here: no material, or every corner deleted.</summary>
        public bool IsAir => Material == 0 || Corners == CornerBits.None;

        /// <summary>True for a complete, uncut cube.</summary>
        public bool IsFullCube => Material != 0 && Corners == CornerBits.All;

        public Block WithCorners(byte corners) =>
            new Block { Material = Material, Corners = corners, Reserved = Reserved };

        public Block WithoutCorner(int x, int y, int z) =>
            WithCorners((byte)(Corners & ~CornerBits.Bit(x, y, z)));

        public bool Equals(Block other) =>
            Material == other.Material && Corners == other.Corners && Reserved == other.Reserved;

        public override bool Equals(object obj) => obj is Block other && Equals(other);

        public override int GetHashCode() => Material | (Corners << 16) | (Reserved << 24);

        public static bool operator ==(Block a, Block b) => a.Equals(b);

        public static bool operator !=(Block a, Block b) => !a.Equals(b);

        public override string ToString() =>
            IsAir ? "Air" : $"Block(material {Material}, corners 0x{Corners:X2})";
    }
}
