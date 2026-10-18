using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Wildwood.Core.Blocks;

namespace Wildwood.Net
{
    /// <summary>
    /// Keeps every player's world identical: the world seed (which generates the terrain) and the blocks.
    /// The host owns the world:
    ///  - Players send edit requests to the host (small commands, never raw blocks).
    ///  - The host checks and applies each edit, then forwards it to every client, who apply the same edit.
    ///  - The host remembers what each player's edits changed, so each player can undo their own edits.
    ///    An undo is sent to everyone as fresh copies of the chunks it touched.
    ///  - A player who joins asks for a snapshot; the host replies with the world seed, then streams the chunks
    ///    a little each frame. Snapshots and edits travel on the same ordered channel, so a chunk's snapshot
    ///    always reflects every edit sent before it.
    /// Uses Netcode "named messages", so no networked GameObject is needed.
    /// </summary>
    public sealed class BlockWorldSync : IDisposable
    {
        const string EditRequestMessage = "wildwood.blocks.edit-request";
        const string EditMessage = "wildwood.blocks.edit";
        const string SnapshotRequestMessage = "wildwood.blocks.snapshot-request";
        const string ChunkMessage = "wildwood.blocks.chunk";
        const string WorldInfoMessage = "wildwood.world.info";
        const string UndoRequestMessage = "wildwood.blocks.undo-request";

        const NetworkDelivery Delivery = NetworkDelivery.ReliableFragmentedSequenced;

        /// <summary>Chunk data is split into pieces this big (Unity Transport allows 6 KB per message).</summary>
        const int ChunkPartBytes = 4000;

        /// <summary>How much snapshot data the host sends to each joining player per frame.</summary>
        const int SnapshotBytesPerFrame = 48 * 1024;

        readonly NetworkManager _manager;
        readonly Dictionary<ulong, Queue<int3>> _pendingSnapshots = new Dictionary<ulong, Queue<int3>>();
        readonly List<ulong> _finishedSnapshots = new List<ulong>();
        readonly Dictionary<int3, IncomingChunk> _incomingChunks = new Dictionary<int3, IncomingChunk>();
        readonly UndoHistory _undo = new UndoHistory();
        readonly List<BlockChange> _changes = new List<BlockChange>();
        readonly HashSet<int3> _undoChunks = new HashSet<int3>();
        bool _handlersRegistered;

        sealed class IncomingChunk
        {
            public byte[] Data;
            public int PartsReceived;
        }

