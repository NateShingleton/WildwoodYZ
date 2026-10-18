using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Wildwood.Core.Blocks;
using Wildwood.Core.Meshing;

namespace Wildwood.Core.Tests
{
    public class BlockWorldTests
    {
        [Test]
        public void BlocksWorkAtNegativeCoordinates()
        {
            var world = new BlockWorld();
            var p = new int3(-1, -33, -64);
            Assert.IsTrue(world.SetBlock(p, Block.Solid(BlockMaterials.Stone)));
            Assert.AreEqual(Block.Solid(BlockMaterials.Stone), world.GetBlock(p));
            Assert.IsTrue(world.GetBlock(new int3(0, -33, -64)).IsAir);
            Assert.IsTrue(world.TryGetChunk(new int3(-1, -2, -2), out _));
        }

        [Test]
        public void RemovingTheLastBlockDropsTheChunk()
        {
            var world = new BlockWorld();
            var p = new int3(5, 5, 5);
            world.SetBlock(p, Block.Solid(BlockMaterials.Wood));
            Assert.AreEqual(1, world.ChunkCount);
            world.SetBlock(p, Block.Air);
            Assert.AreEqual(0, world.ChunkCount);
        }

        [Test]
        public void UnknownMaterialsAndFlatShapesBecomeAir()
        {
            var world = new BlockWorld();
            Assert.IsFalse(world.SetBlock(int3.zero, new Block { Material = 999, Corners = CornerBits.All }));
            var flat = new Block { Material = BlockMaterials.Stone, Corners = (byte)(CornerBits.Bit(0, 0, 0) | CornerBits.Bit(1, 0, 0) | CornerBits.Bit(0, 0, 1) | CornerBits.Bit(1, 0, 1)) };
            Assert.IsFalse(world.SetBlock(int3.zero, flat));
            Assert.AreEqual(0, world.ChunkCount);
        }

        [Test]
        public void EditingABorderBlockAlsoDirtiesTheNeighborChunk()
        {
            var world = new BlockWorld();
            var dirty = new List<int3>();
            world.SetBlock(new int3(31, 0, 0), Block.Solid(BlockMaterials.Stone));
            world.TakeDirty(dirty);
            CollectionAssert.Contains(dirty, new int3(0, 0, 0));
            CollectionAssert.Contains(dirty, new int3(1, 0, 0));
        }

        [Test]
        public void FillOnlyAirKeepsExistingBlocks()
        {
            var world = new BlockWorld();
            world.SetBlock(new int3(1, 1, 1), Block.Solid(BlockMaterials.Brick));
            int changed = BlockEditor.Apply(world, BlockEdit.FillBox(int3.zero, new int3(2, 2, 2), Block.Solid(BlockMaterials.Stone), FillMode.OnlyAir));
            Assert.AreEqual(26, changed);
            Assert.AreEqual(BlockMaterials.Brick, world.GetBlock(new int3(1, 1, 1)).Material);
        }

