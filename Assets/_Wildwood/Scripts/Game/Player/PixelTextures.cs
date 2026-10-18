using System.Collections.Generic;
using UnityEngine;

namespace Wildwood.Game.Player
{
    /// <summary>
    /// Makes the tiny, crunchy pixel textures the characters wear (32 x 32, no smoothing), so the low-poly
    /// shapes get that hand-painted pixel look. Textures are cached and shared between players with the same colors.
    /// </summary>
    public static class PixelTextures
    {
        public enum Kind
        {
            Cloth,
            Trousers,
            Leather,
            Skin,
            Hair,
            Eyes,
            Metal,
        }

        const int Size = 32;

        static readonly Dictionary<(Kind, Color32), Texture2D> Cache = new Dictionary<(Kind, Color32), Texture2D>();

        public static Texture2D Get(Kind kind, Color color)
        {
            Color32 key = color;
            if (Cache.TryGetValue((kind, key), out var cached) && cached != null) return cached;

            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = $"Pixel {kind}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            var random = new System.Random(kind.GetHashCode() * 7919 + key.r * 31 + key.g * 17 + key.b);
            var pixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                pixels[y * Size + x] = Pixel(kind, color, x, y, random);
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            Cache[(kind, key)] = texture;
            return texture;
        }

        static Color Pixel(Kind kind, Color baseColor, int x, int y, System.Random random)
        {
            float noise = (float)random.NextDouble();
            switch (kind)
            {
                case Kind.Cloth:
                {
                    // Woven wool: blotchy shades, a faint vertical weave, and a woven checker band.
                    float shade = 0.82f + noise * 0.28f - ((x % 2 == 0) ? 0.04f : 0f);
                    bool band = y >= 26 && y <= 29;
                    if (band)
                    {
                        bool light = ((x / 2) + (y / 2)) % 2 == 0;
                        return Tint(baseColor, light ? 1.35f : 0.6f) * (0.95f + noise * 0.1f);
                    }
                    if (y == 25 || y == 30) return Tint(baseColor, 0.55f);
                    return Tint(baseColor, shade);
                }
                case Kind.Trousers:
                    return Tint(baseColor, 0.85f + noise * 0.25f - ((y % 3 == 0) ? 0.05f : 0f));
                case Kind.Leather:
                {
                    float shade = 0.8f + noise * 0.3f;
                    if (y % 16 == 0 && x % 3 != 2) shade = 1.35f; // stitching
                    return Tint(baseColor, shade);
                }
                case Kind.Skin:
                    return Tint(baseColor, 0.94f + noise * 0.1f);
                case Kind.Hair:
                {
                    // Vertical strands: each column gets its own shade, plus a little speckle.
                    float strand = (float)new System.Random(x * 131 + 7).NextDouble();
                    return Tint(baseColor, 0.72f + strand * 0.4f + noise * 0.12f);
                }
                case Kind.Metal:
                    return Tint(baseColor, 0.8f + noise * 0.45f);
                default:
                    return baseColor;
            }
        }

        static Color Tint(Color color, float amount)
        {
            return new Color(
                Mathf.Clamp01(color.r * amount),
                Mathf.Clamp01(color.g * amount),
                Mathf.Clamp01(color.b * amount),
                1f);
        }
    }
}
