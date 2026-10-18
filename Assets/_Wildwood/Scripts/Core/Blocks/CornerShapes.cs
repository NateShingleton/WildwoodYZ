using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// The six cube faces, in the order used everywhere: +X, -X, +Y, -Y, +Z, -Z.
    /// Index 6 means "slanted": a cut face inside the cube that a neighbor can never hide.
    /// </summary>
    public static class BlockFaces
    {
        public const int PosX = 0, NegX = 1, PosY = 2, NegY = 3, PosZ = 4, NegZ = 5;
        public const int Count = 6;
        public const int Slanted = 6;

        public static readonly int3[] Directions =
        {
            new int3(1, 0, 0), new int3(-1, 0, 0),
            new int3(0, 1, 0), new int3(0, -1, 0),
            new int3(0, 0, 1), new int3(0, 0, -1),
        };

        public static readonly Vector3[] Normals =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
        };

        public static int Opposite(int face) => face ^ 1;

        /// <summary>The axis (0 = x, 1 = y, 2 = z) a face is perpendicular to.</summary>
        public static int Axis(int face) => face >> 1;

        /// <summary>1 for the positive faces, 0 for the negative ones.</summary>
        public static int Side(int face) => (face & 1) == 0 ? 1 : 0;
    }

    /// <summary>
    /// The geometry for one corner mask: the solid you get by connecting the corners that remain.
    /// </summary>
    public sealed class CornerShape
    {
        /// <summary>The corner mask this shape was built from.</summary>
        public readonly byte Mask;

        /// <summary>True when the remaining corners don't enclose any volume (fewer than 4, or all flat).</summary>
        public readonly bool IsEmpty;

        /// <summary>Volume as a fraction of a full block (1 for a cube, 0.5 for a slope).</summary>
        public readonly float Volume;

        /// <summary>
        /// For each of the 6 cube faces, which of that face's 4 corners the shape covers (4-bit mask), or 0
        /// if the shape doesn't touch that face with any area. Bit = u + 2v, where u and v are the two
        /// other axes in x, y, z order. Opposite faces of neighboring blocks use the same bits, so a face
        /// is hidden when the neighbor's coverage contains all of its bits.
        /// </summary>
        public readonly byte[] FaceCoverage;

        /// <summary>
        /// Triangles grouped by face (0-5) plus slanted (6). Each triangle is 3 corner indices,
        /// wound so Unity draws the outside.
        /// </summary>
        public readonly byte[][] Triangles;

        /// <summary>One outward normal per slanted triangle.</summary>
        public readonly Vector3[] SlantedNormals;

        internal CornerShape(byte mask, bool isEmpty, float volume, byte[] faceCoverage, byte[][] triangles, Vector3[] slantedNormals)
        {
            Mask = mask;
            IsEmpty = isEmpty;
            Volume = volume;
            FaceCoverage = faceCoverage;
            Triangles = triangles;
            SlantedNormals = slantedNormals;
        }
    }

    /// <summary>
    /// Lookup table of all 256 corner masks, built once at startup.
    /// 151 masks give a real 3D shape; the rest are too few corners or flat, and count as air.
    /// </summary>
    public static class CornerShapes
    {
        const float Epsilon = 1e-4f;

        static readonly CornerShape[] Table = BuildTable();

        public static CornerShape Get(byte mask) => Table[mask];

        public static bool IsSolid(byte mask) => !Table[mask].IsEmpty;

        /// <summary>Position of corner index 0-7 inside a unit cube.</summary>
        public static Vector3 CornerPosition(int index) =>
            new Vector3(index & 1, (index >> 1) & 1, (index >> 2) & 1);

        static CornerShape[] BuildTable()
        {
            var table = new CornerShape[256];
            for (int mask = 0; mask < 256; mask++) table[mask] = BuildShape((byte)mask);
            return table;
        }

        static CornerShape Empty(byte mask)
        {
            var triangles = new byte[7][];
            for (int i = 0; i < 7; i++) triangles[i] = new byte[0];
            return new CornerShape(mask, true, 0f, new byte[6], triangles, new Vector3[0]);
        }

        static CornerShape BuildShape(byte mask)
        {
            var present = new List<int>(8);
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) != 0) present.Add(i);
            if (present.Count < 4) return Empty(mask);

            // 1. Find every plane of the convex hull: planes through 3 corners with all other corners on one side.
            var planes = new List<(Vector3 normal, float distance)>();
            bool flat = true;
            for (int a = 0; a < present.Count; a++)
            for (int b = a + 1; b < present.Count; b++)
            for (int c = b + 1; c < present.Count; c++)
            {
                Vector3 pa = CornerPosition(present[a]);
                Vector3 normal = Vector3.Cross(CornerPosition(present[b]) - pa, CornerPosition(present[c]) - pa);
                if (normal.sqrMagnitude < Epsilon) continue;
                normal.Normalize();
                float distance = Vector3.Dot(normal, pa);

                bool allBelow = true, allAbove = true;
                foreach (int i in present)
                {
                    float side = Vector3.Dot(normal, CornerPosition(i)) - distance;
                    if (Mathf.Abs(side) > Epsilon) flat = false;
                    if (side > Epsilon) allBelow = false;
                    if (side < -Epsilon) allAbove = false;
                }
                if (!allBelow)
                {
                    if (!allAbove) continue; // corners on both sides: not a hull face
                    normal = -normal;
                    distance = -distance;
                }
                if (!ContainsPlane(planes, normal, distance)) planes.Add((normal, distance));
            }
            if (flat) return Empty(mask);

            // 2. Turn each plane into a polygon of the corners lying on it, then into triangles.
            var triangleLists = new List<byte>[7];
            for (int i = 0; i < 7; i++) triangleLists[i] = new List<byte>();
            var slantedNormals = new List<Vector3>();
            var faceCoverage = new byte[6];
            var onPlane = new List<int>(8);

            foreach (var (normal, distance) in planes)
            {
                onPlane.Clear();
                Vector3 center = Vector3.zero;
                foreach (int i in present)
                {
                    if (Mathf.Abs(Vector3.Dot(normal, CornerPosition(i)) - distance) > Epsilon) continue;
                    onPlane.Add(i);
                    center += CornerPosition(i);
                }
                center /= onPlane.Count;

                // Sort the polygon's corners counter-clockwise around the outward normal. With this order,
                // Vector3.Cross(b - a, c - a) points outward, which is what Unity treats as the front face.
                Vector3 u = (CornerPosition(onPlane[0]) - center).normalized;
                Vector3 v = Vector3.Cross(normal, u);
                onPlane.Sort((i, j) => Angle(i).CompareTo(Angle(j)));
                float Angle(int corner)
                {
                    Vector3 offset = CornerPosition(corner) - center;
                    return Mathf.Atan2(Vector3.Dot(offset, v), Vector3.Dot(offset, u));
                }

                int face = ClassifyFace(normal, onPlane);
                for (int k = 1; k < onPlane.Count - 1; k++)
                {
                    triangleLists[face].Add((byte)onPlane[0]);
                    triangleLists[face].Add((byte)onPlane[k]);
                    triangleLists[face].Add((byte)onPlane[k + 1]);
                    if (face == BlockFaces.Slanted) slantedNormals.Add(normal);
                }
                if (face < BlockFaces.Count)
                {
                    foreach (int corner in onPlane) faceCoverage[face] |= FaceBit(face, corner);
                }
            }

            // 3. Volume from the closed, outward-facing triangle mesh (divergence theorem).
            float volume = 0f;
            var triangles = new byte[7][];
            for (int f = 0; f < 7; f++)
            {
                triangles[f] = triangleLists[f].ToArray();
                for (int t = 0; t < triangles[f].Length; t += 3)
                {
                    Vector3 a = CornerPosition(triangles[f][t]);
                    Vector3 b = CornerPosition(triangles[f][t + 1]);
                    Vector3 c = CornerPosition(triangles[f][t + 2]);
                    volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
                }
            }

            return new CornerShape(mask, false, volume, faceCoverage, triangles, slantedNormals.ToArray());
        }

        static bool ContainsPlane(List<(Vector3 normal, float distance)> planes, Vector3 normal, float distance)
        {
            foreach (var plane in planes)
            {
                if (Vector3.Dot(plane.normal, normal) > 1f - Epsilon && Mathf.Abs(plane.distance - distance) < Epsilon)
                    return true;
            }
            return false;
        }

        /// <summary>Which cube face a hull plane lies on, or Slanted if it's a cut through the cube.</summary>
        static int ClassifyFace(Vector3 normal, List<int> corners)
        {
            for (int face = 0; face < BlockFaces.Count; face++)
            {
                if (Vector3.Dot(normal, BlockFaces.Normals[face]) < 1f - Epsilon) continue;
                int axis = BlockFaces.Axis(face);
                int side = BlockFaces.Side(face);
                bool allOnFace = true;
                foreach (int corner in corners)
                {
                    int coordinate = (corner >> axis) & 1;
                    if (coordinate != side) { allOnFace = false; break; }
                }
                if (allOnFace) return face;
            }
            return BlockFaces.Slanted;
        }

        /// <summary>Bit for a corner within a face's 4-corner coverage mask (u + 2v over the other two axes).</summary>
        static byte FaceBit(int face, int corner)
        {
            int axis = BlockFaces.Axis(face);
            int first = axis == 0 ? 1 : 0;
            int second = axis == 2 ? 1 : 2;
            int u = (corner >> first) & 1;
            int v = (corner >> second) & 1;
            return (byte)(1 << (u + 2 * v));
        }
    }
}
