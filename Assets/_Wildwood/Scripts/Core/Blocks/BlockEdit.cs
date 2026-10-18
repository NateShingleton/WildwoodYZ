using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    public enum BlockEditKind : byte
    {
        /// <summary>Write one block value into every cell of a box (Air removes).</summary>
        Fill = 1,

        /// <summary>
        /// Delete corners from a cube-shaped region (1 block, or a whole brush-sized group).
        /// The region is treated as one big block: deleting its corner reshapes every small block inside.
        /// </summary>
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

        /// <summary>Box corner (inclusive).</summary>
        public int3 Min;

        /// <summary>Opposite box corner (inclusive). For Chisel the box must be a cube.</summary>
        public int3 Max;

        /// <summary>For Fill: the block written into each cell.</summary>
        public Block Block;

        /// <summary>For Chisel: which corners of the region to delete (same bit layout as a block's corners).</summary>
        public byte Corners;

        public static BlockEdit FillBox(int3 min, int3 max, Block block, FillMode mode) =>
            new BlockEdit { Kind = BlockEditKind.Fill, Mode = mode, Min = math.min(min, max), Max = math.max(min, max), Block = block };

        public static BlockEdit RemoveBox(int3 min, int3 max) => FillBox(min, max, Block.Air, FillMode.Replace);

        /// <summary>Chisel a cube region <paramref name="size"/> blocks wide (1 = a single block).</summary>
        public static BlockEdit Chisel(int3 min, int size, byte cornersToDelete) =>
            new BlockEdit { Kind = BlockEditKind.Chisel, Min = min, Max = min + (size - 1), Corners = cornersToDelete };
    }

    /// <summary>One block that an edit changed: where, what it was, what it became. Used for undo.</summary>
    public readonly struct BlockChange
    {
        public readonly int3 Position;
        public readonly Block Before;
        public readonly Block After;

        public BlockChange(int3 position, Block before, Block after)
        {
            Position = position;
            Before = before;
            After = after;
        }
    }

    /// <summary>Validates and applies <see cref="BlockEdit"/>s.</summary>
    public static class BlockEditor
    {
        /// <summary>Longest side of a fill box (128 blocks = 12.8 m), so drag-built walls and floors can be big.</summary>
        public const int MaxEditSize = 128;

        /// <summary>Most blocks one fill may touch (64 x 64 x 64).</summary>
        public const int MaxEditVolume = 64 * 64 * 64;

        /// <summary>Largest chisel region per side (the biggest brush is 8).</summary>
        public const int MaxChiselSize = 16;

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
                    if ((long)size.x * size.y * size.z > MaxEditVolume) return false;
                    if (edit.Mode != FillMode.Replace && edit.Mode != FillMode.OnlyAir) return false;
                    return edit.Block.Material == BlockMaterials.Air || BlockMaterials.IsValid(edit.Block.Material);
                case BlockEditKind.Chisel:
                    int3 extent = edit.Max - edit.Min + 1;
                    return edit.Corners != 0 && extent.x >= 1 && extent.x <= MaxChiselSize &&
                           extent.x == extent.y && extent.x == extent.z;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Applies an edit. Returns how many blocks changed (0 if invalid or nothing to do).
        /// If <paramref name="record"/> is given, every changed block is added to it (for undo).
        /// </summary>
        public static int Apply(BlockWorld world, in BlockEdit edit, List<BlockChange> record = null)
        {
            if (!IsValid(edit)) return 0;

            if (edit.Kind == BlockEditKind.Chisel) return ApplyChisel(world, edit, record);

            int changed = 0;
            for (int y = edit.Min.y; y <= edit.Max.y; y++)
            for (int z = edit.Min.z; z <= edit.Max.z; z++)
            for (int x = edit.Min.x; x <= edit.Max.x; x++)
            {
                var position = new int3(x, y, z);
                Block before = world.GetBlock(position);
                if (edit.Mode == FillMode.OnlyAir && !before.IsAir) continue;
                if (Set(world, position, before, edit.Block, record)) changed++;
            }
            return changed;
        }

        static bool Set(BlockWorld world, int3 position, Block before, Block block, List<BlockChange> record)
        {
            if (!world.SetBlock(position, block)) return false;
            record?.Add(new BlockChange(position, before, world.GetBlock(position)));
            return true;
        }

        /// <summary>
        /// Which corners a cube region still has, judged from the blocks sitting in its 8 corners:
        /// the region's corner is there if that corner block still has its matching outer corner.
        /// </summary>
        public static byte RegionCorners(BlockWorld world, int3 min, int size)
        {
            byte corners = 0;
            for (int corner = 0; corner < 8; corner++)
            {
                var offset = new int3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
                Block block = world.GetBlock(min + offset * (size - 1));
                if (!block.IsAir && (block.Corners & (1 << corner)) != 0) corners |= (byte)(1 << corner);
            }
            return corners;
        }

        static int ApplyChisel(BlockWorld world, in BlockEdit edit, List<BlockChange> record)
        {
            int size = edit.Max.x - edit.Min.x + 1;
            byte before = RegionCorners(world, edit.Min, size);
            byte after = (byte)(before & ~edit.Corners);
            if (after == before) return 0; // those corners are already gone

            // The region's new shape. Each small block keeps only the corners that are inside it,
            // which is exact because the shape's planes always pass through grid points.
            CornerShape shape = CornerShapes.Get(after);
            int changed = 0;
            for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                var local = new int3(x, y, z);
                int3 position = edit.Min + local;
                Block block = world.GetBlock(position);
                if (block.IsAir) continue;

                byte keep = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    var vertex = local + new int3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
                    if (shape.ContainsScaled(vertex, size)) keep |= (byte)(1 << corner);
                }
                if (Set(world, position, block, block.WithCorners((byte)(block.Corners & keep)), record)) changed++;
            }
            return changed;
        }

        static bool InWorld(int3 p) => math.all(p > -WorldLimit) && math.all(p < WorldLimit);
    }
}
