using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Wildwood.Core.Blocks;
using Wildwood.Core.Terrain;

namespace Wildwood.Net
{
    /// <summary>
    /// Keeps every player's world identical: the world seed (which generates the terrain) and the blocks.
    /// The host owns the world:
    ///  - Players send edit requests to the host (small commands, never raw blocks).
    ///  - The host checks and applies each edit, then forwards it to every client, who apply the same edit.
    ///  - The host remembers what each player's edits changed, so each player can undo their own edits.
    ///    An undo is sent to everyone as fresh copies of the chunks it touched.
    ///  - After every edit or undo, the host checks whether any blocks lost their connection to the ground.
    ///    Those break off into falling pieces: the host simulates their physics and streams their positions.
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
        const string PieceDetachMessage = "wildwood.pieces.detach";
        const string PieceStateMessage = "wildwood.pieces.state";
        const string PieceRemoveMessage = "wildwood.pieces.remove";
        const string PieceRemoveRequestMessage = "wildwood.pieces.remove-request";

        /// <summary>Most loose pieces at once; the oldest disappear first (until rubble turns into pickups).</summary>
        const int MaxPieces = 60;

        /// <summary>Piece states per position message (keeps each message under the unfragmented size limit).</summary>
        const int StatesPerMessage = 24;

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
        readonly List<int3> _restored = new List<int3>();
        readonly HashSet<int3> _seeds = new HashSet<int3>();
        readonly List<List<int3>> _floatingGroups = new List<List<int3>>();
        readonly Dictionary<uint, BlockPiece> _pieces = new Dictionary<uint, BlockPiece>();
        readonly List<uint> _pieceOrder = new List<uint>();
        readonly Dictionary<uint, IncomingPiece> _incomingPieces = new Dictionary<uint, IncomingPiece>();
        readonly List<BlockPieceState> _receivedStates = new List<BlockPieceState>();
        StructureCheck _structure;
        uint _nextPieceId;
        int _worldSeed;
        bool _handlersRegistered;

        sealed class IncomingChunk
        {
            public byte[] Data;
            public int PartsReceived;
        }

        sealed class IncomingPiece
        {
            public BlockPiece Piece;
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

        /// <summary>A piece broke off (on the host right away; on clients when the host says so, or when joining).</summary>
        public event Action<BlockPiece> PieceAdded;

        /// <summary>A piece was cleared away.</summary>
        public event Action<uint> PieceRemoved;

        /// <summary>All pieces were dropped (the session ended).</summary>
        public event Action PiecesCleared;

        /// <summary>Clients only: new positions for moving pieces. The list is reused, so read it right away.</summary>
        public event Action<List<BlockPieceState>> PieceStatesReceived;

        /// <summary>The seed the terrain is generated from. The host sets it; clients receive it when they join.</summary>
        public int WorldSeed
        {
            get => _worldSeed;
            set
            {
                _worldSeed = value;
                // The ground check uses the same terrain the players see.
                _structure = new StructureCheck(new TerrainGenerator(value).Height);
            }
        }

        public bool IsRunning => _manager != null && _manager.IsListening;

        public bool IsHost => _manager != null && _manager.IsServer;

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
            _pieces.Clear();
            _pieceOrder.Clear();
            _incomingPieces.Clear();
            _structure = null;
            World.Clear();
            PiecesCleared?.Invoke();
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
            messaging.RegisterNamedMessageHandler(PieceDetachMessage, OnPieceDetach);
            messaging.RegisterNamedMessageHandler(PieceStateMessage, OnPieceState);
            messaging.RegisterNamedMessageHandler(PieceRemoveMessage, OnPieceRemove);
            messaging.RegisterNamedMessageHandler(PieceRemoveRequestMessage, OnPieceRemoveRequest);
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
            messaging.UnregisterNamedMessageHandler(PieceDetachMessage);
            messaging.UnregisterNamedMessageHandler(PieceStateMessage);
            messaging.UnregisterNamedMessageHandler(PieceRemoveMessage);
            messaging.UnregisterNamedMessageHandler(PieceRemoveRequestMessage);
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

            // Did that edit leave anything hanging in the air?
            _seeds.Clear();
            foreach (var change in _changes) AddSeeds(change.Position);
            DropFloatingBlocks();
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
            _restored.Clear();
            if (_undo.Undo(World, player, _undoChunks, _restored) == 0) return;

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

            // Undoing a support (say, a pillar you just placed) can leave things hanging too.
            _seeds.Clear();
            foreach (int3 position in _restored) AddSeeds(position);
            DropFloatingBlocks();
        }

        // ---------- Falling pieces ----------

        void AddSeeds(int3 position)
        {
            _seeds.Add(position);
            for (int face = 0; face < BlockFaces.Count; face++) _seeds.Add(position + BlockFaces.Directions[face]);
        }