        public BlockWorldSync(NetworkManager manager, BlockWorld world)
        {
            _manager = manager;
            World = world;
            _manager.OnServerStarted += HandleServerStarted;
            _manager.OnClientStarted += HandleClientStarted;
            _manager.OnServerStopped += HandleStopped;
            _manager.OnClientStopped += HandleStopped;
            _manager.OnClientConnectedCallback += HandleClientConnected;
            _manager.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        public BlockWorld World { get; }

        /// <summary>Raised on the host when a new session starts with an empty world (a good time to load or generate).</summary>
        public event Action HostStarted;

        /// <summary>Raised on a client when the host has told it the world seed.</summary>
        public event Action<int> WorldInfoReceived;

        /// <summary>Raised when a session ends, before the world is cleared (true if this machine was the host). Save here.</summary>
        public event Action<bool> Stopping;

        /// <summary>The seed the terrain is generated from. The host sets it; clients receive it when they join.</summary>
        public int WorldSeed { get; set; }

        public bool IsRunning => _manager != null && _manager.IsListening;

        public void Dispose()
        {
            if (_manager == null) return;
            _manager.OnServerStarted -= HandleServerStarted;
            _manager.OnClientStarted -= HandleClientStarted;
            _manager.OnServerStopped -= HandleStopped;
            _manager.OnClientStopped -= HandleStopped;
            _manager.OnClientConnectedCallback -= HandleClientConnected;
            _manager.OnClientDisconnectCallback -= HandleClientDisconnected;
            UnregisterHandlers();
        }

        /// <summary>Ask for an edit. On the host it applies right away; on a client it goes to the host.</summary>
        public void RequestEdit(in BlockEdit edit)
        {
            if (!IsRunning || !BlockEditor.IsValid(edit)) return;

            if (_manager.IsServer)
            {
                ApplyAsHost(edit, NetworkManager.ServerClientId);
                return;
            }

            using var writer = new FastBufferWriter(64, Allocator.Temp);
            WriteEdit(writer, edit);
            _manager.CustomMessagingManager.SendNamedMessage(EditRequestMessage, NetworkManager.ServerClientId, writer, Delivery);
        }

        /// <summary>Undo this player's most recent edit (the host does the work).</summary>
        public void RequestUndo()
        {
            if (!IsRunning) return;
            if (_manager.IsServer)
            {
                UndoAsHost(NetworkManager.ServerClientId);
                return;
            }

            using var writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe((byte)1); // format version
            _manager.CustomMessagingManager.SendNamedMessage(UndoRequestMessage, NetworkManager.ServerClientId, writer, Delivery);
        }

        /// <summary>Call once per frame. Streams snapshot data to players who are joining.</summary>
        public void Tick()
        {
            if (!IsRunning || !_manager.IsServer || _pendingSnapshots.Count == 0) return;

            _finishedSnapshots.Clear();
            foreach (var pair in _pendingSnapshots)
            {
                int budget = SnapshotBytesPerFrame;
                Queue<int3> queue = pair.Value;
                while (queue.Count > 0 && budget > 0)
                {
                    int3 coord = queue.Dequeue();
                    if (World.TryGetChunk(coord, out var chunk) && !chunk.IsEmpty)
                        budget -= SendChunk(pair.Key, chunk);
                }
                if (queue.Count == 0) _finishedSnapshots.Add(pair.Key);
            }
            foreach (ulong client in _finishedSnapshots) _pendingSnapshots.Remove(client);
        }

        // ---------- Session lifecycle ----------

        void HandleServerStarted()
        {
            ResetState();
            RegisterHandlers();
            HostStarted?.Invoke();
        }

        void HandleClientStarted()
        {
            if (_manager.IsServer) return; // the host was set up in HandleServerStarted
            ResetState();
            RegisterHandlers();
        }

        void HandleStopped(bool wasHost)
        {
            Stopping?.Invoke(wasHost);
            UnregisterHandlers();
            ResetState();
        }

        void ResetState()
        {
            _pendingSnapshots.Clear();
            _incomingChunks.Clear();
            _undo.Clear();
            World.Clear();
        }

        void HandleClientConnected(ulong clientId)
        {
            // On a client: once we're connected, ask the host for the current world.
            if (!_manager.IsServer && clientId == _manager.LocalClientId)
            {
                using var writer = new FastBufferWriter(8, Allocator.Temp);
                writer.WriteValueSafe((byte)1); // format version
                _manager.CustomMessagingManager.SendNamedMessage(SnapshotRequestMessage, NetworkManager.ServerClientId, writer, Delivery);
            }
        }

        void HandleClientDisconnected(ulong clientId)
        {
            _pendingSnapshots.Remove(clientId);
            _undo.Forget(clientId);
        }

        void RegisterHandlers()
        {
            var messaging = _manager.CustomMessagingManager;
            if (messaging == null || _handlersRegistered) return;
            messaging.RegisterNamedMessageHandler(EditRequestMessage, OnEditRequest);
            messaging.RegisterNamedMessageHandler(EditMessage, OnEdit);
            messaging.RegisterNamedMessageHandler(SnapshotRequestMessage, OnSnapshotRequest);
            messaging.RegisterNamedMessageHandler(ChunkMessage, OnChunk);
            messaging.RegisterNamedMessageHandler(WorldInfoMessage, OnWorldInfo);
            messaging.RegisterNamedMessageHandler(UndoRequestMessage, OnUndoRequest);
            _handlersRegistered = true;
        }

        void UnregisterHandlers()
        {
            if (!_handlersRegistered) return;
            _handlersRegistered = false;
            var messaging = _manager != null ? _manager.CustomMessagingManager : null;
            if (messaging == null) return;
            messaging.UnregisterNamedMessageHandler(EditRequestMessage);
            messaging.UnregisterNamedMessageHandler(EditMessage);
            messaging.UnregisterNamedMessageHandler(SnapshotRequestMessage);
            messaging.UnregisterNamedMessageHandler(ChunkMessage);
            messaging.UnregisterNamedMessageHandler(WorldInfoMessage);
            messaging.UnregisterNamedMessageHandler(UndoRequestMessage);
        }

        // ---------- Edits ----------

        void OnEditRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!_manager.IsServer) return;
            if (TryReadEdit(ref reader, out var edit)) ApplyAsHost(edit, senderClientId);
        }

