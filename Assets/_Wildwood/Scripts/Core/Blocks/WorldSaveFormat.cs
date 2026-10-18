using System;
using System.IO;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// The save file: the world seed (which regenerates all the untouched terrain) plus every chunk of blocks.
    /// Same idea as joining a session: seed + edits.
    ///
    /// Layout (little-endian): magic "WWSV", format version, seed, chunk count,
    /// then per chunk: x, y, z, byte length, run-length-encoded blocks (see <see cref="ChunkSerializer"/>).
    /// </summary>
    public static class WorldSaveFormat
    {
        const uint Magic = 0x56535757; // "WWSV" as little-endian bytes
        public const int CurrentVersion = 1;

        public static void Write(Stream stream, int seed, BlockWorld world)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(CurrentVersion);
            writer.Write(seed);
            writer.Write(world.ChunkCount);
            foreach (var chunk in world.Chunks)
            {
                byte[] data = ChunkSerializer.Encode(chunk);
                writer.Write(chunk.Coord.x);
                writer.Write(chunk.Coord.y);
                writer.Write(chunk.Coord.z);
                writer.Write(data.Length);
                writer.Write(data);
            }
        }

        /// <summary>Loads chunks into <paramref name="world"/> (which should be empty) and returns the seed.</summary>
        public static int Read(Stream stream, BlockWorld world)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic) throw new FormatException("Not a Wildwood world file.");
            int version = reader.ReadInt32();
            if (version < 1 || version > CurrentVersion) throw new FormatException($"Unsupported world file version {version}.");

            int seed = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count < 0) throw new FormatException("Bad chunk count.");
            for (int i = 0; i < count; i++)
            {
                var coord = new int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                int length = reader.ReadInt32();
                if (length <= 0 || length > BlockConstants.ChunkVolume * ChunkSerializer.RunBytes)
                    throw new FormatException("Bad chunk length.");
                byte[] data = reader.ReadBytes(length);
                if (data.Length != length) throw new EndOfStreamException("World file is cut short.");
                world.LoadChunk(coord, ChunkSerializer.Decode(data));
            }
            return seed;
        }
    }
}