        /// <summary>Host: turn every group near the seeds that lost its support into a falling piece.</summary>
        void DropFloatingBlocks()
        {
            if (_structure == null || _seeds.Count == 0) return;
            _floatingGroups.Clear();
            _structure.FindFloating(World, _seeds, _floatingGroups);
            foreach (var group in _floatingGroups)
            {
                BlockPiece piece = BlockPiece.Extract(World, group, ++_nextPieceId);
                if (piece != null) AddPieceAsHost(piece);
            }
        }

        void AddPieceAsHost(BlockPiece piece)
        {
            _pieces[piece.Id] = piece;
            _pieceOrder.Add(piece.Id);
            foreach (ulong client in _manager.ConnectedClientsIds)
            {
                if (client == NetworkManager.ServerClientId) continue;
                SendPiece(client, piece);
            }
            PieceAdded?.Invoke(piece);

            while (_pieceOrder.Count > MaxPieces) RemovePiece(_pieceOrder[0]);
        }

        /// <summary>Host: clear a piece away for everyone.</summary>
        public void RemovePiece(uint id)
        {
            if (!IsHost || !_pieces.Remove(id)) return;
            _pieceOrder.Remove(id);
            using (var writer = new FastBufferWriter(8, Allocator.Temp))
            {
                writer.WriteValueSafe(id);
                foreach (ulong client in _manager.ConnectedClientsIds)
                {
                    if (client == NetworkManager.ServerClientId) continue;
                    _manager.CustomMessagingManager.SendNamedMessage(PieceRemoveMessage, client, writer, Delivery);
                }
            }
            PieceRemoved?.Invoke(id);
        }

        /// <summary>Ask the host to clear a piece away (any player can).</summary>
        public void RequestRemovePiece(uint id)
        {
            if (!IsRunning) return;
            if (IsHost)
            {
                RemovePiece(id);
                return;
            }
            using var writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe(id);
            _manager.CustomMessagingManager.SendNamedMessage(PieceRemoveRequestMessage, NetworkManager.ServerClientId, writer, Delivery);
        }

        /// <summary>Host: send where the moving pieces are now. Call ~20 times a second.</summary>
        public void BroadcastPieceStates(List<BlockPieceState> states)
        {
            if (!IsHost || states.Count == 0) return;
            foreach (var state in states)
            {
                if (_pieces.TryGetValue(state.Id, out var piece))
                {
                    piece.Position = state.Position;
                    piece.Rotation = state.Rotation;
                }
            }

            for (int start = 0; start < states.Count; start += StatesPerMessage)
            {
                int count = Math.Min(StatesPerMessage, states.Count - start);
                using var writer = new FastBufferWriter(8 + count * 32, Allocator.Temp);
                writer.WriteValueSafe((byte)count);
                for (int i = start; i < start + count; i++)
                {
                    BlockPieceState state = states[i];
                    writer.WriteValueSafe(state.Id);
                    writer.WriteValueSafe(state.Position.x);
                    writer.WriteValueSafe(state.Position.y);
                    writer.WriteValueSafe(state.Position.z);
                    writer.WriteValueSafe(state.Rotation.x);
                    writer.WriteValueSafe(state.Rotation.y);
                    writer.WriteValueSafe(state.Rotation.z);
                    writer.WriteValueSafe(state.Rotation.w);
                }
                foreach (ulong client in _manager.ConnectedClientsIds)
                {
                    if (client == NetworkManager.ServerClientId) continue;
                    // Positions are sent constantly, so a lost one doesn't matter: unreliable, but in order.
                    _manager.CustomMessagingManager.SendNamedMessage(PieceStateMessage, client, writer, NetworkDelivery.UnreliableSequenced);
                }
            }
        }

        void SendPiece(ulong clientId, BlockPiece piece)
        {
            byte[] data = piece.EncodeBlocks();
            int partCount = Math.Max(1, (data.Length + ChunkPartBytes - 1) / ChunkPartBytes);
            for (int part = 0; part < partCount; part++)
            {
                int offset = part * ChunkPartBytes;
                int length = Math.Min(ChunkPartBytes, data.Length - offset);
                using var writer = new FastBufferWriter(length + 96, Allocator.Temp);
                writer.WriteValueSafe(piece.Id);
                writer.WriteValueSafe(piece.Origin.x);
                writer.WriteValueSafe(piece.Origin.y);
                writer.WriteValueSafe(piece.Origin.z);
                writer.WriteValueSafe(piece.Size.x);
                writer.WriteValueSafe(piece.Size.y);
                writer.WriteValueSafe(piece.Size.z);
                writer.WriteValueSafe(piece.Position.x);
                writer.WriteValueSafe(piece.Position.y);
                writer.WriteValueSafe(piece.Position.z);
                writer.WriteValueSafe(piece.Rotation.x);
                writer.WriteValueSafe(piece.Rotation.y);
                writer.WriteValueSafe(piece.Rotation.z);
                writer.WriteValueSafe(piece.Rotation.w);
                writer.WriteValueSafe((ushort)part);
                writer.WriteValueSafe((ushort)partCount);
                writer.WriteValueSafe(data.Length);
                writer.WriteValueSafe((ushort)length);
                writer.WriteBytesSafe(data, length, offset);
                _manager.CustomMessagingManager.SendNamedMessage(PieceDetachMessage, clientId, writer, Delivery);
            }
        }

