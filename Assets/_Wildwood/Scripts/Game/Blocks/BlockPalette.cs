using UnityEngine;
using Wildwood.Core.Blocks;
using Wildwood.Core.Meshing;

namespace Wildwood.Game.Blocks
{
    /// <summary>
    /// Builds the tiny color texture block meshes read from: one row per material, 8 shades per row.
    /// One shared texture means every chunk draws with one material in one draw call.
    /// </summary>
    public static class BlockPalette
    {
        /// <summary>Rows in the palette. Material ids at or above this share the last row.</summary>
        public const int Rows = 16;

        public static Texture2D CreateTexture()
        {
            int shades = ChunkMesher.PaletteShades;
            var texture = new Texture2D(shades, Rows, TextureFormat.RGBA32, false)
            {
                name = "Block Palette",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            for (int row = 0; row < Rows; row++)
            {
                Color baseColor = row < BlockMaterials.Count && row > 0
                    ? (Color)BlockMaterials.Get((ushort)row).Color
                    : new Color(1f, 0f, 1f); // magenta = a material without a color yet
                for (int shade = 0; shade < shades; shade++)
                {
                    // Brightness from about 90% to 108%, with a hint of warmth on the lighter shades.
                    float t = shade / (float)(shades - 1);
                    float brightness = Mathf.Lerp(0.90f, 1.08f, t);
                    Color c = baseColor * brightness;
                    c.r += 0.012f * t;
                    c.b -= 0.012f * t;
                    c.a = 1f;
                    texture.SetPixel(shade, row, c);
                }
            }
            texture.Apply(false, true);
            return texture;
        }
    }
}
