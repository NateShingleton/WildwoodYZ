using System;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// Builds shaped blocks from a rule that says whether a point is inside a solid. The rule is checked at
    /// every block corner, and each block keeps the corners that are inside. Slopes, pyramids and rounded
    /// low-poly rocks all fall out of this automatically. Terrain features and rocks will use the same tool.
    /// </summary>
    public static class ShapeStamp
    {
        /// <summary>
        /// Fills the blocks in [min, max] (inclusive). <paramref name="isInside"/> receives a grid vertex
        /// (a block corner, in block units) and returns true if that point is inside the shape.
        /// Returns how many blocks changed.
        /// </summary>
        public static int FromCornerField(BlockWorld world, int3 min, int3 max, ushort material, Func<int3, bool> isInside)
        {
            int changed = 0;
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            for (int x = min.x; x <= max.x; x++)
            {
                var block = new int3(x, y, z);
                byte mask = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    var vertex = block + new int3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
                    if (isInside(vertex)) mask |= (byte)(1 << corner);
                }
                if (mask == 0) continue;
                if (world.SetBlock(block, new Block { Material = material, Corners = mask })) changed++;
            }
            return changed;
        }

        /// <summary>A number from 0 to 1 that's stable for a given grid vertex (for rough, natural shapes).</summary>
        public static float Noise01(int3 vertex)
        {
            unchecked
            {
                uint h = (uint)vertex.x * 374761393u + (uint)vertex.y * 668265263u + (uint)vertex.z * 2147483647u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535f;
            }
        }
    }
}
