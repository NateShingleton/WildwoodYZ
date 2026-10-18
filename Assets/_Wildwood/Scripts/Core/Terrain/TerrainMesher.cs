using Unity.Mathematics;
using UnityEngine;
using Wildwood.Core.Meshing;

namespace Wildwood.Core.Terrain
{
    /// <summary>
    /// Builds the smooth ground mesh for one 16 m terrain chunk using "surface nets":
    /// the ground is a 3D density field (positive = solid), sampled every 0.5 m. Each grid cell the surface
    /// passes through gets one vertex, and neighboring cells are joined into quads. Because it's a true 3D
    /// field, digging tunnels and overhangs will work later just by editing the density.
    ///
    /// Vertices are shared and normals come from the density gradient, so hills shade smoothly.
    /// UV x = forest amount (picks plains vs forest colors), UV y = steepness (grass to dirt to rock).
    /// Not thread-safe: use one mesher per thread.
    /// </summary>
    public sealed class TerrainMesher
    {
        const int N = TerrainSettings.ChunkCells;
        const int Pad = 2;            // grid points run from -2 to N + 1
        const int P = N + 2 * Pad;    // points per axis
        const int CellsPerAxis = N + 1; // cells run from -1 to N - 1

        static readonly int3[] CornerOffsets =
        {
            new int3(0, 0, 0), new int3(1, 0, 0), new int3(0, 1, 0), new int3(1, 1, 0),
            new int3(0, 0, 1), new int3(1, 0, 1), new int3(0, 1, 1), new int3(1, 1, 1),
        };

        // The 12 cell edges as pairs of corner indices (into CornerOffsets).
        static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
        static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };

        readonly float[] _heights = new float[P * P];
        readonly float[] _density = new float[P * P * P];
        readonly int[] _vertexIndex = new int[CellsPerAxis * CellsPerAxis * CellsPerAxis];
        readonly float[] _cornerDensity = new float[8];

        static int GridIndex(int x, int y, int z) => (x + Pad) + P * ((y + Pad) + P * (z + Pad));
        static int HeightIndex(int x, int z) => (x + Pad) + P * (z + Pad);
        static int CellIndex(int x, int y, int z) => (x + 1) + CellsPerAxis * ((y + 1) + CellsPerAxis * (z + 1));