        void ApplyAsHost(in BlockEdit edit, ulong player)
        {
            _changes.Clear();
            if (BlockEditor.Apply(World, edit, _changes) == 0) return;
            _undo.Record(player, _changes);

            using var writer = new FastBufferWriter(64, Allocator.Temp);
            WriteEdit(writer, edit);
            foreach (ulong client in _manager.ConnectedClientsIds)
            {
                if (client == NetworkManager.ServerClientId) continue; // that's us
                _manager.CustomMessagingManager.SendNamedMessage(EditMessage, client, writer, Delivery);
            }
        }

        void OnEdit(ulong senderClientId, FastBufferReader reader)
        {
            if (_manager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
            if (TryReadEdit(ref reader, out var edit)) BlockEditor.Apply(World, edit);
        }

        static void WriteEdit(FastBufferWriter writer, in BlockEdit edit)
        {
            writer.WriteValueSafe((byte)edit.Kind);
            writer.WriteValueSafe((byte)edit.Mode);
            writer.WriteValueSafe(edit.Min.x);
            writer.WriteValueSafe(edit.Min.y);
            writer.WriteValueSafe(edit.Min.z);
            writer.WriteValueSafe(edit.Max.x);
            writer.WriteValueSafe(edit.Max.y);
            writer.WriteValueSafe(edit.Max.z);
            writer.WriteValueSafe(edit.Block.Material);
            writer.WriteValueSafe(edit.Block.Corners);
            writer.WriteValueSafe(edit.Block.Reserved);
            writer.WriteValueSafe(edit.Corners);
        }

        static bool TryReadEdit(ref FastBufferReader reader, out BlockEdit edit)
        {
            edit = default;
            try
            {
                reader.ReadValueSafe(out byte kind);
                reader.ReadValueSafe(out byte mode);
                reader.ReadValueSafe(out int minX);
                reader.ReadValueSafe(out int minY);
                reader.ReadValueSafe(out int minZ);
                reader.ReadValueSafe(out int maxX);
                reader.ReadValueSafe(out int maxY);
                reader.ReadValueSafe(out int maxZ);
                reader.ReadValueSafe(out ushort material);
                reader.ReadValueSafe(out byte corners);
                reader.ReadValueSafe(out byte reserved);
                reader.ReadValueSafe(out byte chiselCorners);

                edit = new BlockEdit
                {
                    Kind = (BlockEditKind)kind,
                    Mode = (FillMode)mode,
                    Min = new int3(minX, minY, minZ),
                    Max = new int3(maxX, maxY, maxZ),
                    Block = new Block { Material = material, Corners = corners, Reserved = reserved },
                    Corners = chiselCorners,
                };
                return BlockEditor.IsValid(edit);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored a malformed block edit: {exception.Message}");
                return false;
            }
        }

        // ---------- Undo ----------

        void OnUndoRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (_manager.IsServer) UndoAsHost(senderClientId);
        }

