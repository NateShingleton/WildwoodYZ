using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Wildwood.Core.Blocks;

namespace Wildwood.Core.Tests
{
    public class BlockTests
    {
        [Test]
        public void DefaultBlockIsAir()
        {
            Assert.IsTrue(Block.Air.IsAir);
            Assert.IsTrue(default(Block).IsAir);
        }

        [Test]
        public void SolidBlockIsAFullCube()
        {
            var block = Block.Solid(1);
            Assert.IsTrue(block.IsFullCube);
            Assert.AreEqual(8, CornerBits.Count(block.Corners));
        }

        [Test]
        public void EveryCornerHasItsOwnBit()
        {
            int seen = 0;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                int bit = CornerBits.Bit(x, y, z);
                Assert.AreEqual(0, seen & bit, $"Corner ({x},{y},{z}) shares a bit with another corner");
                seen |= bit;
            }
            Assert.AreEqual(0xFF, seen);
        }

        [Test]
        public void DeletingOneCornerLeavesSeven()
        {
            var block = Block.Solid(1).WithoutCorner(1, 1, 0);
            Assert.AreEqual(7, CornerBits.Count(block.Corners));
            Assert.IsFalse(CornerBits.Has(block.Corners, 1, 1, 0));
            Assert.IsFalse(block.IsFullCube);
            Assert.IsFalse(block.IsAir);
        }

        [Test]
        public void DeletingAnEdgeMakesASlope()
        {
            // Deleting the front-top edge removes its two corners, leaving the 6 corners of a wedge.
            var block = Block.Solid(1).WithoutCorner(0, 1, 0).WithoutCorner(1, 1, 0);
            Assert.AreEqual(6, CornerBits.Count(block.Corners));
        }

        [Test]
        public void DeletingEveryCornerMakesAir()
        {
            var block = Block.Solid(3);
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
                block = block.WithoutCorner(x, y, z);
            Assert.IsTrue(block.IsAir);
        }

        [Test]
        public void BlockStaysFourBytes()
        {
            // The block layout is also the save and network format. If this fails, that format changed.
            Assert.AreEqual(4, UnsafeUtility.SizeOf<Block>());
        }
    }
}
