using System;
using System.IO;
using UnityEngine;
using Wildwood.Core.Blocks;

namespace Wildwood.Game.Persistence
{
    /// <summary>Choices made in the menu before hosting.</summary>
    public static class WorldOptions
    {
        /// <summary>Set by the menu's "Host new world" button. Read (and reset) when the host session starts.</summary>
        public static bool NewWorldRequested;

        public static bool ConsumeNewWorldRequest()
        {
            bool requested = NewWorldRequested;
            NewWorldRequested = false;
            return requested;
        }
    }

    /// <summary>
    /// Reads and writes the host's world file. Only the host saves; clients always get the world from the host.
    /// The file lives in Unity's per-user data folder (logged when saving), e.g.
    /// C:\Users\you\AppData\LocalLow\ShingIndustries\Wildwood\Worlds\sandbox.wwworld
    /// </summary>
    public static class WorldStorage
    {
        const string WorldName = "sandbox";

        public static string FilePath =>
            Path.Combine(Application.persistentDataPath, "Worlds", WorldName + ".wwworld");

        public static bool HasSave => File.Exists(FilePath);

        public static bool TrySave(BlockWorld world, int seed)
        {
            string path = FilePath;
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Write to a temporary file first, so a crash mid-save never corrupts the real one.
                using (var stream = File.Create(temporary))
                {
                    WorldSaveFormat.Write(stream, seed, world);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Wildwood] Couldn't save the world to {path}: {exception.Message}");
                return false;
            }
        }

        public static bool TryLoad(BlockWorld world, out int seed)
        {
            seed = 0;
            string path = FilePath;
            if (!File.Exists(path)) return false;
            try
            {
                using var stream = File.OpenRead(path);
                seed = WorldSaveFormat.Read(stream, world);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Wildwood] Couldn't load the world from {path}: {exception.Message}. Starting a new one.");
                world.Clear();
                return false;
            }
        }
    }
}