        void OnPieceDetach(ulong senderClientId, FastBufferReader reader)
        {
            if (IsHost || senderClientId != NetworkManager.ServerClientId) return;
            try
            {
                reader.ReadValueSafe(out uint id);
                reader.ReadValueSafe(out int ox);
                reader.ReadValueSafe(out int oy);
                reader.ReadValueSafe(out int oz);
                reader.ReadValueSafe(out int sx);
                reader.ReadValueSafe(out int sy);
                reader.ReadValueSafe(out int sz);
                reader.ReadValueSafe(out float px);
                reader.ReadValueSafe(out float py);
                reader.ReadValueSafe(out float pz);
                reader.ReadValueSafe(out float qx);
                reader.ReadValueSafe(out float qy);
                reader.ReadValueSafe(out float qz);
                reader.ReadValueSafe(out float qw);
                reader.ReadValueSafe(out ushort part);
                reader.ReadValueSafe(out ushort partCount);
                reader.ReadValueSafe(out int totalLength);
                reader.ReadValueSafe(out ushort length);

                var size = new int3(sx, sy, sz);
                if (math.any(size < 1) || math.any(size > BlockPiece.MaxSide)) throw new FormatException("Bad piece size.");
                int maxBytes = size.x * size.y * size.z * ChunkSerializer.RunBytes;
                int offset = part * ChunkPartBytes;
                if (totalLength <= 0 || totalLength > maxBytes || offset + length > totalLength || part >= partCount)
                    throw new FormatException("Piece part is out of range.");

                if (part == 0 || !_incomingPieces.TryGetValue(id, out var incoming) || incoming.Data.Length != totalLength)
                {
                    incoming = new IncomingPiece
                    {
                        Data = new byte[totalLength],
                        Piece = new BlockPiece
                        {
                            Id = id,
                            Origin = new int3(ox, oy, oz),
                            Size = size,
                            Position = new Vector3(px, py, pz),
                            Rotation = new Quaternion(qx, qy, qz, qw),
                        },
                    };
                    _incomingPieces[id] = incoming;
                }
                reader.ReadBytesSafe(ref incoming.Data, length, offset);
                incoming.PartsReceived++;
                if (incoming.PartsReceived < partCount) return;

                _incomingPieces.Remove(id);
                BlockPiece piece = incoming.Piece;
                piece.Blocks = BlockPiece.DecodeBlocks(incoming.Data, piece.Size);
                piece.RemoveFrom(World);
                _pieces[id] = piece;
                PieceAdded?.Invoke(piece);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored a malformed falling piece: {exception.Message}");
            }
        }

        void OnPieceState(ulong senderClientId, FastBufferReader reader)
        {
            if (IsHost || senderClientId != NetworkManager.ServerClientId) return;
            try
            {
                _receivedStates.Clear();
                reader.ReadValueSafe(out byte count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out uint id);
                    reader.ReadValueSafe(out float px);
                    reader.ReadValueSafe(out float py);
                    reader.ReadValueSafe(out float pz);
                    reader.ReadValueSafe(out float qx);
                    reader.ReadValueSafe(out float qy);
                    reader.ReadValueSafe(out float qz);
                    reader.ReadValueSafe(out float qw);
                    _receivedStates.Add(new BlockPieceState
                    {
                        Id = id,
                        Position = new Vector3(px, py, pz),
                        Rotation = new Quaternion(qx, qy, qz, qw),
                    });
                }
                PieceStatesReceived?.Invoke(_receivedStates);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored malformed piece positions: {exception.Message}");
            }
        }

        void OnPieceRemove(ulong senderClientId, FastBufferReader reader)
        {
            if (IsHost || senderClientId != NetworkManager.ServerClientId) return;
            try
            {
                reader.ReadValueSafe(out uint id);
                _pieces.Remove(id);
                _incomingPieces.Remove(id);
                PieceRemoved?.Invoke(id);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored a malformed piece removal: {exception.Message}");
            }
        }

        void OnPieceRemoveRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsHost) return;
            try
            {
                reader.ReadValueSafe(out uint id);
                RemovePiece(id);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Wildwood] Ignored a malformed piece removal request: {exception.Message}");
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

            // Pieces that are already loose, where they are now.
            foreach (uint id in _pieceOrder) SendPiece(senderClientId, _pieces[id]);

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
