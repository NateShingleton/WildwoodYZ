using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Wildwood.Core.Meshing;
using Wildwood.Core.Terrain;

namespace Wildwood.Core.Tests
{
    public class TerrainTests
    {
        [Test]
        public void SameSeedGivesTheSameWorld()
        {
            var a = new TerrainGenerator(42);
            var b = new TerrainGenerator(42);
            for (int i = 0; i < 50; i++)
            {
                float x = i * 37.3f - 900f, z = i * -21.7f + 400f;
                Assert.AreEqual(a.Height(x, z), b.Height(x, z));
                Assert.AreEqual(a.Forest(x, z), b.Forest(x, z));
            }
        }

        [Test]
        public void DifferentSeedsGiveDifferentWorlds()
        {
            var a = new TerrainGenerator(1);
            var b = new TerrainGenerator(2);
            int different = 0;
            for (int i = 0; i < 50; i++)
            {
                float x = 200f + i * 13f, z = -300f + i * 7f;
                if (math.abs(a.Height(x, z) - b.Height(x, z)) > 0.01f) different++;
            }
            Assert.Greater(different, 40);
        }

        [Test]
        public void SpawnClearingIsFlatAtZero()
        {
            var generator = new TerrainGenerator(777);
            Assert.AreEqual(0f, generator.Height(0f, 0f), 1e-4f);
            Assert.AreEqual(0f, generator.Height(15f, -12f), 1e-4f);
        }

        [Test]
        public void GroundMeshAppearsOnlyWhereTheSurfaceIs()
        {
            var generator = new TerrainGenerator(5);
            var mesher = new TerrainMesher();
            var buffers = new MeshBuffers();

            // The flat clearing sits at y = 0, the boundary between chunk layers -1 and 0.
            Assert.IsTrue(mesher.Build(generator, new int3(0, -1, 0), buffers), "ground under spawn");
            Assert.IsFalse(mesher.Build(generator, new int3(0, 1, 0), buffers), "nothing high in the sky");
            Assert.IsFalse(mesher.Build(generator, new int3(0, -2, 0), buffers), "nothing deep underground");

            // Near spawn the ground is flat, so normals point up.
            Assert.IsTrue(mesher.Build(generator, new int3(0, -1, 0), buffers));
            float up = 0f;
            foreach (var normal in buffers.Normals) up += normal.y;
            Assert.Greater(up / buffers.Normals.Count, 0.95f);
        }

        [Test]
        public void TreesAreDeterministicAndKeepOutOfTheClearing()
        {
            var generator = new TerrainGenerator(99);
            var first = new List<TreeSpawn>();
            var second = new List<TreeSpawn>();
            generator.GetTrees(new float2(-200f, -200f), 400f, first);
            generator.GetTrees(new float2(-200f, -200f), 400f, second);

            Assert.Greater(first.Count, 50, "the forest east of spawn should have plenty of trees");
            Assert.AreEqual(first.Count, second.Count);
            for (int i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(first[i].Position, second[i].Position);
                Assert.Greater(math.length(first[i].Position.xz), TerrainSettings.SpawnClearingRadius);
            }
        }
    }
}
