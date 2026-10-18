using System.Collections.Generic;
using UnityEngine;
using Wildwood.Core.Meshing;
using Wildwood.Core.Terrain;

namespace Wildwood.Game.Terrain
{
    /// <summary>
    /// Builds simple stylized trees out of a few shapes: a tapered trunk, then either a cluster of lumpy
    /// spheres (broadleaf) or stacked cones (pine). Canopy normals point away from the canopy's center rather
    /// than following each lump, which gives the soft, rounded "painted" shading from the reference image.
    ///
    /// For now trees are scenery with trunk colliders. Later, hitting a tree will turn it into real blocks
    /// so it can be chopped and fall.
    /// </summary>
    public static class TreeMeshBuilder
    {
        static List<Vector3> s_spherePoints;
        static List<int> s_sphereTriangles;

        /// <summary>Adds a tree to <paramref name="output"/>, positioned relative to <paramref name="origin"/>.</summary>
        public static void AddTree(MeshBuffers output, TreeSpawn tree, Vector3 origin)
        {
            var random = new System.Random((int)(tree.Seed & 0x7FFFFFFF));
            Vector3 root = new Vector3(tree.Position.x, tree.Position.y, tree.Position.z) - origin;
            Quaternion yaw = Quaternion.Euler(0f, tree.Yaw, 0f);

            if (tree.Kind == TreeKind.Pine) AddPine(output, tree, root, yaw, random);
            else AddBroadleaf(output, tree, root, yaw, random);
        }

        /// <summary>Height of the trunk part (used for the trunk collider).</summary>
        public static float TrunkHeight(TreeSpawn tree) => tree.Kind == TreeKind.Pine ? tree.Height * 0.8f : tree.Height * 0.6f;

        static void AddBroadleaf(MeshBuffers output, TreeSpawn tree, Vector3 root, Quaternion yaw, System.Random random)
        {
            float h = tree.Height;
            AddTrunk(output, root, tree.TrunkRadius, TrunkHeight(tree), random);

            bool golden = random.NextDouble() < 0.22;
            int row = golden ? NaturePalettes.GoldenLeafRow : NaturePalettes.LeafRow;
            Vector3 canopyCenter = root + Vector3.up * (h * 0.7f);

            int blobs = 3 + random.Next(3);
            AddBlob(output, canopyCenter, h * 0.27f, canopyCenter, row, random);
            for (int i = 0; i < blobs; i++)
            {
                float angle = (i / (float)blobs) * 360f + (float)random.NextDouble() * 40f;
                Vector3 direction = yaw * (Quaternion.Euler(0f, angle, 0f) * Vector3.forward);
                Vector3 center = canopyCenter + direction * (h * Lerp(0.14f, 0.22f, random)) +
                                 Vector3.up * (h * Lerp(-0.1f, 0.12f, random));
                AddBlob(output, center, h * Lerp(0.16f, 0.22f, random), canopyCenter, row, random);
            }
        }

        static void AddPine(MeshBuffers output, TreeSpawn tree, Vector3 root, Quaternion yaw, System.Random random)
        {
            float h = tree.Height;
            AddTrunk(output, root, tree.TrunkRadius * 0.85f, TrunkHeight(tree), random);

            int tiers = 4;
            for (int i = 0; i < tiers; i++)
            {
                float t = i / (float)(tiers - 1);
                float baseY = h * Mathf.Lerp(0.2f, 0.62f, t);
                float coneHeight = h * Mathf.Lerp(0.34f, 0.38f, t);
                float radius = h * Mathf.Lerp(0.22f, 0.09f, t) * Lerp(0.9f, 1.1f, random);
                int shade = random.Next(NaturePalettes.TreeShades);
                AddCone(output, root + Vector3.up * baseY, radius, coneHeight, yaw, NaturePalettes.TreeUV(NaturePalettes.PineRow, shade));
            }
        }

        static void AddTrunk(MeshBuffers output, Vector3 root, float radius, float height, System.Random random)
        {
            const int sides = 6;
            Vector2 uv = NaturePalettes.TreeUV(NaturePalettes.TrunkRow, random.Next(NaturePalettes.TreeShades));
            Vector3 bottom = root + Vector3.down * 0.4f; // sink into the ground so slopes never show a gap
            Vector3 top = root + Vector3.up * height;
            int start = output.Vertices.Count;
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                var radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                output.Vertices.Add(bottom + radial * radius);
                output.Vertices.Add(top + radial * (radius * 0.55f));
                output.Normals.Add(radial);
                output.Normals.Add(radial);
                output.UVs.Add(uv);
                output.UVs.Add(uv);
            }
            for (int i = 0; i < sides; i++)
            {
                int a = start + i * 2;
                int b = start + ((i + 1) % sides) * 2;
                Vector3 outward = output.Normals[a] + output.Normals[b];
                AddTriangleFacing(output, a, a + 1, b + 1, outward);
                AddTriangleFacing(output, a, b + 1, b, outward);
            }
        }

