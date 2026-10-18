using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// A few test builds the host places when a session starts, to show off corner shapes:
    /// a pyramid (slopes and corner pieces), a walkable 45 degree ramp, a low-poly boulder,
    /// and a brick wall with a pointed stone cap. All in block units (10 blocks = 1 m).
    /// Players spawn around (0, 0, -60) facing +Z, so everything here is in front of them.
    /// </summary>
    public static class DemoStructures
    {
        public static void Build(BlockWorld world)
        {
            Pyramid(world, new int3(-40, 0, 90), 12, BlockMaterials.Sandstone);
            Ramp(world, new int3(20, 0, 70), width: 10, rise: 12, BlockMaterials.Wood);
            Boulder(world, new int3(0, 5, 130), radius: 9, BlockMaterials.Stone);
            CappedWall(world, new int3(-15, 0, 170), length: 30, height: 10, BlockMaterials.Brick, BlockMaterials.Stone);
        }

        /// <summary>Square pyramid with 45 degree sides. <paramref name="center"/> is the middle of its base.</summary>
        static void Pyramid(BlockWorld world, int3 center, int halfSize, ushort material)
        {
            var min = new int3(center.x - halfSize, center.y, center.z - halfSize);
            var max = new int3(center.x + halfSize - 1, center.y + halfSize - 1, center.z + halfSize - 1);
            ShapeStamp.FromCornerField(world, min, max, material, v =>
            {
                int height = halfSize - math.max(math.abs(v.x - center.x), math.abs(v.z - center.z));
                return v.y - center.y <= height;
            });
        }

        /// <summary>Ramp rising toward +Z, one block of height per block of length, then a short flat top.</summary>
        static void Ramp(BlockWorld world, int3 start, int width, int rise, ushort material)
        {
            int flatTop = 10;
            var min = start;
            var max = new int3(start.x + width - 1, start.y + rise - 1, start.z + rise + flatTop - 1);
            ShapeStamp.FromCornerField(world, min, max, material, v =>
            {
                int height = math.min(v.z - start.z, rise);
                return v.y - start.y <= height;
            });
        }

        /// <summary>Rough faceted rock. Anything below the ground is cut off flat.</summary>
        static void Boulder(BlockWorld world, int3 center, int radius, ushort material)
        {
            var min = new int3(center.x - radius - 1, 0, center.z - radius - 1);
            var max = new int3(center.x + radius, center.y + radius, center.z + radius);
            float radiusSquared = radius * radius;
            ShapeStamp.FromCornerField(world, min, max, material, v =>
            {
                if (v.y < 0) return false;
                float3 offset = new float3(v - center) * new float3(1f, 1.25f, 1f); // a bit squashed
                float bumps = 1f + 0.35f * (ShapeStamp.Noise01(v) - 0.5f);
                return math.lengthsq(offset) <= radiusSquared * bumps;
            });
        }

        /// <summary>A 3-block-thick wall along X with a pointed cap (slopes on both sides).</summary>
        static void CappedWall(BlockWorld world, int3 start, int length, int height, ushort wallMaterial, ushort capMaterial)
        {
            var wallMax = new int3(start.x + length - 1, start.y + height - 1, start.z + 2);
            BlockEditorFill(world, start, wallMax, wallMaterial);

            var capMin = new int3(start.x, start.y + height, start.z);
            var capMax = new int3(start.x + length - 1, start.y + height, start.z + 2);
            ShapeStamp.FromCornerField(world, capMin, capMax, capMaterial, v =>
            {
                // Vertices across the wall's thickness are at z offsets 0..3; the middle two are the peak.
                int across = v.z - start.z;
                int peak = math.min(across, 3 - across);
                return v.y - (start.y + height) <= math.min(peak, 1);
            });
        }

        static void BlockEditorFill(BlockWorld world, int3 min, int3 max, ushort material)
        {
            var block = Block.Solid(material);
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            for (int x = min.x; x <= max.x; x++)
                world.SetBlock(new int3(x, y, z), block);
        }
    }
}
