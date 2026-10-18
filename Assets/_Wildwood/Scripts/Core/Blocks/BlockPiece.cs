using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// A group of blocks that broke off the world and is now a physics object.
    /// Blocks are stored in a local box: index = x + Size.x * (z + Size.z * y).
    /// <see cref="Origin"/> is the world block position of local (0, 0, 0) at the moment it broke off;
    /// after that the piece moves freely with <see cref="Position"/> and <see cref="Rotation"/>.
    /// </summary>
    public sealed class BlockPiece
    {
        /// <summary>Biggest box a piece may occupy (keeps network data and memory sane).</summary>
        public const int MaxSide = 256;

        public uint Id;
        public int3 Origin;
        public int3 Size;
        public Block[] Blocks;

        /// <summary>World position of the piece's local origin, in meters.</summary>
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;

        public int Volume => Size.x * Size.y * Size.z;

        public int Index(int x, int y, int z) => x + Size.x * (z + Size.z * y);

        public Block Get(int x, int y, int z) => Blocks[Index(x, y, z)];

        /// <summary>
        /// Lifts the given blocks out of <paramref name="world"/> (setting them to air) into a new piece.
        /// Returns null if the group is too big for one piece.
        /// </summary>
        public static BlockPiece Extract(BlockWorld world, List<int3> positions, uint id)
        {
            if (positions.Count == 0) return null;

            int3 min = positions[0], max = positions[0];
            foreach (int3 p in positions)
            {
                min = math.min(min, p);
                max = math.max(max, p);
            }
            int3 size = max - min + 1;
            if (math.any(size > MaxSide)) return null;

            var piece = new BlockPiece
            {
                Id = id,
                Origin = min,
                Size = size,
                Blocks = new Block[size.x * size.y * size.z],
                Position = new Vector3(min.x, min.y, min.z) * BlockConstants.BlockSize,
                Rotation = Quaternion.identity,
            };

            foreach (int3 p in positions)
            {
                int3 local = p - min;
                piece.Blocks[piece.Index(local.x, local.y, local.z)] = world.GetBlock(p);
            }
            foreach (int3 p in positions) world.SetBlock(p, Block.Air);
            return piece;
        }

        /// <summary>Removes this piece's blocks from a world (used by clients when the host says it broke off).</summary>
        public void RemoveFrom(BlockWorld world)
        {
            for (int y = 0; y < Size.y; y++)
            for (int z = 0; z < Size.z; z++)
            for (int x = 0; x < Size.x; x++)
            {
                if (Get(x, y, z).IsAir) continue;
                world.SetBlock(Origin + new int3(x, y, z), Block.Air);
            }
        }

        /// <summary>Total mass in kg, from each block's volume and material density.</summary>
        public float Mass()
        {
            float blockVolume = BlockConstants.BlockSize * BlockConstants.BlockSize * BlockConstants.BlockSize;
            float mass = 0f;
            foreach (Block block in Blocks)
            {
                if (block.IsAir) continue;
                mass += CornerShapes.Get(block.Corners).Volume * blockVolume * BlockMaterials.Get(block.Material).Density;
            }
            return mass;
        }

        /// <summary>
        /// Approximates the piece with as few boxes as possible (every non-air block counted as a full cube),
        /// for physics colliders. Each box is (min, max) in local block coordinates, inclusive.
        /// Returns false if more than <paramref name="maxBoxes"/> would be needed.
        /// </summary>
        public bool BuildBoxes(List<(int3 min, int3 max)> boxes, int maxBoxes)
        {
            boxes.Clear();
            var used = new bool[Blocks.Length];
            for (int y = 0; y < Size.y; y++)
            for (int z = 0; z < Size.z; z++)
            for (int x = 0; x < Size.x; x++)
            {
                if (!Free(used, x, y, z)) continue;

                int x2 = x;
                while (x2 + 1 < Size.x && Free(used, x2 + 1, y, z)) x2++;

                int z2 = z;
                while (z2 + 1 < Size.z && RowFree(used, x, x2, y, z2 + 1)) z2++;

                int y2 = y;
                while (y2 + 1 < Size.y && LayerFree(used, x, x2, y2 + 1, z, z2)) y2++;

                for (int yy = y; yy <= y2; yy++)
                for (int zz = z; zz <= z2; zz++)
                for (int xx = x; xx <= x2; xx++)
                    used[Index(xx, yy, zz)] = true;

                boxes.Add((new int3(x, y, z), new int3(x2, y2, z2)));
                if (boxes.Count > maxBoxes) return false;
            }
            return true;
        }

        bool Free(bool[] used, int x, int y, int z)
        {
            int i = Index(x, y, z);
            return !used[i] && !Blocks[i].IsAir;
        }

        bool RowFree(bool[] used, int x1, int x2, int y, int z)
        {
            for (int x = x1; x <= x2; x++)
                if (!Free(used, x, y, z)) return false;
            return true;
        }

        bool LayerFree(bool[] used, int x1, int x2, int y, int z1, int z2)
        {
            for (int z = z1; z <= z2; z++)
                if (!RowFree(used, x1, x2, y, z)) return false;
            return true;
        }

        /// <summary>Serializes everything but the id, for the network.</summary>
        public byte[] EncodeBlocks() => ChunkSerializer.EncodeBlocks(Blocks);

        public static Block[] DecodeBlocks(byte[] data, int3 size)
        {
            if (math.any(size < 1) || math.any(size > MaxSide)) throw new FormatException("Bad piece size.");
            Block[] blocks = ChunkSerializer.DecodeBlocks(data, size.x * size.y * size.z);
            for (int i = 0; i < blocks.Length; i++) blocks[i] = BlockWorld.Sanitize(blocks[i]);
            return blocks;
        }
    }

    /// <summary>Where a piece is right now (sent from the host about 20 times a second while it moves).</summary>
    public struct BlockPieceState
    {
        public uint Id;
        public Vector3 Position;
        public Quaternion Rotation;
    }
}