        void UndoAsHost(ulong player)
        {
            _undoChunks.Clear();
            if (_undo.Undo(World, player, _undoChunks) == 0) return;

            // Undo can touch any mix of blocks, so send everyone fresh copies of the affected chunks.
            foreach (int3 coord in _undoChunks)
            {
                ChunkData chunk = World.TryGetChunk(coord, out var existing) ? existing : new ChunkData(coord);
                foreach (ulong client in _manager.ConnectedClientsIds)
                {
                    if (client == NetworkManager.ServerClientId) continue;
                    SendChunk(client, chunk);
                }
            }
        }

        // ---------- Snapshots ----------

        void OnSnapshotRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!_manager.IsServer || senderClientId == NetworkManager.ServerClientId) return;

            // The seed goes first, on the same ordered channel as the chunks that follow.
            using (var writer = new FastBufferWriter(16, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)1); // format version
                writer.WriteValueSafe(WorldSeed);
                _manager.CustomMessagingManager.SendNamedMessage(WorldInfoMessage, senderClientId, writer, Delivery);
            }

            var queue = new Queue<int3>();
            foreach (var chunk in World.Chunks) queue.Enqueue(chunk.Coord);
            _pendingSnapshots[senderClientId] = queue;
        }

        /// <summary>Sends one chunk (possibly in several parts). Returns the number of bytes sent.</summary>
        int SendChunk(ulong clientId, ChunkData chunk)
        {
            byte[] data = ChunkSerializer.Encode(chunk);
            int partCount = (data.Length + ChunkPartBytes - 1) / ChunkPartBytes;
            for (int part = 0; part < partCount; part++)
            {
                int offset = part * ChunkPartBytes;
                int length = Math.Min(ChunkPartBytes, data.Length - offset);
                using var writer = new FastBufferWriter(length + 32, Allocator.Temp);
                writer.WriteValueSafe(chunk.Coord.x);
                writer.WriteValueSafe(chunk.Coord.y);
                writer.WriteValueSafe(chunk.Coord.z);
                writer.WriteValueSafe((ushort)part);
                writer.WriteValueSafe((ushort)partCount);
                writer.WriteValueSafe(data.Length);
                writer.WriteValueSafe((ushort)length);
                writer.WriteBytesSafe(data, length, offset);
                _manager.CustomMessagingManager.SendNamedMessage(ChunkMessage, clientId, writer, Delivery);
            }
            return data.Length;
        }

        void OnWorldInfo(ulong senderClientId, FastBufferReader reader)
        {
            if (_manager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
            try
            {
                reader.ReadValueSafe(out byte version);
                reader.ReadValueSafe(out int seed);
                WorldSeed = seed;
                WorldInfoReceived?.Invoke(seed);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored malformed world info: {exception.Message}");
            }
        }

        void OnChunk(ulong senderClientId, FastBufferReader reader)
        {
            if (_manager.IsServer || senderClientId != NetworkManager.ServerClientId) return;
            try
            {
                reader.ReadValueSafe(out int x);
                reader.ReadValueSafe(out int y);
                reader.ReadValueSafe(out int z);
                reader.ReadValueSafe(out ushort part);
                reader.ReadValueSafe(out ushort partCount);
                reader.ReadValueSafe(out int totalLength);
                reader.ReadValueSafe(out ushort length);

                int offset = part * ChunkPartBytes;
                if (totalLength <= 0 || totalLength > BlockConstants.ChunkVolume * ChunkSerializer.RunBytes ||
                    offset + length > totalLength || part >= partCount)
                {
                    throw new FormatException("Chunk part is out of range.");
                }

                var coord = new int3(x, y, z);
                if (part == 0 || !_incomingChunks.TryGetValue(coord, out var incoming) || incoming.Data.Length != totalLength)
                {
                    incoming = new IncomingChunk { Data = new byte[totalLength] };
                    _incomingChunks[coord] = incoming;
                }
                reader.ReadBytesSafe(ref incoming.Data, length, offset);
                incoming.PartsReceived++;

                if (incoming.PartsReceived == partCount)
                {
                    _incomingChunks.Remove(coord);
                    World.LoadChunk(coord, ChunkSerializer.Decode(incoming.Data));
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored a malformed chunk snapshot: {exception.Message}");
            }
        }
    }
}
