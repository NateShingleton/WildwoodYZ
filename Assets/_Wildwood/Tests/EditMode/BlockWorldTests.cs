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
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, CornerBits.Bit(0, 1, 0)));
            BlockEditor.Apply(world, BlockEdit.Chisel(int3.zero, CornerBits.Bit(1, 1, 0)));
            Assert.AreEqual(0.5f, CornerShapes.Get(world.GetBlock(int3.zero).Corners).Volume, 1e-4f);
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
