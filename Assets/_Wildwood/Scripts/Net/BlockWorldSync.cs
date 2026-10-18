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
    /// Keeps every player's <see cref="BlockWorld"/> identical. The host owns the world:
    ///  - Players send edit requests to the host (small commands, never raw blocks).
    ///  - The host checks and applies each edit, then forwards it to every client, who apply the same edit.
    ///  - A player who joins mid-session asks for a snapshot; the host streams the current chunks to them
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

        const NetworkDelivery Delivery = NetworkDelivery.ReliableFragmentedSequenced;

        /// <summary>Chunk data is split into pieces this big (Unity Transport allows 6 KB per message).</summary>
        const int ChunkPartBytes = 4000;

        /// <summary>How much snapshot data the host sends to each joining player per frame.</summary>
        const int SnapshotBytesPerFrame = 48 * 1024;

        readonly NetworkManager _manager;
        readonly Dictionary<ulong, Queue<int3>> _pendingSnapshots = new Dictionary<ulong, Queue<int3>>();
        readonly List<ulong> _finishedSnapshots = new List<ulong>();
        readonly Dictionary<int3, IncomingChunk> _incomingChunks = new Dictionary<int3, IncomingChunk>();
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
                ApplyAsHost(edit);
                return;
            }

            using var writer = new FastBufferWriter(64, Allocator.Temp);
            WriteEdit(writer, edit);
            _manager.CustomMessagingManager.SendNamedMessage(EditRequestMessage, NetworkManager.ServerClientId, writer, Delivery);
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
            UnregisterHandlers();
            ResetState();
        }

        void ResetState()
        {
            _pendingSnapshots.Clear();
            _incomingChunks.Clear();
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
        }

        void RegisterHandlers()
        {
            var messaging = _manager.CustomMessagingManager;
            if (messaging == null || _handlersRegistered) return;
            messaging.RegisterNamedMessageHandler(EditRequestMessage, OnEditRequest);
            messaging.RegisterNamedMessageHandler(EditMessage, OnEdit);
            messaging.RegisterNamedMessageHandler(SnapshotRequestMessage, OnSnapshotRequest);
            messaging.RegisterNamedMessageHandler(ChunkMessage, OnChunk);
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
        }

        // ---------- Edits ----------

        void OnEditRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!_manager.IsServer) return;
            if (TryReadEdit(ref reader, out var edit)) ApplyAsHost(edit);
        }

        void ApplyAsHost(in BlockEdit edit)
        {
            if (BlockEditor.Apply(World, edit) == 0) return;

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

        // ---------- Snapshots ----------

        void OnSnapshotRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!_manager.IsServer || senderClientId == NetworkManager.ServerClientId) return;
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