        [Test]
        public void ChiselingAnEdgeMakesASlope()
        {
            var world = new BlockWorld();
            world.SetBlock(int3.zero, Block.Solid(BlockMaterials.Stone));
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 1, CornerBits.Bit(0, 1, 0)));
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 1, CornerBits.Bit(1, 1, 0)));
            Assert.AreEqual(0.5f, CornerShapes.Get(world.GetBlock(int3.zero).Corners).Volume, 1e-4f);
        }

        static float TotalVolume(BlockWorld world)
        {
            float total = 0f;
            foreach (var chunk in world.Chunks)
                foreach (var block in chunk.RawBlocks)
                    if (!block.IsAir) total += CornerShapes.Get(block.Corners).Volume;
            return total;
        }

        [Test]
        public void BigChiselCutsTheCornerOffAWholeBrushRegion()
        {
            var world = new BlockWorld();
            BlockEditor.Apply(world, BlockEdit.FillBox(int3.zero, new int3(3, 3, 3), Block.Solid(BlockMaterials.Stone), FillMode.Replace));
            Assert.AreEqual(64f, TotalVolume(world), 1e-3f);

            // Deleting one corner of the 4x4x4 group removes a corner slice worth 1/6 of it.
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 4, CornerBits.Bit(1, 1, 1)));
            Assert.AreEqual(64f * 5f / 6f, TotalVolume(world), 1e-3f);

            // Deleting the corner next to it (sharing the top edge) leaves a clean slope: exactly half.
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 4, CornerBits.Bit(0, 1, 1)));
            Assert.AreEqual(32f, TotalVolume(world), 1e-3f);
            Assert.AreEqual((byte)(CornerBits.All & ~CornerBits.Bit(0, 1, 1) & ~CornerBits.Bit(1, 1, 1)),
                BlockEditor.RegionCorners(world, int3.zero, 4));
        }

        [Test]
        public void ChiselingAnAlreadyMissingCornerDoesNothing()
        {
            var world = new BlockWorld();
            BlockEditor.Apply(world, BlockEdit.FillBox(int3.zero, new int3(1, 1, 1), Block.Solid(BlockMaterials.Stone), FillMode.Replace));
            Assert.Greater(BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 2, CornerBits.Bit(0, 0, 0))), 0);
            Assert.AreEqual(0, BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 2, CornerBits.Bit(0, 0, 0))));
        }

        [Test]
        public void ChiselRegionsMustBeCubes()
        {
            var edit = BlockEdit.Chisel(int3.zero, 2, CornerBits.Bit(0, 0, 0));
            edit.Max = new int3(1, 2, 1);
            Assert.IsFalse(BlockEditor.IsValid(edit));
        }

        [Test]
        public void UndoPutsBackExactlyWhatAnEditChanged()
        {
            var world = new BlockWorld();
            var undo = new UndoHistory();
            var changes = new List<BlockChange>();
            const ulong player = 7;

            world.SetBlock(new int3(1, 0, 0), Block.Solid(BlockMaterials.Brick)); // already there before the edit
            BlockEditor.Apply(world, BlockEdit.FillBox(int3.zero, new int3(3, 0, 0), Block.Solid(BlockMaterials.Wood), FillMode.OnlyAir), changes);
            Assert.AreEqual(3, changes.Count, "the brick wasn't overwritten, so it isn't part of the edit");
            undo.Record(player, changes);

            var touched = new HashSet<int3>();
            Assert.AreEqual(3, undo.Undo(world, player, touched));
            Assert.IsTrue(world.GetBlock(int3.zero).IsAir);
            Assert.AreEqual(BlockMaterials.Brick, world.GetBlock(new int3(1, 0, 0)).Material, "the older brick stays");
            CollectionAssert.Contains(touched, int3.zero);
            Assert.AreEqual(0, undo.Undo(world, player, touched), "nothing left to undo");
        }

        [Test]
        public void UndoLeavesOtherPlayersLaterWorkAlone()
        {
            var world = new BlockWorld();
            var undo = new UndoHistory();
            var changes = new List<BlockChange>();

            BlockEditor.Apply(world, BlockEdit.FillBox(int3.zero, new int3(1, 0, 0), Block.Solid(BlockMaterials.Stone), FillMode.Replace), changes);
            undo.Record(1, changes);

            // Player 2 then paints one of those blocks brick.
            world.SetBlock(int3.zero, Block.Solid(BlockMaterials.Brick));

            undo.Undo(world, 1, new HashSet<int3>());
            Assert.AreEqual(BlockMaterials.Brick, world.GetBlock(int3.zero).Material, "player 2's block survives");
            Assert.IsTrue(world.GetBlock(new int3(1, 0, 0)).IsAir, "player 1's untouched block is undone");
        }

        [Test]
        public void UndoCoversChiselsToo()
        {
            var world = new BlockWorld();
            var undo = new UndoHistory();
            var changes = new List<BlockChange>();
            world.SetBlock(int3.zero, Block.Solid(BlockMaterials.Stone));
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, 1, CornerBits.Bit(1, 1, 1)), changes);
            undo.Record(3, changes);
            undo.Undo(world, 3, new HashSet<int3>());
            Assert.IsTrue(world.GetBlock(int3.zero).IsFullCube);
        }

        [Test]
        public void BigDragBoxesAreAllowedButNotHugeOnes()
        {
            var wall = BlockEdit.FillBox(int3.zero, new int3(127, 29, 0), Block.Solid(BlockMaterials.Brick), FillMode.OnlyAir);
            Assert.IsTrue(BlockEditor.IsValid(wall), "a 12.8 m x 3 m wall");
            var tooLong = BlockEdit.FillBox(int3.zero, new int3(128, 0, 0), Block.Solid(BlockMaterials.Brick), FillMode.OnlyAir);
            Assert.IsFalse(BlockEditor.IsValid(tooLong));
            var tooMuch = BlockEdit.FillBox(int3.zero, new int3(127, 127, 127), Block.Solid(BlockMaterials.Brick), FillMode.OnlyAir);
            Assert.IsFalse(BlockEditor.IsValid(tooMuch));
        }

        [Test]
        public void WorldFilesRoundTrip()
        {
            var source = new BlockWorld();
            DemoStructures.Build(source);
            var stream = new System.IO.MemoryStream();
            WorldSaveFormat.Write(stream, 12345, source);
            stream.Position = 0;

            var loaded = new BlockWorld();
            int seed = WorldSaveFormat.Read(stream, loaded);
            Assert.AreEqual(12345, seed);
            Assert.AreEqual(source.ChunkCount, loaded.ChunkCount);
            Assert.AreEqual(TotalVolume(source), TotalVolume(loaded), 1e-3f);
        }

        [Test]
        public void OversizedEditsAreRejected()
        {
            var edit = BlockEdit.FillBox(int3.zero, new int3(BlockEditor.MaxEditSize, 0, 0), Block.Solid(BlockMaterials.Stone), FillMode.Replace);
            Assert.IsFalse(BlockEditor.IsValid(edit));
            Assert.AreEqual(0, BlockEditor.Apply(new BlockWorld(), edit));
        }

        [Test]
        public void ChunksSurviveASaveAndLoadRoundTrip()
        {
            var source = new BlockWorld();
            DemoStructures.Build(source);
            var copy = new BlockWorld();
            foreach (var chunk in source.Chunks)
                copy.LoadChunk(chunk.Coord, ChunkSerializer.Decode(ChunkSerializer.Encode(chunk)));

            Assert.AreEqual(source.ChunkCount, copy.ChunkCount);
            foreach (var chunk in source.Chunks)
            {
                Assert.IsTrue(copy.TryGetChunk(chunk.Coord, out var loaded));
                Assert.AreEqual(chunk.SolidCount, loaded.SolidCount);
                CollectionAssert.AreEqual(chunk.RawBlocks, loaded.RawBlocks);
            }
        }

        [Test]
        public void TouchingCubesHideTheFaceBetweenThem()
        {
            var world = new BlockWorld();
            var buffers = new MeshBuffers();

            world.SetBlock(int3.zero, Block.Solid(BlockMaterials.Stone));
            ChunkMesher.Build(world, int3.zero, buffers, 16);
            Assert.AreEqual(12 * 3, buffers.Indices.Count, "one cube = 12 triangles");

            world.SetBlock(new int3(1, 0, 0), Block.Solid(BlockMaterials.Stone));
            ChunkMesher.Build(world, int3.zero, buffers, 16);
            Assert.AreEqual(20 * 3, buffers.Indices.Count, "two cubes = 24 triangles minus the 4 between them");
        }

        [Test]
        public void ASlopeOnlyHidesTheMatchingTriangleOfItsNeighbor()
        {
            var world = new BlockWorld();
            var buffers = new MeshBuffers();
            // A slope rising toward +Z next to a cube on its +X side. The slope's +X side is a triangle,
            // so it's hidden by the cube, but the cube's -X square is only half covered and stays visible.
            byte slope = (byte)(CornerBits.All & ~CornerBits.Bit(0, 1, 0) & ~CornerBits.Bit(1, 1, 0));
            world.SetBlock(int3.zero, new Block { Material = BlockMaterials.Stone, Corners = slope });
            world.SetBlock(new int3(1, 0, 0), Block.Solid(BlockMaterials.Stone));
            ChunkMesher.Build(world, int3.zero, buffers, 16);
            // Slope: 8 triangles alone, minus 1 hidden side = 7. Cube: 12 minus nothing = 12.
            Assert.AreEqual((7 + 12) * 3, buffers.Indices.Count);
        }
    }
}
