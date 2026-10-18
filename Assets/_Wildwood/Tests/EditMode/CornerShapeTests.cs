using NUnit.Framework;
using Wildwood.Core.Blocks;

namespace Wildwood.Core.Tests
{
    public class CornerShapeTests
    {
        static byte Without(params (int x, int y, int z)[] corners)
        {
            byte mask = CornerBits.All;
            foreach (var c in corners) mask = (byte)(mask & ~CornerBits.Bit(c.x, c.y, c.z));
            return mask;
        }

        static byte Only(params (int x, int y, int z)[] corners)
        {
            byte mask = 0;
            foreach (var c in corners) mask |= CornerBits.Bit(c.x, c.y, c.z);
            return mask;
        }

        [Test]
        public void ExactlyOneHundredFiftyOneMasksAreSolid()
        {
            int solid = 0;
            for (int mask = 0; mask < 256; mask++)
                if (CornerShapes.IsSolid((byte)mask)) solid++;
            Assert.AreEqual(151, solid);
        }

        [Test]
        public void FullCubeHasTwelveTrianglesAndVolumeOne()
        {
            var shape = CornerShapes.Get(CornerBits.All);
            int triangles = 0;
            for (int f = 0; f < 7; f++) triangles += shape.Triangles[f].Length / 3;
            Assert.AreEqual(12, triangles);
            Assert.AreEqual(0, shape.Triangles[BlockFaces.Slanted].Length);
            Assert.AreEqual(1f, shape.Volume, 1e-4f);
            for (int f = 0; f < 6; f++) Assert.AreEqual(15, shape.FaceCoverage[f]);
        }

        [Test]
        public void CutCornerIsFiveSixthsOfABlock()
        {
            var shape = CornerShapes.Get(Without((1, 1, 0)));
            Assert.AreEqual(5f / 6f, shape.Volume, 1e-4f);
            Assert.AreEqual(1, shape.Triangles[BlockFaces.Slanted].Length / 3);
        }

        [Test]
        public void SlopeIsHalfABlock()
        {
            var shape = CornerShapes.Get(Without((0, 1, 0), (1, 1, 0)));
            Assert.AreEqual(0.5f, shape.Volume, 1e-4f);
            Assert.AreEqual(0, shape.FaceCoverage[BlockFaces.PosY], "a slope has no flat top");
            Assert.AreEqual(15, shape.FaceCoverage[BlockFaces.NegY], "but a full flat bottom");
        }

        [Test]
        public void SpikeIsOneSixthOfABlock()
        {
            var shape = CornerShapes.Get(Only((0, 0, 1), (1, 0, 0), (1, 0, 1), (1, 1, 1)));
            Assert.AreEqual(1f / 6f, shape.Volume, 1e-4f);
        }

        [Test]
        public void FlatOrTinyMasksAreEmpty()
        {
            Assert.IsTrue(CornerShapes.Get(0).IsEmpty);
            Assert.IsTrue(CornerShapes.Get(Only((0, 0, 0), (1, 0, 0), (0, 1, 0))).IsEmpty, "three corners");
            Assert.IsTrue(CornerShapes.Get(Only((0, 0, 0), (1, 0, 0), (0, 0, 1), (1, 0, 1))).IsEmpty, "flat square");
        }

        [Test]
        public void EveryShapeIsWoundOutward()
        {
            // For a closed shape with outward-facing triangles, the volume comes out positive.
            for (int mask = 0; mask < 256; mask++)
            {
                var shape = CornerShapes.Get((byte)mask);
                if (shape.IsEmpty) continue;
                Assert.Greater(shape.Volume, 0.1f, $"mask 0x{mask:X2}");
                Assert.LessOrEqual(shape.Volume, 1f + 1e-4f, $"mask 0x{mask:X2}");
            }
        }
    }
}
