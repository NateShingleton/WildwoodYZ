using UnityEngine;

namespace Wildwood.Game.Terrain
{
    /// <summary>
    /// Small generated color textures for the ground, trees and grass. Meshes pick colors with their UVs,
    /// so everything draws with stock URP materials. Tweak the colors here to change the look of the world.
    /// </summary>
    public static class NaturePalettes
    {
        // Ground: left to right goes from open plains (golden) to deep forest (green);
        // bottom to top goes from flat grass to steep dirt and rock.
        static readonly Color PlainsGrass = new Color(0.80f, 0.64f, 0.30f);
        static readonly Color MeadowGrass = new Color(0.60f, 0.62f, 0.27f);
        static readonly Color ForestFloor = new Color(0.33f, 0.45f, 0.20f);
        static readonly Color Dirt = new Color(0.47f, 0.35f, 0.24f);
        static readonly Color Rock = new Color(0.52f, 0.50f, 0.46f);

        public static Texture2D CreateGroundTexture()
        {
            const int width = 16, height = 4;
            var texture = NewTexture("Ground Palette", width, height, FilterMode.Bilinear);
            for (int x = 0; x < width; x++)
            {
                float forest = x / (float)(width - 1);
                Color grass = forest < 0.5f
                    ? Color.Lerp(PlainsGrass, MeadowGrass, forest * 2f)
                    : Color.Lerp(MeadowGrass, ForestFloor, (forest - 0.5f) * 2f);
                texture.SetPixel(x, 0, grass);
                texture.SetPixel(x, 1, Color.Lerp(grass, Dirt, 0.35f));
                texture.SetPixel(x, 2, Dirt);
                texture.SetPixel(x, 3, Rock);
            }
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>Rows of the tree palette.</summary>
        public const int TreeShades = 8;
        public const int TreeRows = 4;
        public const int TrunkRow = 0, LeafRow = 1, PineRow = 2, GoldenLeafRow = 3;

        public static Texture2D CreateTreeTexture()
        {
            var texture = NewTexture("Tree Palette", TreeShades, TreeRows, FilterMode.Point);
            for (int shade = 0; shade < TreeShades; shade++)
            {
                float t = shade / (float)(TreeShades - 1);
                texture.SetPixel(shade, TrunkRow, Color.Lerp(new Color(0.30f, 0.21f, 0.15f), new Color(0.45f, 0.33f, 0.23f), t));
                texture.SetPixel(shade, LeafRow, Color.Lerp(new Color(0.30f, 0.48f, 0.16f), new Color(0.56f, 0.66f, 0.24f), t));
                texture.SetPixel(shade, PineRow, Color.Lerp(new Color(0.14f, 0.28f, 0.17f), new Color(0.26f, 0.42f, 0.24f), t));
                texture.SetPixel(shade, GoldenLeafRow, Color.Lerp(new Color(0.78f, 0.52f, 0.16f), new Color(0.95f, 0.76f, 0.30f), t));
            }
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>UV for a tree palette cell.</summary>
        public static Vector2 TreeUV(int row, int shade) =>
            new Vector2((shade + 0.5f) / TreeShades, (row + 0.5f) / TreeRows);

        /// <summary>Grass: column 0 = golden plains grass, column 1 = green forest grass; bottom of a blade to its tip.</summary>
        public static Texture2D CreateGrassTexture()
        {
            const int height = 8;
            var texture = NewTexture("Grass Palette", 2, height, FilterMode.Bilinear);
            for (int y = 0; y < height; y++)
            {
                float t = y / (float)(height - 1);
                texture.SetPixel(0, y, Color.Lerp(new Color(0.55f, 0.40f, 0.17f), new Color(1.00f, 0.85f, 0.46f), t));
                texture.SetPixel(1, y, Color.Lerp(new Color(0.20f, 0.32f, 0.13f), new Color(0.52f, 0.66f, 0.27f), t));
            }
            texture.Apply(false, true);
            return texture;
        }

        public const float GrassPlainsU = 0.25f;
        public const float GrassForestU = 0.75f;

        static Texture2D NewTexture(string name, int width, int height, FilterMode filter)
        {
            return new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
            };
        }
    }
}
