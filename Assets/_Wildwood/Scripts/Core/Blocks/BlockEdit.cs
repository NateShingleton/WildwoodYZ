using System;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    public enum BlockEditKind : byte
    {
        /// <summary>Write one block value into every cell of a box (Air removes).</summary>
        Fill = 1,

        /// <summary>Delete corners from one block.</summary>
        Chisel = 2,
    }

    public enum FillMode : byte
    {
        /// <summary>Overwrite whatever is there.</summary>
        Replace = 0,

        /// <summary>Only fill empty cells (used for placing, so you never overwrite existing blocks).</summary>
        OnlyAir = 1,
    }

    /// <summary>
    /// One world edit, small enough to send over the network as-is.
    /// Players send these to the host; the host applies them and forwards them to everyone.
    /// Applying an edit is deterministic (integers only), so every machine ends up with identical blocks.
    /// </summary>
    [Serializable]
    public struct BlockEdit
    {
        public BlockEditKind Kind;
        public FillMode Mode;

        /// <summary>Box corner (inclusive). For Chisel, the block being chiseled.</summary>
        public int3 Min;

        /// <summary>Opposite box corner (inclusive). Unused for Chisel.</summary>
        public int3 Max;

        /// <summary>For Fill: the block written into each cell.</summary>
        public Block Block;

        /// <summary>For Chisel: which corners to delete.</summary>
        public byte Corners;

        public static BlockEdit FillBox(int3 min, int3 max, Block block, FillMode mode) =>
            new BlockEdit { Kind = BlockEditKind.Fill, Mode = mode, Min = math.min(min, max), Max = math.max(min, max), Block = block };

        public static BlockEdit RemoveBox(int3 min, int3 max) => FillBox(min, max, Block.Air, FillMode.Replace);

        public static BlockEdit Chisel(int3 position, byte cornersToDelete) =>
            new BlockEdit { Kind = BlockEditKind.Chisel, Min = position, Max = position, Corners = cornersToDelete };
    }

    /// <summary>Validates and applies <see cref="BlockEdit"/>s.</summary>
    public static class BlockEditor
    {
        /// <summary>Largest box one edit may touch, per side (16 blocks = 1.6 m).</summary>
        public const int MaxEditSize = 16;

        /// <summary>Edits must stay within this many blocks of the origin (10 km).</summary>
        public const int WorldLimit = 100_000;

        public static bool IsValid(in BlockEdit edit)
        {
            if (!InWorld(edit.Min) || !InWorld(edit.Max)) return false;
            switch (edit.Kind)
            {
                case BlockEditKind.Fill:
                    int3 size = edit.Max - edit.Min + 1;
                    if (math.any(size < 1) || math.any(size > MaxEditSize)) return false;
                    if (edit.Mode != FillMode.Replace && edit.Mode != FillMode.OnlyAir) return false;
                    return edit.Block.Material == BlockMaterials.Air || BlockMaterials.IsValid(edit.Block.Material);
                case BlockEditKind.Chisel:
                    return edit.Corners != 0;
                default:
                    return false;
            }
        }

        /// <summary>Applies an edit. Returns how many blocks changed (0 if invalid or nothing to do).</summary>
        public static int Apply(BlockWorld world, in BlockEdit edit)
        {
            if (!IsValid(edit)) return 0;

            if (edit.Kind == BlockEditKind.Chisel)
            {
                Block block = world.GetBlock(edit.Min);
                if (block.IsAir) return 0;
                return world.SetBlock(edit.Min, block.WithCorners((byte)(block.Corners & ~edit.Corners))) ? 1 : 0;
            }

            int changed = 0;
            for (int y = edit.Min.y; y <= edit.Max.y; y++)
            for (int z = edit.Min.z; z <= edit.Max.z; z++)
            for (int x = edit.Min.x; x <= edit.Max.x; x++)
            {
                var position = new int3(x, y, z);
                if (edit.Mode == FillMode.OnlyAir && !world.GetBlock(position).IsAir) continue;
                if (world.SetBlock(position, edit.Block)) changed++;
            }
            return changed;
        }

        static bool InWorld(int3 p) => math.all(p > -WorldLimit) && math.all(p < WorldLimit);
    }
}
