using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Wildwood.Core.Blocks;
using Wildwood.Core.Terrain;

namespace Wildwood.Core.Tests
{
    /// <summary>Blocks breaking off when they lose support, and the loose pieces they turn into.</summary>
    public class StructureTests
    {
        static readonly Block Stone = Block.Solid(BlockMaterials.Stone);

        /// <summary>Flat ground at height 0: blocks at y = 0 rest on it.</summary>
        static StructureCheck FlatGround() => new StructureCheck((x, z) => 0f);

        /// <summary>A 5-block pillar at x = 0 with a 6-block beam on top of it, sticking out along +x.</summary>
        static BlockWorld PillarAndBeam()
        {
            var world = new BlockWorld();
            for (int y = 0; y < 5; y++) world.SetBlock(new int3(0, y, 0), Stone);
            for (int x = 0; x < 6; x++) world.SetBlock(new int3(x, 5, 0), Stone);
            return world;
        }

        static List<int3> SeedsAround(int3 p)
        {
            var seeds = new List<int3> { p };
            for (int face = 0; face < BlockFaces.Count; face++) seeds.Add(p + BlockFaces.Directions[face]);
            return seeds;
        }

        [Test]
        public void SupportedStructureStaysPut()
        {
            var world = PillarAndBeam();
            var groups = new List<List<int3>>();
            FlatGround().FindFloating(world, SeedsAround(new int3(5, 5, 0)), groups);
            Assert.AreEqual(0, groups.Count);
        }

        [Test]
        public void CuttingThePillarDropsEverythingAboveTheCut()
        {
            var world = PillarAndBeam();
            var cut = new int3(0, 2, 0);
            world.SetBlock(cut, Block.Air);

            var groups = new List<List<int3>>();
            FlatGround().FindFloating(world, SeedsAround(cut), groups);

            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(2 + 6, groups[0].Count); // the two pillar blocks above the cut, plus the beam
            Assert.IsFalse(groups[0].Contains(new int3(0, 1, 0)));
            Assert.IsTrue(groups[0].Contains(new int3(5, 5, 0)));
        }

        [Test]
        public void BridgeHeldAtBothEndsSurvivesACutAtOneEnd()
        {
            var world = new BlockWorld();
            for (int y = 0; y < 3; y++)
            {
                world.SetBlock(new int3(0, y, 0), Stone);
                world.SetBlock(new int3(10, y, 0), Stone);
            }
            for (int x = 0; x <= 10; x++) world.SetBlock(new int3(x, 3, 0), Stone);

            // Cutting the left pillar leaves its top hanging from the bridge, which the right pillar still holds.
            var cut = new int3(0, 1, 0);
            world.SetBlock(cut, Block.Air);
            var groups = new List<List<int3>>();
            FlatGround().FindFloating(world, SeedsAround(cut), groups);
            Assert.AreEqual(0, groups.Count);

            // Cutting the right one too drops the whole span (the left stub at y = 2 hangs from it).
            var cut2 = new int3(10, 1, 0);
            world.SetBlock(cut2, Block.Air);
            FlatGround().FindFloating(world, SeedsAround(cut2), groups);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(11 + 2, groups[0].Count); // the 11-block span plus both pillar stubs at y = 2
        }

        [Test]
        public void SeparateLooseGroupsBecomeSeparatePieces()
        {
            var world = new BlockWorld();
            // Two blocks hanging in the air far apart: two separate pieces.
            world.SetBlock(new int3(0, 3, 0), Stone);
            world.SetBlock(new int3(20, 3, 0), Stone);

            var groups = new List<List<int3>>();
            var seeds = new List<int3> { new int3(0, 3, 0), new int3(20, 3, 0) };
            FlatGround().FindFloating(world, seeds, groups);
            Assert.AreEqual(2, groups.Count);
        }

        [Test]
        public void GroundCheckHasALittleSlackButNotAWholeBlock()
        {
            var flat = FlatGround();
            Assert.IsTrue(flat.IsGrounded(new int3(0, 0, 0)));  // bottom at 0 cm
            Assert.IsTrue(flat.IsGrounded(new int3(0, -3, 0))); // buried
            Assert.IsFalse(flat.IsGrounded(new int3(0, 1, 0))); // bottom 10 cm up: a real gap

            var almost = new StructureCheck((x, z) => 0.05f);
            Assert.IsTrue(almost.IsGrounded(new int3(0, 1, 0))); // 5 cm off: close enough to the smoothed surface
        }

