using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Wildwood.Core.Blocks;

namespace Wildwood.Core.Meshing
{
    /// <summary>Reusable lists the mesher writes into, ready to hand to a Unity Mesh.</summary>
    public sealed class MeshBuffers
    {
        public readonly List<Vector3> Vertices = new List<Vector3>(4096);
        public readonly List<Vector3> Normals = new List<Vector3>(4096);
        public readonly List<Vector2> UVs = new List<Vector2>(4096);
        public readonly List<int> Indices = new List<int>(8192);

        public bool IsEmpty => Indices.Count == 0;

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            UVs.Clear();
            Indices.Clear();
        }
    }

    /// <summary>
    /// Turns one chunk into triangles. Every block contributes its corner shape; a face on the cube boundary
    /// is skipped when the neighbor's matching face fully covers it. Faces are flat-shaded (each triangle gets
    /// its own vertices) for the crisp low-poly look. Colors come from a small palette texture:
    /// row = material, column = one of 8 shades picked per block, so walls get subtle variation.
    ///
    /// This is a plain C# version for clarity. When chunks get busy it'll move to a Burst job
    /// and merge flat faces (greedy meshing) without changing the output format.
    /// </summary>
    public static class ChunkMesher
    {
        public const int PaletteShades = 8;

        const int Size = BlockConstants.ChunkSize;

        /// <param name="shadeOffset">Added to block positions when picking shades, so a piece that broke off keeps
        /// the exact colors it had in the world.</param>
        public static void Build(BlockWorld world, int3 chunkCoord, MeshBuffers output, int paletteRows, int3 shadeOffset = default)
        {
            output.Clear();
            if (!world.TryGetChunk(chunkCoord, out var chunk) || chunk.IsEmpty) return;

            int3 origin = chunkCoord * Size;
            float scale = BlockConstants.BlockSize;

            for (int y = 0; y < Size; y++)
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                Block block = chunk.Get(x, y, z);
                if (block.IsAir) continue;
                CornerShape shape = CornerShapes.Get(block.Corners);
                if (shape.IsEmpty) continue;

                var local = new Vector3(x, y, z);
                Vector2 uv = PaletteUV(block.Material, origin + new int3(x, y, z) + shadeOffset, paletteRows);

                for (int face = 0; face < BlockFaces.Count; face++)
                {
                    byte[] triangles = shape.Triangles[face];
                    if (triangles.Length == 0) continue;

                    int3 n = new int3(x, y, z) + BlockFaces.Directions[face];
                    Block neighbor = IsInside(n) ? chunk.Get(n.x, n.y, n.z) : world.GetBlock(origin + n);
                    if (!neighbor.IsAir)
                    {
                        byte cover = CornerShapes.Get(neighbor.Corners).FaceCoverage[BlockFaces.Opposite(face)];
                        if ((shape.FaceCoverage[face] & ~cover) == 0) continue; // fully hidden
                    }
                    AddTriangles(output, triangles, local, scale, BlockFaces.Normals[face], uv);
                }

                byte[] slanted = shape.Triangles[BlockFaces.Slanted];
                for (int t = 0; t < slanted.Length; t += 3)
                {
                    AddTriangle(output, slanted[t], slanted[t + 1], slanted[t + 2], local, scale, shape.SlantedNormals[t / 3], uv);
                }
            }
        }

        static bool IsInside(int3 p) => (uint)p.x < Size && (uint)p.y < Size && (uint)p.z < Size;

        static void AddTriangles(MeshBuffers output, byte[] corners, Vector3 local, float scale, Vector3 normal, Vector2 uv)
        {
            for (int t = 0; t < corners.Length; t += 3)
                AddTriangle(output, corners[t], corners[t + 1], corners[t + 2], local, scale, normal, uv);
        }

        static void AddTriangle(MeshBuffers output, int a, int b, int c, Vector3 local, float scale, Vector3 normal, Vector2 uv)
        {
            int start = output.Vertices.Count;
            output.Vertices.Add((local + CornerShapes.CornerPosition(a)) * scale);
            output.Vertices.Add((local + CornerShapes.CornerPosition(b)) * scale);
            output.Vertices.Add((local + CornerShapes.CornerPosition(c)) * scale);
            for (int i = 0; i < 3; i++)
            {
                output.Normals.Add(normal);
                output.UVs.Add(uv);
            }
            output.Indices.Add(start);
            output.Indices.Add(start + 1);
            output.Indices.Add(start + 2);
        }

        /// <summary>Texture coordinate of the palette cell for a material, with a per-block shade.</summary>
        public static Vector2 PaletteUV(ushort material, int3 worldBlock, int paletteRows)
        {
            int row = math.min(material, paletteRows - 1);
            int shade = (int)(Hash(worldBlock) % PaletteShades);
            return new Vector2((shade + 0.5f) / PaletteShades, (row + 0.5f) / paletteRows);
        }

        /// <summary>Cheap, stable hash of a block position (same on every machine).</summary>
        public static uint Hash(int3 p)
        {
            unchecked
            {
                uint h = (uint)p.x * 73856093u ^ (uint)p.y * 19349663u ^ (uint)p.z * 83492791u;
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                h ^= h >> 15;
                return h;
            }
        }
    }
}