        /// <summary>A lumpy sphere whose normals point away from <paramref name="shadingCenter"/>.</summary>
        static void AddBlob(MeshBuffers output, Vector3 center, float radius, Vector3 shadingCenter, int row, System.Random random)
        {
            EnsureSphere();
            Vector2 uv = NaturePalettes.TreeUV(row, random.Next(NaturePalettes.TreeShades));
            int start = output.Vertices.Count;
            foreach (Vector3 unit in s_spherePoints)
            {
                float lump = Lerp(0.88f, 1.1f, random);
                Vector3 position = center + new Vector3(unit.x, unit.y * 0.85f, unit.z) * (radius * lump);
                output.Vertices.Add(position);
                Vector3 normal = (position - shadingCenter).normalized;
                output.Normals.Add(Vector3.Slerp(normal, Vector3.up, 0.25f)); // a little extra light from above
                output.UVs.Add(uv);
            }
            for (int i = 0; i < s_sphereTriangles.Count; i += 3)
            {
                int a = start + s_sphereTriangles[i];
                int b = start + s_sphereTriangles[i + 1];
                int c = start + s_sphereTriangles[i + 2];
                Vector3 outward = (output.Vertices[a] + output.Vertices[b] + output.Vertices[c]) / 3f - center;
                AddTriangleFacing(output, a, b, c, outward);
            }
        }

        static void AddCone(MeshBuffers output, Vector3 baseCenter, float radius, float height, Quaternion yaw, Vector2 uv)
        {
            const int sides = 8;
            Vector3 apex = baseCenter + Vector3.up * height;
            int apexIndex = output.Vertices.Count;
            output.Vertices.Add(apex);
            output.Normals.Add(Vector3.up);
            output.UVs.Add(uv);

            int ring = output.Vertices.Count;
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                Vector3 radial = yaw * new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                output.Vertices.Add(baseCenter + radial * radius);
                output.Normals.Add((radial * 0.6f + Vector3.up * 0.8f).normalized); // soft, mostly upward
                output.UVs.Add(uv);
            }
            // The underside, so the cone looks solid from below.
            int underside = output.Vertices.Count;
            output.Vertices.Add(baseCenter);
            output.Normals.Add(Vector3.down);
            output.UVs.Add(uv);

            for (int i = 0; i < sides; i++)
            {
                int a = ring + i;
                int b = ring + (i + 1) % sides;
                Vector3 side = (output.Vertices[a] + output.Vertices[b]) * 0.5f - baseCenter;
                AddTriangleFacing(output, apexIndex, a, b, side + Vector3.up * radius * 0.2f);
                AddTriangleFacing(output, underside, a, b, Vector3.down);
            }
        }

        /// <summary>Adds a triangle, flipping it if needed so its front faces <paramref name="outward"/>.</summary>
        static void AddTriangleFacing(MeshBuffers output, int a, int b, int c, Vector3 outward)
        {
            Vector3 pa = output.Vertices[a];
            Vector3 cross = Vector3.Cross(output.Vertices[b] - pa, output.Vertices[c] - pa);
            if (Vector3.Dot(cross, outward) < 0f)
            {
                int swap = b;
                b = c;
                c = swap;
            }
            output.Indices.Add(a);
            output.Indices.Add(b);
            output.Indices.Add(c);
        }

        static float Lerp(float a, float b, System.Random random) => a + (b - a) * (float)random.NextDouble();

        /// <summary>A unit sphere made by splitting each face of an icosahedron into 4 (42 points, 80 triangles).</summary>
        static void EnsureSphere()
        {
            if (s_spherePoints != null) return;

            float t = (1f + Mathf.Sqrt(5f)) / 2f;
            var points = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < points.Count; i++) points[i] = points[i].normalized;

            var faces = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
                1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
                4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };

            var midpoints = new Dictionary<long, int>();
            int Midpoint(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (midpoints.TryGetValue(key, out int index)) return index;
                index = points.Count;
                points.Add(((points[a] + points[b]) * 0.5f).normalized);
                midpoints.Add(key, index);
                return index;
            }

            var subdivided = new List<int>(faces.Count * 4);
            for (int i = 0; i < faces.Count; i += 3)
            {
                int a = faces[i], b = faces[i + 1], c = faces[i + 2];
                int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                subdivided.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }

            s_spherePoints = points;
            s_sphereTriangles = subdivided;
        }
    }
}
