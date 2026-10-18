using System.Collections.Generic;
using Unity.Mathematics;

namespace Wildwood.Core.Terrain
{
    /// <summary>Sizes for the smooth terrain (separate from the 10 cm block grid).</summary>
    public static class TerrainSettings
    {
        /// <summary>Spacing of terrain samples, in meters. Digging will work at this resolution.</summary>
        public const float CellSize = 0.5f;

        /// <summary>Cells along one edge of a terrain chunk.</summary>
        public const int ChunkCells = 32;

        /// <summary>Edge length of a terrain chunk in meters (16 m).</summary>
        public const float ChunkWorldSize = CellSize * ChunkCells;

        /// <summary>Vertical chunk layers that can contain ground (y from -32 m to +32 m).</summary>
        public const int MinChunkY = -2;
        public const int MaxChunkY = 1;

        /// <summary>Flat area around the spawn point (meters), where the test builds sit at height 0.</summary>
        public const float SpawnClearingRadius = 22f;

        /// <summary>Distance over which the clearing blends into the surrounding hills.</summary>
        public const float SpawnBlendDistance = 35f;
    }

    public enum TreeKind : byte
    {
        Broadleaf = 0,
        Pine = 1,
    }

    /// <summary>Where and how to grow one tree. Fully determined by the world seed.</summary>
    public struct TreeSpawn
    {
        public float3 Position;
        public float Height;
        public float TrunkRadius;
        public float Yaw;
        public TreeKind Kind;
        public uint Seed;
    }

    /// <summary>
    /// Turns a world seed into ground height, forest coverage and tree positions. Every machine runs it
    /// with the same seed and gets the same world, so only the seed (plus player edits) is ever sent or saved.
    /// Plains are low rolling hills of golden grass; forests are a little hillier and full of trees.
    /// </summary>
    public sealed class TerrainGenerator
    {
        /// <summary>Tree candidates are placed one per cell of this size (meters), then thinned by biome.</summary>
        public const float TreeCellSize = 4f;

        readonly float2 _heightOffset;
        readonly float2 _hillOffset;
        readonly float2 _detailOffset;
        readonly float2 _forestOffset;
        readonly uint _seedHash;

        public TerrainGenerator(int seed)
        {
            Seed = seed;
            _seedHash = Hash((uint)seed, 0x9E3779B9u);
            // Offsets keep each seed in a different part of the noise. Kept under ~5000 for float precision.
            _heightOffset = RandomOffset(1);
            _hillOffset = RandomOffset(2);
            _detailOffset = RandomOffset(3);
            _forestOffset = RandomOffset(4);
        }

        public int Seed { get; }

        /// <summary>Ground height in meters at a world position.</summary>
        public float Height(float x, float z)
        {
            var p = new float2(x, z);
            float forest = Forest(x, z);

            float broad = Fbm(p * 0.0016f + _heightOffset, 3);      // big, slow swells
            float hills = Fbm(p * 0.0065f + _hillOffset, 4);        // rolling hills
            float detail = noise.snoise(p * 0.035f + _detailOffset); // small bumps

            float height = 3.5f * broad + hills * math.lerp(1.8f, 5.0f, forest) + detail * 0.25f;

            // Flatten the spawn clearing so the test builds and the step test sit on level ground at 0.
            float distance = math.length(p);
            float blend = math.smoothstep(TerrainSettings.SpawnClearingRadius,
                TerrainSettings.SpawnClearingRadius + TerrainSettings.SpawnBlendDistance, distance);
            return math.lerp(0f, height, blend);
        }

        /// <summary>How forested a spot is, from 0 (open plains) to 1 (deep forest).</summary>
        public float Forest(float x, float z)
        {
            var p = new float2(x, z);
            float value = Fbm(p * 0.0045f + _forestOffset, 3);

            // Near spawn, guarantee a forest edge to the east (+X) and open plains to the west,
            // so both are visible from the start in every world.
            float nearSpawn = 1f - math.smoothstep(150f, 220f, math.length(p));
            value += nearSpawn * (0.9f * math.smoothstep(25f, 70f, x) - 0.5f * math.smoothstep(0f, 60f, -x));

            return math.saturate(math.smoothstep(0.0f, 0.3f, value));
        }

        /// <summary>Gradient-based steepness: 0 = flat, 1 = vertical.</summary>
        public float Steepness(float x, float z)
        {
            const float e = 0.5f;
            float dx = (Height(x + e, z) - Height(x - e, z)) / (2f * e);
            float dz = (Height(x, z + e) - Height(x, z - e)) / (2f * e);
            float3 normal = math.normalize(new float3(-dx, 1f, -dz));
            return 1f - normal.y;
        }

        /// <summary>
        /// Adds the trees whose trunks stand inside the square [min, min + size) (meters, x/z) to <paramref name="output"/>.
        /// </summary>
        public void GetTrees(float2 min, float size, List<TreeSpawn> output)
        {
            int startX = (int)math.floor(min.x / TreeCellSize);
            int startZ = (int)math.floor(min.y / TreeCellSize);
            int endX = (int)math.floor((min.x + size) / TreeCellSize);
            int endZ = (int)math.floor((min.y + size) / TreeCellSize);

            for (int cz = startZ; cz < endZ; cz++)
            for (int cx = startX; cx < endX; cx++)
            {
                uint h = Hash(Hash(_seedHash, (uint)cx), (uint)cz);
                float x = (cx + 0.15f + 0.7f * Unit(h, 1)) * TreeCellSize;
                float z = (cz + 0.15f + 0.7f * Unit(h, 2)) * TreeCellSize;

                float distance = math.length(new float2(x, z));
                if (distance < TerrainSettings.SpawnClearingRadius + 6f) continue;

                float forest = Forest(x, z);
                float chance = math.lerp(0.015f, 0.62f, forest);
                if (Unit(h, 3) > chance) continue;
                if (Steepness(x, z) > 0.35f) continue;

                bool pine = Unit(h, 4) < math.lerp(0.15f, 0.4f, forest);
                float sizeRoll = Unit(h, 5);
                output.Add(new TreeSpawn
                {
                    Position = new float3(x, Height(x, z) - 0.15f, z),
                    Kind = pine ? TreeKind.Pine : TreeKind.Broadleaf,
                    Height = pine ? math.lerp(7f, 11f, sizeRoll) : math.lerp(4.5f, 7.5f, sizeRoll),
                    TrunkRadius = math.lerp(0.18f, 0.32f, sizeRoll),
                    Yaw = Unit(h, 6) * 360f,
                    Seed = Hash(h, 7),
                });
            }
        }

        // ---------- Noise helpers ----------

        static float Fbm(float2 p, int octaves)
        {
            float sum = 0f, amplitude = 0.5f, total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += noise.snoise(p) * amplitude;
                total += amplitude;
                p = p * 2.03f + new float2(17.1f, 9.7f);
                amplitude *= 0.5f;
            }
            return sum / total;
        }

        float2 RandomOffset(uint salt)
        {
            uint h = Hash(_seedHash, salt);
            return new float2(Unit(h, 11), Unit(h, 12)) * 4000f - 2000f;
        }

        /// <summary>Stable integer hash (same result on every machine).</summary>
        public static uint Hash(uint a, uint b)
        {
            unchecked
            {
                uint h = a * 0x85EBCA6Bu ^ (b + 0x9E3779B9u + (a << 6) + (a >> 2));
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>A number from 0 to 1 derived from a hash and a salt.</summary>
        public static float Unit(uint hash, uint salt) => (Hash(hash, salt) & 0xFFFFFF) / 16777216f;
    }
}
