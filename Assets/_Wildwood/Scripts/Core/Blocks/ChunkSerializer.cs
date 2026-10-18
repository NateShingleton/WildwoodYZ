using System;
using System.Collections.Generic;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// Compresses a chunk with run-length encoding: each run is 6 bytes
    /// (2 bytes length, 2 bytes material, 1 byte corners, 1 byte reserved). An empty chunk is one run;
    /// a chunk with a small build in it is usually a few hundred bytes. Used for network snapshots now
    /// and save files later.
    /// </summary>
    public static class ChunkSerializer
    {
        public const int RunBytes = 6;

        public static byte[] Encode(ChunkData chunk)
        {
            var output = new List<byte>(256);
            Block[] blocks = chunk.RawBlocks;
            if (blocks == null)
            {
                WriteRun(output, BlockConstants.ChunkVolume, Block.Air);
                return output.ToArray();
            }

            Block current = blocks[0];
            int length = 1;
            for (int i = 1; i < blocks.Length; i++)
            {
                if (blocks[i] == current && length < ushort.MaxValue)
                {
                    length++;
                    continue;
                }
                WriteRun(output, length, current);
                current = blocks[i];
                length = 1;
            }
            WriteRun(output, length, current);
            return output.ToArray();
        }

        /// <summary>Decodes a chunk. Throws <see cref="FormatException"/> on malformed data. The result is not sanitized.</summary>
        public static Block[] Decode(byte[] data)
        {
            if (data == null || data.Length % RunBytes != 0) throw new FormatException("Chunk data has the wrong length.");

            var blocks = new Block[BlockConstants.ChunkVolume];
            int index = 0;
            for (int p = 0; p < data.Length; p += RunBytes)
            {
                int length = data[p] | (data[p + 1] << 8);
                var block = new Block
                {
                    Material = (ushort)(data[p + 2] | (data[p + 3] << 8)),
                    Corners = data[p + 4],
                    Reserved = data[p + 5],
                };
                if (length == 0 || index + length > BlockConstants.ChunkVolume)
                    throw new FormatException("Chunk data has a bad run length.");
                if (!block.IsAir)
                {
                    for (int i = 0; i < length; i++) blocks[index + i] = block;
                }
                index += length;
            }
            if (index != BlockConstants.ChunkVolume) throw new FormatException("Chunk data is incomplete.");
            return blocks;
        }

        static void WriteRun(List<byte> output, int length, Block block)
        {
            output.Add((byte)length);
            output.Add((byte)(length >> 8));
            output.Add((byte)block.Material);
            output.Add((byte)(block.Material >> 8));
            output.Add(block.Corners);
            output.Add(block.Reserved);
        }
    }
}
