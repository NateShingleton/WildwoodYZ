using System.Collections.Generic;
using Unity.Mathematics;

namespace Wildwood.Core.Blocks
{
    /// <summary>
    /// Per-player undo, kept by the host. Each entry is the list of blocks one edit changed.
    /// Undo puts blocks back only if they still look exactly how that edit left them, so undoing never
    /// wipes out something another player built on the same spot afterwards.
    /// </summary>
    public sealed class UndoHistory
    {
        /// <summary>Most edits remembered per player.</summary>
        public const int MaxEntriesPerPlayer = 40;

        /// <summary>Most changed blocks remembered per player (oldest edits are forgotten first).</summary>
        public const int MaxChangesPerPlayer = 1_500_000;

        sealed class PlayerHistory
        {
            public readonly LinkedList<BlockChange[]> Entries = new LinkedList<BlockChange[]>();
            public int TotalChanges;
        }

        readonly Dictionary<ulong, PlayerHistory> _players = new Dictionary<ulong, PlayerHistory>();

        public int EntryCount(ulong player) => _players.TryGetValue(player, out var history) ? history.Entries.Count : 0;

        /// <summary>Remembers one edit by <paramref name="player"/>.</summary>
        public void Record(ulong player, List<BlockChange> changes)
        {
            if (changes.Count == 0 || changes.Count > MaxChangesPerPlayer) return;
            if (!_players.TryGetValue(player, out var history))
            {
                history = new PlayerHistory();
                _players.Add(player, history);
            }

            history.Entries.AddLast(changes.ToArray());
            history.TotalChanges += changes.Count;
            while (history.Entries.Count > MaxEntriesPerPlayer || history.TotalChanges > MaxChangesPerPlayer)
            {
                history.TotalChanges -= history.Entries.First.Value.Length;
                history.Entries.RemoveFirst();
            }
        }

        /// <summary>
        /// Reverts <paramref name="player"/>'s most recent edit. Adds every chunk that changed to
        /// <paramref name="changedChunks"/>. Returns how many blocks were restored (0 if there was nothing to undo).
        /// </summary>
        public int Undo(BlockWorld world, ulong player, HashSet<int3> changedChunks)
        {
            if (!_players.TryGetValue(player, out var history) || history.Entries.Count == 0) return 0;

            BlockChange[] changes = history.Entries.Last.Value;
            history.Entries.RemoveLast();
            history.TotalChanges -= changes.Length;

            int restored = 0;
            for (int i = changes.Length - 1; i >= 0; i--)
            {
                BlockChange change = changes[i];
                if (world.GetBlock(change.Position) != change.After) continue; // someone changed it since
                if (!world.SetBlock(change.Position, change.Before)) continue;
                changedChunks.Add(BlockWorld.ChunkOf(change.Position));
                restored++;
            }
            return restored;
        }

        public void Forget(ulong player) => _players.Remove(player);

        public void Clear() => _players.Clear();
    }
}