        [Test]
        public void GroundCheckUsesTheGeneratedTerrain()
        {
            // The spawn clearing is flat at height 0.
            var check = new StructureCheck(new TerrainGenerator(1234).Height);
            Assert.IsTrue(check.IsGrounded(new int3(0, 0, 0)));
            Assert.IsFalse(check.IsGrounded(new int3(0, 20, 0)));
        }

        [Test]
        public void FacesOnlyHoldEachOtherUpWhenTheyShareArea()
        {
            const byte full = 0b1111;
            const byte missing3 = 0b0111; // triangle over corners 0, 1, 2
            const byte missing0 = 0b1110; // triangle over corners 1, 2, 3 (meets the one above only along the diagonal)
            const byte missing2 = 0b1011; // triangle over corners 0, 1, 3 (overlaps missing3)

            Assert.IsTrue(StructureCheck.FacesTouch(full, full));
            Assert.IsTrue(StructureCheck.FacesTouch(full, missing3));
            Assert.IsFalse(StructureCheck.FacesTouch(0, full));
            Assert.IsFalse(StructureCheck.FacesTouch(missing3, missing0));
            Assert.IsFalse(StructureCheck.FacesTouch(missing0, missing3));
            Assert.IsTrue(StructureCheck.FacesTouch(missing3, missing3));
            Assert.IsTrue(StructureCheck.FacesTouch(missing3, missing2));
        }

        [Test]
        public void BlocksMeetingOnlyAlongAnEdgeDoNotSupportEachOther()
        {
            var world = new BlockWorld();
            world.SetBlock(new int3(0, 0, 0), Stone); // on the ground

            // A wedge on top whose bottom face is a full square, but whose top is only an edge, so nothing
            // can rest on it: delete the top corners at x = 1 and the top corner (0, 1, 1).
            byte wedge = (byte)(CornerBits.All & ~(CornerBits.Bit(1, 1, 0) | CornerBits.Bit(1, 1, 1)));
            world.SetBlock(new int3(0, 1, 0), new Block { Material = BlockMaterials.Stone, Corners = wedge });
            world.SetBlock(new int3(0, 2, 0), Stone);

            // The wedge's top face is just the edge at x = 0, so the cube above has nothing to sit on.
            var groups = new List<List<int3>>();
            FlatGround().FindFloating(world, new List<int3> { new int3(0, 2, 0) }, groups);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(1, groups[0].Count);

            // The wedge itself sits flat on the cube below.
            groups.Clear();
            FlatGround().FindFloating(world, new List<int3> { new int3(0, 1, 0) }, groups);
            Assert.AreEqual(0, groups.Count);
        }

        [Test]
        public void ExtractLiftsTheBlocksIntoAPiece()
        {
            var world = new BlockWorld();
            var positions = new List<int3> { new int3(3, 7, -2), new int3(4, 7, -2), new int3(4, 8, -2) };
            var wood = Block.Solid(BlockMaterials.Wood);
            foreach (var p in positions) world.SetBlock(p, wood);

            BlockPiece piece = BlockPiece.Extract(world, positions, 42);

            Assert.IsNotNull(piece);
            Assert.AreEqual(42u, piece.Id);
            Assert.AreEqual(new int3(3, 7, -2), piece.Origin);
            Assert.AreEqual(new int3(2, 2, 1), piece.Size);
            Assert.AreEqual(wood, piece.Get(0, 0, 0));
            Assert.AreEqual(wood, piece.Get(1, 1, 0));
            Assert.IsTrue(piece.Get(0, 1, 0).IsAir);
            foreach (var p in positions) Assert.IsTrue(world.GetBlock(p).IsAir);
            Assert.AreEqual(0, world.ChunkCount);

            float oneBlock = BlockMaterials.Get(BlockMaterials.Wood).FullBlockMass;
            Assert.AreEqual(3f * oneBlock, piece.Mass(), 1e-4f);
            Assert.AreEqual(0.3f, piece.Position.x, 1e-5f); // origin in meters
        }

        [Test]
        public void PiecesTooBigToMoveStayInTheWorld()
        {
            var world = new BlockWorld();
            var positions = new List<int3> { new int3(0, 0, 0), new int3(BlockPiece.MaxSide, 0, 0) };
            foreach (var p in positions) world.SetBlock(p, Stone);
            Assert.IsNull(BlockPiece.Extract(world, positions, 1));
            Assert.IsFalse(world.GetBlock(positions[0]).IsAir);
        }

