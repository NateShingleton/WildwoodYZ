using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Wildwood.Core.Terrain;

namespace Wildwood.Game.Terrain
{
    /// <summary>
    /// Draws grass tufts with GPU instancing: one small tuft mesh drawn thousands of times, only near the camera.
    /// Golden tufts on the plains, shorter green ones in forests. Blade normals all point up, so grass is lit
    /// evenly and glows in low sun instead of looking like spiky geometry.
    ///
    /// Not animated yet (wind needs a custom shader, which comes with the art pass).
    /// </summary>
    public sealed class GrassField
    {
        const float Spacing = 0.55f;
        const float ClearingWithoutGrass = TerrainSettings.SpawnClearingRadius - 4f;
        const int MaxPerDraw = 1023;

        sealed class Patch
        {
            public Matrix4x4[] Plains;
            public int PlainsCount;
            public Matrix4x4[] Forest;
            public int ForestCount;
            public Bounds Bounds;
        }

        readonly Dictionary<int2, Patch> _patches = new Dictionary<int2, Patch>();
        readonly List<int2> _toRemove = new List<int2>();
        readonly Mesh _plainsTuft;
        readonly Mesh _forestTuft;
        readonly Material _material;

        public GrassField(Material material)
        {
            _material = material;
            _plainsTuft = BuildTuft("Grass Tuft (plains)", NaturePalettes.GrassPlainsU, 9, 0.55f, 0.85f, 1);
            _forestTuft = BuildTuft("Grass Tuft (forest)", NaturePalettes.GrassForestU, 6, 0.3f, 0.5f, 2);
        }

        public void Dispose()
        {
            Object.Destroy(_plainsTuft);
            Object.Destroy(_forestTuft);
            _patches.Clear();
        }

        public void Clear() => _patches.Clear();

        /// <summary>Places grass on one terrain column (16 x 16 m) if it isn't already.</summary>
        public void EnsurePatch(TerrainGenerator generator, int2 column)
        {
            if (_patches.ContainsKey(column)) return;

            float size = TerrainSettings.ChunkWorldSize;
            float2 min = new float2(column) * size;
            var plains = new List<Matrix4x4>();
            var forest = new List<Matrix4x4>();
            float minY = float.MaxValue, maxY = float.MinValue;

            int steps = (int)(size / Spacing);
            for (int iz = 0; iz < steps; iz++)
            for (int ix = 0; ix < steps; ix++)
            {
                int gx = (int)math.floor(min.x / Spacing) + ix;
                int gz = (int)math.floor(min.y / Spacing) + iz;
                uint h = TerrainGenerator.Hash(TerrainGenerator.Hash((uint)generator.Seed, (uint)gx), (uint)gz);
                float x = (gx + TerrainGenerator.Unit(h, 1)) * Spacing;
                float z = (gz + TerrainGenerator.Unit(h, 2)) * Spacing;
                if (x < min.x || x >= min.x + size || z < min.y || z >= min.y + size) continue;
                // Keep the middle of the spawn clearing trampled, so grass doesn't poke through the test builds.
                if (x * x + z * z < ClearingWithoutGrass * ClearingWithoutGrass) continue;

                float forestAmount = generator.Forest(x, z);
                float density = math.lerp(0.95f, 0.35f, forestAmount);
                if (TerrainGenerator.Unit(h, 3) > density) continue;

                float y = generator.Height(x, z);
                float scale = math.lerp(0.75f, 1.3f, TerrainGenerator.Unit(h, 4));
                var matrix = Matrix4x4.TRS(
                    new Vector3(x, y - 0.03f, z),
                    Quaternion.Euler(0f, TerrainGenerator.Unit(h, 5) * 360f, 0f),
                    new Vector3(scale, scale * math.lerp(0.8f, 1.2f, TerrainGenerator.Unit(h, 6)), scale));

                if (forestAmount > 0.55f) forest.Add(matrix);
                else plains.Add(matrix);
                minY = math.min(minY, y);
                maxY = math.max(maxY, y);
            }

            var center = new Vector3(min.x + size * 0.5f, (minY + maxY) * 0.5f, min.y + size * 0.5f);
            _patches[column] = new Patch
            {
                Plains = plains.ToArray(),
                PlainsCount = plains.Count,
                Forest = forest.ToArray(),
                ForestCount = forest.Count,
                Bounds = new Bounds(center, new Vector3(size + 2f, (maxY - minY) + 4f, size + 2f)),
            };
        }

        /// <summary>Drops patches farther than <paramref name="keepRadius"/> columns from <paramref name="center"/>.</summary>
        public void Prune(int2 center, int keepRadius)
        {
            _toRemove.Clear();
            foreach (var pair in _patches)
            {
                int2 d = math.abs(pair.Key - center);
                if (math.max(d.x, d.y) > keepRadius) _toRemove.Add(pair.Key);
            }
            foreach (var key in _toRemove) _patches.Remove(key);
        }

        /// <summary>Call every frame to draw grass near <paramref name="focus"/>.</summary>
        public void Draw(Vector3 focus, float maxDistance)
        {
            if (_material == null) return;
            foreach (var patch in _patches.Values)
            {
                if (patch.Bounds.SqrDistance(focus) > maxDistance * maxDistance) continue;
                var renderParams = new RenderParams(_material)
                {
                    worldBounds = patch.Bounds,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = true,
                };
                DrawInstances(renderParams, _plainsTuft, patch.Plains, patch.PlainsCount);
                DrawInstances(renderParams, _forestTuft, patch.Forest, patch.ForestCount);
            }
        }

        static void DrawInstances(RenderParams renderParams, Mesh mesh, Matrix4x4[] instances, int count)
        {
            for (int start = 0; start < count; start += MaxPerDraw)
            {
                int batch = Mathf.Min(MaxPerDraw, count - start);
                Graphics.RenderMeshInstanced(renderParams, mesh, 0, instances, batch, start);
            }
        }

        /// <summary>A tuft of thin triangular blades fanning out from one point.</summary>
        static Mesh BuildTuft(string name, float paletteU, int blades, float minHeight, float maxHeight, int seed)
        {
            var random = new System.Random(seed);
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (int i = 0; i < blades; i++)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                float offset = (float)random.NextDouble() * 0.12f;
                var root = new Vector3(Mathf.Cos(angle) * offset, 0f, Mathf.Sin(angle) * offset);
                float facing = (float)random.NextDouble() * Mathf.PI * 2f;
                var across = new Vector3(Mathf.Cos(facing), 0f, Mathf.Sin(facing)) * 0.035f;
                float height = Mathf.Lerp(minHeight, maxHeight, (float)random.NextDouble());
                Vector3 lean = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (height * 0.35f);

                int start = vertices.Count;
                vertices.Add(root - across);
                vertices.Add(root + across);
                vertices.Add(root + lean + Vector3.up * height);
                uvs.Add(new Vector2(paletteU, 0.02f));
                uvs.Add(new Vector2(paletteU, 0.02f));
                uvs.Add(new Vector2(paletteU, 0.98f));
                triangles.Add(start);
                triangles.Add(start + 2);
                triangles.Add(start + 1);
            }

            var normals = new List<Vector3>(vertices.Count);
            for (int i = 0; i < vertices.Count; i++) normals.Add(Vector3.up);

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