        /// <summary>Fills <paramref name="output"/> with the chunk's ground. Returns false if no ground crosses this chunk.</summary>
        public bool Build(TerrainGenerator generator, int3 chunk, MeshBuffers output)
        {
            output.Clear();
            float s = TerrainSettings.CellSize;
            float3 origin = new float3(chunk) * TerrainSettings.ChunkWorldSize;

            // 1. Ground heights for every grid column (x, z).
            float minHeight = float.MaxValue, maxHeight = float.MinValue;
            for (int z = -Pad; z < N + Pad; z++)
            for (int x = -Pad; x < N + Pad; x++)
            {
                float h = generator.Height(origin.x + x * s, origin.z + z * s);
                _heights[HeightIndex(x, z)] = h;
                minHeight = math.min(minHeight, h);
                maxHeight = math.max(maxHeight, h);
            }

            // Skip chunks the ground surface never passes through (all air or all solid).
            float bottom = origin.y - Pad * s;
            float top = origin.y + (N + Pad) * s;
            if (maxHeight < bottom || minHeight > top) return false;

            // 2. Density: how far below the ground surface each grid point is (positive = solid).
            for (int z = -Pad; z < N + Pad; z++)
            for (int y = -Pad; y < N + Pad; y++)
            for (int x = -Pad; x < N + Pad; x++)
                _density[GridIndex(x, y, z)] = _heights[HeightIndex(x, z)] - (origin.y + y * s);

            // 3. One vertex per cell that the surface passes through.
            for (int cz = -1; cz < N; cz++)
            for (int cy = -1; cy < N; cy++)
            for (int cx = -1; cx < N; cx++)
            {
                int cell = CellIndex(cx, cy, cz);
                _vertexIndex[cell] = -1;

                int signs = 0;
                for (int c = 0; c < 8; c++)
                {
                    int3 o = CornerOffsets[c];
                    float d = _density[GridIndex(cx + o.x, cy + o.y, cz + o.z)];
                    _cornerDensity[c] = d;
                    if (d > 0f) signs |= 1 << c;
                }
                if (signs == 0 || signs == 0xFF) continue;

                // Average of the points where the surface crosses the cell's edges.
                float3 sum = float3.zero;
                int crossings = 0;
                for (int e = 0; e < 12; e++)
                {
                    float da = _cornerDensity[EdgeA[e]];
                    float db = _cornerDensity[EdgeB[e]];
                    if ((da > 0f) == (db > 0f)) continue;
                    float t = da / (da - db);
                    sum += math.lerp(new float3(CornerOffsets[EdgeA[e]]), new float3(CornerOffsets[EdgeB[e]]), t);
                    crossings++;
                }
                float3 inCell = sum / crossings; // 0..1 inside the cell
                float3 local = (new float3(cx, cy, cz) + inCell) * s;

                float3 normal = InterpolatedNormal(cx, cy, cz, inCell, s);
                float3 world = origin + local;
                float forest = generator.Forest(world.x, world.z);
                float steep = math.saturate((1f - normal.y) * 3f);

                _vertexIndex[cell] = output.Vertices.Count;
                output.Vertices.Add(local);
                output.Normals.Add(normal);
                output.UVs.Add(new Vector2(0.05f + 0.9f * forest, 0.1f + 0.8f * steep));
            }

            // 4. A quad for every grid edge (owned by this chunk) where the surface crosses.
            for (int axis = 0; axis < 3; axis++)
            {
                int b = (axis + 1) % 3;
                int c = (axis + 2) % 3;
                for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var p = new int3(x, y, z);
                    int3 q = p;
                    q[axis] += 1;
                    bool solidStart = _density[GridIndex(p.x, p.y, p.z)] > 0f;
                    bool solidEnd = _density[GridIndex(q.x, q.y, q.z)] > 0f;
                    if (solidStart == solidEnd) continue;

                    int v0 = CellVertex(p, b, c, -1, -1);
                    int v1 = CellVertex(p, b, c, 0, -1);
                    int v2 = CellVertex(p, b, c, 0, 0);
                    int v3 = CellVertex(p, b, c, -1, 0);
                    if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0) continue;

                    // Wind the quad so its front faces out of the ground (Unity draws clockwise-from-front).
                    if (solidStart) AddQuad(output, v0, v1, v2, v3);
                    else AddQuad(output, v3, v2, v1, v0);
                }
            }
            return output.Indices.Count > 0;
        }

        int CellVertex(int3 p, int axisB, int axisC, int offsetB, int offsetC)
        {
            int3 cell = p;
            cell[axisB] += offsetB;
            cell[axisC] += offsetC;
            return _vertexIndex[CellIndex(cell.x, cell.y, cell.z)];
        }

        static void AddQuad(MeshBuffers output, int a, int b, int c, int d)
        {
            output.Indices.Add(a);
            output.Indices.Add(b);
            output.Indices.Add(c);
            output.Indices.Add(a);
            output.Indices.Add(c);
            output.Indices.Add(d);
        }

        /// <summary>Surface normal at a point inside a cell: the density gradient, blended from the cell's 8 corners.</summary>
        float3 InterpolatedNormal(int cx, int cy, int cz, float3 t, float s)
        {
            float3 gradient = float3.zero;
            for (int c = 0; c < 8; c++)
            {
                int3 o = CornerOffsets[c];
                float wx = o.x == 1 ? t.x : 1f - t.x;
                float wy = o.y == 1 ? t.y : 1f - t.y;
                float wz = o.z == 1 ? t.z : 1f - t.z;
                gradient += GridGradient(cx + o.x, cy + o.y, cz + o.z, s) * (wx * wy * wz);
            }
            // Density rises going into the ground, so the outward normal points the other way.
            return math.normalizesafe(-gradient, new float3(0f, 1f, 0f));
        }

        float3 GridGradient(int x, int y, int z, float s)
        {
            float inv = 1f / (2f * s);
            return new float3(
                _density[GridIndex(x + 1, y, z)] - _density[GridIndex(x - 1, y, z)],
                _density[GridIndex(x, y + 1, z)] - _density[GridIndex(x, y - 1, z)],
                _density[GridIndex(x, y, z + 1)] - _density[GridIndex(x, y, z - 1)]) * inv;
        }
    }
}