        [Test]
        public void RemoveFromClearsTheSameBlocksOnAnotherMachine()
        {
            var host = new BlockWorld();
            var client = new BlockWorld();
            var positions = new List<int3> { new int3(1, 1, 1), new int3(1, 2, 1) };
            foreach (var p in positions)
            {
                host.SetBlock(p, Stone);
                client.SetBlock(p, Stone);
            }
            client.SetBlock(new int3(1, 0, 1), Stone); // a neighbor that isn't part of the piece

            BlockPiece piece = BlockPiece.Extract(host, positions, 7);
            piece.RemoveFrom(client);

            foreach (var p in positions) Assert.IsTrue(client.GetBlock(p).IsAir);
            Assert.IsFalse(client.GetBlock(new int3(1, 0, 1)).IsAir);
        }

        [Test]
        public void CollisionBoxesCoverEveryBlockExactlyOnce()
        {
            var world = new BlockWorld();
            var positions = new List<int3>();
            // A 4 x 2 x 3 slab with an L-shaped tower on one corner.
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 3; z++)
            for (int x = 0; x < 4; x++)
                positions.Add(new int3(x, y, z));
            positions.Add(new int3(0, 2, 0));
            positions.Add(new int3(0, 3, 0));
            positions.Add(new int3(1, 3, 0));
            foreach (var p in positions) world.SetBlock(p, Stone);

            BlockPiece piece = BlockPiece.Extract(world, positions, 1);
            var boxes = new List<(int3 min, int3 max)>();
            Assert.IsTrue(piece.BuildBoxes(boxes, 96));
            Assert.LessOrEqual(boxes.Count, 4);

            var covered = new int[piece.Blocks.Length];
            foreach (var (min, max) in boxes)
            {
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                for (int x = min.x; x <= max.x; x++)
                    covered[piece.Index(x, y, z)]++;
            }
            for (int i = 0; i < covered.Length; i++)
                Assert.AreEqual(piece.Blocks[i].IsAir ? 0 : 1, covered[i], $"block {i}");
        }

        [Test]
        public void ASolidBoxNeedsOneCollider()
        {
            var world = new BlockWorld();
            var positions = new List<int3>();
            for (int y = 0; y < 3; y++)
            for (int z = 0; z < 5; z++)
            for (int x = 0; x < 7; x++)
                positions.Add(new int3(x, y, z));
            foreach (var p in positions) world.SetBlock(p, Stone);

            var boxes = new List<(int3 min, int3 max)>();
            Assert.IsTrue(BlockPiece.Extract(world, positions, 1).BuildBoxes(boxes, 96));
            Assert.AreEqual(1, boxes.Count);
            Assert.AreEqual(new int3(0, 0, 0), boxes[0].min);
            Assert.AreEqual(new int3(6, 2, 4), boxes[0].max);
        }

        [Test]
        public void TooManyBoxesIsReported()
        {
            var world = new BlockWorld();
            var positions = new List<int3>();
            // A checkerboard can't be merged at all: 18 separate blocks.
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                if ((x + z) % 2 == 0) positions.Add(new int3(x, 0, z));
            foreach (var p in positions) world.SetBlock(p, Stone);

            var boxes = new List<(int3 min, int3 max)>();
            Assert.IsFalse(BlockPiece.Extract(world, positions, 1).BuildBoxes(boxes, 10));
        }

        [Test]
        public void PieceBlocksSurviveTheNetworkEncoding()
        {
            var world = new BlockWorld();
            var positions = new List<int3>();
            byte slope = (byte)(CornerBits.All & ~CornerBits.Bit(1, 1, 1));
            for (int x = 0; x < 9; x++)
            {
                var p = new int3(x, 0, x % 3);
                positions.Add(p);
                world.SetBlock(p, new Block { Material = (ushort)(1 + x % 8), Corners = x % 2 == 0 ? CornerBits.All : slope });
            }
            BlockPiece piece = BlockPiece.Extract(world, positions, 3);

            Block[] decoded = BlockPiece.DecodeBlocks(piece.EncodeBlocks(), piece.Size);
            Assert.AreEqual(piece.Blocks.Length, decoded.Length);
            for (int i = 0; i < decoded.Length; i++) Assert.AreEqual(piece.Blocks[i], decoded[i]);
        }

        [Test]
        public void BlockRunEncodingRoundTrips()
        {
            var blocks = new Block[1000];
            for (int i = 300; i < 340; i++) blocks[i] = Block.Solid(BlockMaterials.Brick);
            blocks[999] = new Block { Material = BlockMaterials.Iron, Corners = (byte)(CornerBits.All & ~CornerBits.Bit(0, 1, 0)) };

            Block[] decoded = ChunkSerializer.DecodeBlocks(ChunkSerializer.EncodeBlocks(blocks), blocks.Length);
            CollectionAssert.AreEqual(blocks, decoded);
        }
    }
}
