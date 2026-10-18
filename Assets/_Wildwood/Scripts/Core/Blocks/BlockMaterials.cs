using UnityEngine;

namespace Wildwood.Core.Blocks
{
    /// <summary>Static data for one block material.</summary>
    public readonly struct BlockMaterialInfo
    {
        public readonly ushort Id;
        public readonly string Name;

        /// <summary>Base color (sRGB). Each block gets a slight random shade of it.</summary>
        public readonly Color32 Color;

        /// <summary>Real-world density in kg per cubic meter. A full 10 cm block weighs Density / 1000 kg.</summary>
        public readonly float Density;

        public BlockMaterialInfo(ushort id, string name, Color32 color, float density)
        {
            Id = id;
            Name = name;
            Color = color;
            Density = density;
        }

        /// <summary>Mass of one full block of this material, in kg.</summary>
        public float FullBlockMass => Density * BlockConstants.BlockSize * BlockConstants.BlockSize * BlockConstants.BlockSize;
    }

    /// <summary>
    /// Every block material. Ids are part of the save and network format: never reorder or reuse them,
    /// only add new ones at the end. Strength values arrive with the weight-and-strength step.
    /// </summary>
    public static class BlockMaterials
    {
        public const ushort Air = 0;
        public const ushort Stone = 1;
        public const ushort Wood = 2;
        public const ushort Clay = 3;
        public const ushort Brick = 4;
        public const ushort Iron = 5;
        public const ushort Moss = 6;
        public const ushort Sandstone = 7;
        public const ushort Plaster = 8;

        static readonly BlockMaterialInfo[] All =
        {
            new BlockMaterialInfo(Air, "Air", new Color32(0, 0, 0, 0), 0f),
            new BlockMaterialInfo(Stone, "Stone", new Color32(158, 156, 148, 255), 2600f),
            new BlockMaterialInfo(Wood, "Wood", new Color32(168, 115, 64, 255), 600f),
            new BlockMaterialInfo(Clay, "Clay", new Color32(184, 128, 92, 255), 1800f),
            new BlockMaterialInfo(Brick, "Brick", new Color32(173, 77, 56, 255), 1900f),
            new BlockMaterialInfo(Iron, "Iron", new Color32(122, 128, 135, 255), 7800f),
            new BlockMaterialInfo(Moss, "Moss", new Color32(102, 143, 61, 255), 1300f),
            new BlockMaterialInfo(Sandstone, "Sandstone", new Color32(224, 194, 135, 255), 2300f),
            new BlockMaterialInfo(Plaster, "Plaster", new Color32(235, 230, 214, 255), 1200f),
        };

        /// <summary>Number of ids in use, including air.</summary>
        public static int Count => All.Length;

        /// <summary>True for any real (non-air) material.</summary>
        public static bool IsValid(ushort id) => id > 0 && id < All.Length;

        public static BlockMaterialInfo Get(ushort id) => id < All.Length ? All[id] : All[0];
    }
}
