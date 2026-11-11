using System.Buffers.Binary;
using System.Net;
using System.Text;
using Hazel;
using Hazel.Udp;
using Mono.Cecil;
using Mono.Cecil.Cil;

const int ProtocolVersion = ServerConstants.ProtocolVersion;
const int DefaultPort = ServerConstants.DefaultPort;
const string RetiredAddress = ServerConstants.RetiredAddress;
const string LocalAddress = ServerConstants.LocalAddress;

if (args.Length > 0 && args[0] == "patch-client")
{
    if (args.Length is < 2 or > 3)
    {
        Console.Error.WriteLine("Usage: LocalInnerNetServer patch-client <Assembly-CSharp.dll> [output.dll]");
        return 2;
    }

    return PatchClient(args[1], args.Length == 3 ? args[2] : null);
}

if (args.Length > 0 && args[0] == "self-test")
{
    ProtocolCodec.RunSelfTests();
    Console.WriteLine("Protocol codec tests passed.");
    return 0;
}

if (args.Length > 0 && args[0] == "integration-test")
{
    await InnerNetIntegrationTest.RunAsync();
    return 0;
}

int port = DefaultPort;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out int parsedPort):
            port = parsedPort;
            break;
        case "--help":
        case "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown or invalid argument: {args[i]}");
            PrintUsage();
            return 2;
    }
}

if (port is < 1 or > 65535)
{
    Console.Error.WriteLine("The port must be between 1 and 65535.");
    return 2;
}

await using var server = new InnerNetServer(IPAddress.Loopback, port);
Console.WriteLine($"Local InnerNet server ({ProtocolVersion}) listening on {IPAddress.Loopback}:{port}.");
Console.WriteLine("Press Ctrl+C to stop.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    server.Stop();
    stop.Cancel();
};

try
{
    await server.RunAsync(stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        LocalInnerNetServer [--port 22023]
        LocalInnerNetServer patch-client <Assembly-CSharp.dll> [output.dll]
        LocalInnerNetServer self-test
        LocalInnerNetServer integration-test

        The default server binding is loopback-only.
        """);
}

static int PatchClient(string inputPath, string? outputPath)
{
    string fullInput = Path.GetFullPath(inputPath);
    if (!File.Exists(fullInput))
    {
        Console.Error.WriteLine($"Client assembly not found: {fullInput}");
        return 2;
    }

    string fullOutput = Path.GetFullPath(outputPath ?? Path.Combine(
        Path.GetDirectoryName(fullInput)!,
        Path.GetFileNameWithoutExtension(fullInput) + ".local.dll"));
    if (string.Equals(fullInput, fullOutput, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("The patched output must be a separate file; the original is never overwritten.");
        return 2;
    }

    try
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(fullInput);
        int endpointReplacements = 0;
        foreach (ModuleDefinition module in assembly.Modules)
        {
            foreach (TypeDefinition type in AllTypes(module.Types))
            {
                foreach (MethodDefinition method in type.Methods.Where(method => method.HasBody))
                {
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode != OpCodes.Ldstr || instruction.Operand is not string value)
                        {
                            continue;
                        }

                        if (value == RetiredAddress)
                        {
                            instruction.Operand = LocalAddress;
                            endpointReplacements++;
                        }
                    }
                }

                foreach (FieldDefinition field in type.Fields.Where(field => field.IsLiteral && field.HasConstant))
                {
                    if (field.Constant is not string value)
                    {
                        continue;
                    }

                    if (value == RetiredAddress)
                    {
                        field.Constant = LocalAddress;
                        endpointReplacements++;
                    }
                }
            }
        }

        if (endpointReplacements == 0)
        {
            Console.Error.WriteLine($"No {RetiredAddress} endpoint literals were found; refusing to emit an unchanged client.");
            return 1;
        }

        assembly.Write(fullOutput);
        Console.WriteLine($"Patched {endpointReplacements} game-service endpoint reference(s) to {LocalAddress}.");
        Console.WriteLine($"Original unchanged. Patched assembly: {fullOutput}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Client patch failed: {exception.Message}");
        return 1;
    }
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (TypeDefinition type in types)
    {
        yield return type;
        foreach (TypeDefinition nested in AllTypes(type.NestedTypes))
        {
            yield return nested;
        }
    }
}

sealed class InnerNetServer : IAsyncDisposable
{
    private readonly UdpConnectionListener _listener;
    private readonly UdpConnectionListener _announcementListener;
    private readonly HttpListener _telemetryListener = new();
    private readonly Dictionary<Connection, Client> _clients = new();
    private readonly HashSet<Connection> _auxiliaryConnections = new();
    private readonly Dictionary<int, GameRoom> _rooms = new();
    private readonly HashSet<IPAddress> _bannedAddresses = new();
    private readonly object _gate = new();
    private int _nextClientId = 1;
    private int _stopped;

    public InnerNetServer(IPAddress address, int port)
    {
        _listener = new UdpConnectionListener(new NetworkEndPoint(address, port, IPMode.IPv4));
        _listener.NewConnection += OnNewConnection;
        _listener.Start();

        _announcementListener = new UdpConnectionListener(
            new NetworkEndPoint(address, ServerConstants.AnnouncementPort, IPMode.IPv4));
        _announcementListener.NewConnection += OnAnnouncementConnection;
        _announcementListener.Start();

        _telemetryListener.Prefixes.Add($"http://{address}:{ServerConstants.TelemetryPort}/");
        _telemetryListener.Start();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task telemetry = ServeTelemetryAsync(cancellationToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            if (Volatile.Read(ref _stopped) == 0)
            {
                _telemetryListener.Stop();
            }
            try
            {
                await telemetry;
            }
            catch (Exception exception) when (
                cancellationToken.IsCancellationRequested &&
                exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task ServeTelemetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _telemetryListener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await context.Request.InputStream.CopyToAsync(Stream.Null, cancellationToken);
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            }
            catch (Exception exception) when (exception is IOException or HttpListenerException)
            {
                Console.Error.WriteLine($"Local telemetry request failed: {exception.Message}");
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private void OnAnnouncementConnection(object? sender, NewConnectionEventArgs eventArgs)
    {
        Connection connection = eventArgs.Connection;
        byte[] handshake = eventArgs.HandshakeData;
        const int reliableHeaderLength = 2;
        const int handshakePrefixLength = 1;
        int requestOffset = reliableHeaderLength + handshakePrefixLength;
        if (handshake.Length <= requestOffset || handshake[reliableHeaderLength] != 0)
        {
            connection.Dispose();
            return;
        }

        try
        {
            SpanReader request = new(handshake.AsSpan(requestOffset));
            int announcementVersion = request.ReadPackedInt32();
            _ = request.ReadPackedInt32();
            if (announcementVersion != 1)
            {
                connection.Dispose();
                return;
            }

            lock (_gate)
            {
                _auxiliaryConnections.Add(connection);
                connection.Disconnected += OnAuxiliaryDisconnected;
            }

            PacketWriter response = new();
            response.WritePackedInt32(1);
            response.WriteString("Local offline server.");
            connection.SendBytes(response.ToArray(), SendOption.None);
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Rejected malformed local announcement request: {exception.Message}");
            connection.Dispose();
        }
    }

    private void OnNewConnection(object? sender, NewConnectionEventArgs eventArgs)
    {
        Connection connection = eventArgs.Connection;
        if (!TryReadHandshake(eventArgs.HandshakeData, out string playerName))
        {
            Console.Error.WriteLine($"Rejected a client with incompatible or malformed handshake from {connection.EndPoint}.");
            SendDisconnect(connection, 5);
            connection.Dispose();
            return;
        }

        Client client;
        lock (_gate)
        {
            if (_clients.ContainsKey(connection))
            {
                return;
            }

            client = new Client(Interlocked.Increment(ref _nextClientId), connection, playerName);
            _clients.Add(connection, client);
            connection.DataReceived += OnDataReceived;
            connection.Disconnected += OnDisconnected;
        }

        Console.WriteLine($"Client {client.Id} ({client.Name}) connected from {connection.EndPoint}.");
    }

    private void OnDataReceived(object? sender, DataReceivedEventArgs eventArgs)
    {
        if (sender is not Connection connection)
        {
            return;
        }

        lock (_gate)
        {
            if (!_clients.TryGetValue(connection, out Client? client))
            {
                return;
            }

            try
            {
                ProtocolCodec.ReadMessages(eventArgs.Bytes, message =>
                {
                    HandleMessage(client, message, eventArgs.SendOption);
                });
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
            {
                Console.Error.WriteLine($"Malformed packet from client {client.Id}: {exception.Message}");
                _clients.Remove(connection);
                RemoveClientFromRoom(client, client.Room);
                connection.Dispose();
            }
        }
    }

    private void HandleMessage(Client client, ProtocolMessage message, SendOption sendOption)
    {
        switch (message.Tag)
        {
            case 0:
                CreateGame(client);
                break;
            case 1:
                JoinGame(client, message.ReadInt32());
                break;
            case 2:
                if (TryReadGameId(client, message, out GameRoom? startRoom) && startRoom is not null &&
                    IsHost(client, startRoom))
                {
                    startRoom.Started = true;
                    startRoom.RolesAssigned = false;
                    startRoom.ImpostorClientIds.Clear();
                    startRoom.DeadClientIds.Clear();
                    Broadcast(startRoom, message.RawBytes, null, SendOption.Reliable);
                }
                break;
            case 3:
                if (TryReadGameId(client, message, out GameRoom? leaveRoom))
                {
                    RemoveClientFromRoom(client, leaveRoom);
                }
                break;
            case 5:
            case 6:
                RelayGameData(client, message, sendOption);
                break;
            case 8:
                EndGame(client, message);
                break;
            case 9:
                SendGameList(client, message.ReadBoolean());
                break;
            case 10:
                ChangeGamePublic(client, message);
                break;
            case 11:
                KickPlayer(client, message);
                break;
        }
    }

    private void CreateGame(Client client)
    {
        if (client.Room is not null)
        {
            return;
        }

        int gameId = AllocateGameId();
        GameRoom room = new(gameId, client);
        _rooms.Add(gameId, room);
        client.Room = room;
        Send(client.Connection, ProtocolCodec.Message(0, writer => writer.WriteInt32(gameId)));
        Console.WriteLine($"Client {client.Id} created game {ProtocolCodec.GameName(gameId)}.");
    }

    private void JoinGame(Client client, int gameId)
    {
        if (client.Connection.EndPoint is NetworkEndPoint networkEndPoint &&
            networkEndPoint.EndPoint is IPEndPoint remoteEndPoint &&
            _bannedAddresses.Contains(remoteEndPoint.Address))
        {
            SendJoinFailure(client.Connection, 6);
            return;
        }

        if (gameId == 0)
        {
            SendJoinFailure(client.Connection, 3);
            return;
        }

        if (!_rooms.TryGetValue(gameId, out GameRoom? room))
        {
            SendJoinFailure(client.Connection, 3);
            return;
        }

        if (room.Ended)
        {
            JoinEndedGame(client, room);
            return;
        }

        if (room.Started)
        {
            SendJoinFailure(client.Connection, 2);
            return;
        }

        if (!room.Clients.Contains(client) && room.Clients.Count >= 10)
        {
            SendJoinFailure(client.Connection, 1);
            return;
        }

        if (client.Room is not null && client.Room != room)
        {
            RemoveClientFromRoom(client, client.Room);
        }

        RemoveClientFromEndedRooms(client, room);
        if (!room.Clients.Contains(client))
        {
            room.Clients.Add(client);
            client.Room = room;
            if (room.Host is null)
            {
                room.Host = client;
            }

            byte[] playerJoined = PlayerJoinedMessage(room, client);
            Broadcast(room, playerJoined, client, SendOption.Reliable);
            Console.WriteLine($"Client {client.Id} joined game {ProtocolCodec.GameName(gameId)} ({room.Clients.Count}/10).");
        }

        Send(client.Connection, ProtocolCodec.Combine(JoinedMessage(room, client), GameVisibilityMessage(room)));
    }

    private void JoinEndedGame(Client client, GameRoom room)
    {
        if (client.Room is not null && client.Room != room)
        {
            RemoveClientFromRoom(client, client.Room);
        }

        RemoveClientFromEndedRooms(client, room);
        if (client == room.Host)
        {
            room.Ended = false;
            room.Started = false;
            room.RejoinCandidates.Clear();
            ResetRoundRoles(room);
            if (!room.Clients.Contains(client))
            {
                room.Clients.Add(client);
            }
            client.Room = room;

            Broadcast(room, PlayerJoinedMessage(room, client), client, SendOption.Reliable);
            foreach (Client player in room.Clients.ToArray())
            {
                Send(player.Connection,
                    ProtocolCodec.Combine(JoinedMessage(room, player), GameVisibilityMessage(room)));
            }
            return;
        }

        if (!room.Clients.Contains(client))
        {
            if (room.Clients.Count >= 9)
            {
                SendJoinFailure(client.Connection, 1);
                return;
            }

            room.Clients.Add(client);
            if (!room.RejoinCandidates.Contains(client))
            {
                room.RejoinCandidates.Add(client);
            }
            client.Room = room;
            Broadcast(room, PlayerJoinedMessage(room, client), client, SendOption.Reliable);
        }

        Send(client.Connection, ProtocolCodec.Message(12, writer =>
        {
            writer.WriteInt32(room.Id);
            writer.WriteInt32(client.Id);
        }));
    }

    private static byte[] JoinedMessage(GameRoom room, Client client)
    {
        return ProtocolCodec.Message(7, writer =>
        {
            writer.WriteInt32(room.Id);
            writer.WriteInt32(client.Id);
            writer.WriteInt32(room.Host?.Id ?? -1);
            writer.WritePackedInt32(room.Clients.Count - 1);
            foreach (Client other in room.Clients)
            {
                if (other != client)
                {
                    writer.WritePackedInt32(other.Id);
                }
            }
        });
    }

    private static byte[] PlayerJoinedMessage(GameRoom room, Client client)
    {
        return ProtocolCodec.Message(1, writer =>
        {
            writer.WriteInt32(room.Id);
            writer.WriteInt32(client.Id);
            writer.WriteInt32(room.Host?.Id ?? -1);
        });
    }

    private void SendGameList(Client client, bool includePrivate)
    {
        GameRoom[] rooms = _rooms.Values
            .Where(room => !room.Ended && !room.Started && room.Clients.Count > 0 && (room.IsPublic || includePrivate))
            .ToArray();

        byte[] response = ProtocolCodec.Message(9, writer =>
        {
            writer.WritePackedInt32(rooms.Length);
            foreach (GameRoom room in rooms)
            {
                writer.WriteInt32(room.Id);
                writer.WritePackedInt32(room.Clients.Count);
                writer.WritePackedInt32((int)Math.Max(0, (DateTimeOffset.UtcNow - room.CreatedAt).TotalSeconds));
                writer.WriteString(room.Host?.Name ?? "Local Host");
            }
        });
        Send(client.Connection, response);
    }

    private void ChangeGamePublic(Client client, ProtocolMessage message)
    {
        int gameId = message.ReadInt32();
        byte alteration = message.ReadByte();
        bool isPublic = message.ReadBoolean();
        if (alteration == 1 && client.Room is { } room && room.Id == gameId && IsHost(client, room))
        {
            room.IsPublic = isPublic;
            Broadcast(room, GameVisibilityMessage(room), null, SendOption.Reliable);
        }
    }

    private static byte[] GameVisibilityMessage(GameRoom room)
    {
        return ProtocolCodec.Message(10, writer =>
        {
            writer.WriteInt32(room.Id);
            writer.WriteByte(1);
            writer.WriteBoolean(room.IsPublic);
        });
    }

    private void RelayGameData(Client client, ProtocolMessage message, SendOption sendOption)
    {
        int gameId = message.ReadInt32();
        if (client.Room is not { } room || room.Id != gameId || !room.Clients.Contains(client))
        {
            return;
        }
        int targetId = message.Tag == 6 ? message.ReadPackedInt32() : -1;
        byte[] gameData = message.ReadRemainingBytes();
        if (ContainsHostOnlyRpc(room, gameData) && !IsHost(client, room))
        {
            Console.WriteLine($"Rejected host-only settings RPC from non-host client {client.Id}.");
            return;
        }

        TrackGameData(room, client, gameData);
        if (message.Tag == 5)
        {
            Broadcast(room, message.RawBytes, client, sendOption);
            return;
        }

        Client? target = room.Clients.FirstOrDefault(candidate => candidate.Id == targetId);
        if (target is not null)
        {
            Send(target.Connection, message.RawBytes, sendOption);
        }
    }

    private static void TrackGameData(GameRoom room, Client client, byte[] gameData)
    {
        if (room.Ended)
        {
            return;
        }

        ProtocolCodec.ReadMessages(gameData, innerMessage =>
        {
            if (innerMessage.Tag == 4 && client == room.Host)
            {
                TrackSpawnedPlayerObject(room, innerMessage);
            }
            else if (!room.Started)
            {
                return;
            }
            else if (innerMessage.Tag == 1)
            {
                TrackPlayerState(room, client, innerMessage);
            }
            else if (innerMessage.Tag == 2 && client == room.Host)
            {
                TrackInfectedPlayers(room, innerMessage);
            }
        });
    }

    private static void TrackSpawnedPlayerObject(GameRoom room, ProtocolMessage message)
    {
        _ = message.ReadPackedInt32();
        int ownerId = message.ReadPackedInt32();
        byte flags = message.ReadByte();
        int objectCount = message.ReadPackedInt32();
        if (objectCount is < 0 or > 1024)
        {
            throw new InvalidDataException("Spawn object count is invalid.");
        }

        for (int i = 0; i < objectCount; i++)
        {
            int netId = message.ReadPackedInt32();
            ProtocolMessage initialState = message.ReadMessage();
            if (initialState.Tag != 1)
            {
                throw new InvalidDataException("Spawned object is missing its initial state.");
            }

            if ((flags & 1) != 0)
            {
                room.PlayerObjectOwners[netId] = ownerId;
                if (i == 0)
                {
                    room.PlayerControlNetIds[netId] = ownerId;
                    room.DeadClientIds.Remove(ownerId);
                }
            }
        }
    }

    private static void TrackPlayerState(GameRoom room, Client client, ProtocolMessage message)
    {
        int netId = message.ReadPackedInt32();
        if (!room.PlayerControlNetIds.TryGetValue(netId, out int ownerId) ||
            (client != room.Host && client.Id != ownerId))
        {
            return;
        }

        ProtocolMessage state = message.ReadMessage();
        if (state.Tag != 1)
        {
            throw new InvalidDataException("Player state update is missing its data message.");
        }
        byte flags = state.ReadByte();
        _ = state.ReadByte();
        if ((flags & 2) != 0)
        {
            room.DeadClientIds.Add(ownerId);
        }
        else
        {
            room.DeadClientIds.Remove(ownerId);
        }
    }

    private static void TrackInfectedPlayers(GameRoom room, ProtocolMessage message)
    {
        _ = message.ReadPackedInt32();
        byte callId = message.ReadByte();
        if (callId != 3)
        {
            return;
        }

        int infectedCount = message.ReadPackedInt32();
        if (infectedCount is < 0 or > 10)
        {
            throw new InvalidDataException("Impostor count is invalid.");
        }

        HashSet<int> impostors = new();
        for (int i = 0; i < infectedCount; i++)
        {
            int netId = message.ReadPackedInt32();
            if (!room.PlayerObjectOwners.TryGetValue(netId, out int ownerId) ||
                !room.Clients.Any(player => player.Id == ownerId))
            {
                return;
            }
            impostors.Add(ownerId);
        }

        room.ImpostorClientIds.Clear();
        room.ImpostorClientIds.UnionWith(impostors);
        room.RolesAssigned = true;
    }

    private static bool ContainsHostOnlyRpc(GameRoom room, byte[] gameData)
    {
        bool containsHostOnlyRpc = false;
        ProtocolCodec.ReadMessages(gameData, message =>
        {
            if (message.Tag != 2)
            {
                return;
            }

            int netId = message.ReadPackedInt32();
            byte callId = message.ReadByte();
            containsHostOnlyRpc |= room.PlayerControlNetIds.ContainsKey(netId) && callId is 2 or 3;
        });
        return containsHostOnlyRpc;
    }

    private void KickPlayer(Client client, ProtocolMessage message)
    {
        int gameId = message.ReadInt32();
        int targetId = message.ReadPackedInt32();
        bool ban = message.ReadBoolean();
        if (client.Room is not { } room || room.Id != gameId || !IsHost(client, room))
        {
            return;
        }

        Client? target = room.Clients.FirstOrDefault(candidate => candidate.Id == targetId);
        if (target is null)
        {
            return;
        }

        if (ban && target.Connection.EndPoint is NetworkEndPoint networkEndPoint &&
            networkEndPoint.EndPoint is IPEndPoint remoteEndPoint)
        {
            _bannedAddresses.Add(remoteEndPoint.Address);
        }

        byte[] kick = ProtocolCodec.Message(11, writer =>
        {
            writer.WriteInt32(gameId);
            writer.WritePackedInt32(targetId);
            writer.WriteBoolean(ban);
        });
        Broadcast(room, kick, null, SendOption.Reliable);
        target.Connection.Dispose();
    }

    private void EndGame(Client client, ProtocolMessage message)
    {
        int gameId = message.ReadInt32();
        if (client.Room is not { } room || room.Id != gameId || !IsHost(client, room))
        {
            return;
        }

        FinishGame(room, message.RawBytes);
    }

    private void FinishGame(GameRoom room, byte[] endGameMessage)
    {
        if (room.Ended)
        {
            return;
        }

        room.Ended = true;
        room.Started = false;
        Broadcast(room, endGameMessage, null, SendOption.Reliable);
        room.RejoinCandidates.Clear();
        foreach (Client player in room.Clients.ToArray())
        {
            player.Room = null;
            room.RejoinCandidates.Add(player);
        }
        room.Clients.Clear();
    }

    private bool TryReadGameId(Client client, ProtocolMessage message, out GameRoom? room)
    {
        int gameId = message.ReadInt32();
        room = client.Room;
        return room is not null && room.Id == gameId;
    }

    private static bool IsHost(Client client, GameRoom room) => room.Host == client;

    private void RemoveClientFromRoom(Client client, GameRoom? room)
    {
        if (room is null)
        {
            return;
        }

        bool wasMember = room.Clients.Remove(client);
        bool wasRejoinCandidate = room.Ended && room.RejoinCandidates.Remove(client);
        if (!wasMember && !wasRejoinCandidate)
        {
            return;
        }

        client.Room = null;
        if (room.Ended)
        {
            if (room.Host == client)
            {
                room.Host = room.RejoinCandidates.FirstOrDefault();
            }

            if (wasMember)
            {
                BroadcastPlayerLeft(room, client);
            }

            if (room.RejoinCandidates.Count == 0)
            {
                _rooms.Remove(room.Id);
            }
            else if (room.Host is { } host && room.Clients.Contains(host))
            {
                ReopenEndedGame(room, host);
            }
            return;
        }

        if (room.Clients.Count > 0 && room.Host == client)
        {
            room.Host = room.Clients[0];
        }

        BroadcastPlayerLeft(room, client);

        if (room.Started && room.RolesAssigned)
        {
            int impostorsRemaining = room.Clients.Count(player =>
                room.ImpostorClientIds.Contains(player.Id) && !room.DeadClientIds.Contains(player.Id));
            int crewRemaining = room.Clients.Count(player =>
                !room.ImpostorClientIds.Contains(player.Id) && !room.DeadClientIds.Contains(player.Id));
            if (impostorsRemaining == 0)
            {
                FinishGame(room, DisconnectEndGameMessage(room.Id, reason: 5));
            }
            else if (crewRemaining <= impostorsRemaining)
            {
                FinishGame(room, DisconnectEndGameMessage(room.Id, reason: 6));
            }
        }

        if (room.Clients.Count == 0 && (!room.Ended || room.RejoinCandidates.Count == 0))
        {
            _rooms.Remove(room.Id);
        }
    }

    private static byte[] DisconnectEndGameMessage(int gameId, byte reason)
    {
        return ProtocolCodec.Message(8, writer =>
        {
            writer.WriteInt32(gameId);
            writer.WriteByte(reason);
            writer.WriteBoolean(false);
        });
    }

    private static void BroadcastPlayerLeft(GameRoom room, Client client)
    {
        int hostId = room.Host?.Id ?? -1;
        byte[] left = ProtocolCodec.Message(4, writer =>
        {
            writer.WriteInt32(room.Id);
            writer.WriteInt32(client.Id);
            writer.WriteInt32(hostId);
        });
        Broadcast(room, left, null, SendOption.Reliable);
    }

    private void RemoveClientFromEndedRooms(Client client, GameRoom? exceptRoom)
    {
        foreach (GameRoom room in _rooms.Values.Where(room => room.Ended && room != exceptRoom).ToArray())
        {
            if (!room.RejoinCandidates.Remove(client))
            {
                continue;
            }

            if (room.Host == client)
            {
                room.Host = room.RejoinCandidates.FirstOrDefault();
            }

            if (room.RejoinCandidates.Count == 0)
            {
                _rooms.Remove(room.Id);
            }
            else if (room.Host is { } host && room.Clients.Contains(host))
            {
                ReopenEndedGame(room, host);
            }
        }
    }

    private void ReopenEndedGame(GameRoom room, Client host)
    {
        room.Ended = false;
        room.Started = false;
        room.RejoinCandidates.Clear();
        ResetRoundRoles(room);
        if (!room.Clients.Contains(host))
        {
            room.Clients.Add(host);
        }
        host.Room = room;

        Broadcast(room, PlayerJoinedMessage(room, host), host, SendOption.Reliable);
        foreach (Client player in room.Clients.ToArray())
        {
            Send(player.Connection, ProtocolCodec.Combine(JoinedMessage(room, player), GameVisibilityMessage(room)));
        }
    }

    private static void ResetRoundRoles(GameRoom room)
    {
        room.RolesAssigned = false;
        room.PlayerObjectOwners.Clear();
        room.PlayerControlNetIds.Clear();
        room.ImpostorClientIds.Clear();
        room.DeadClientIds.Clear();
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs eventArgs)
    {
        if (sender is not Connection connection)
        {
            return;
        }

        lock (_gate)
        {
            if (!_clients.Remove(connection, out Client? client))
            {
                return;
            }

            RemoveClientFromRoom(client, client.Room);
            RemoveClientFromEndedRooms(client, exceptRoom: null);
            Console.WriteLine($"Client {client.Id} disconnected.");
        }
    }

    private void OnAuxiliaryDisconnected(object? sender, DisconnectedEventArgs eventArgs)
    {
        if (sender is Connection connection)
        {
            lock (_gate)
            {
                _auxiliaryConnections.Remove(connection);
            }
        }
    }

    private static void Broadcast(GameRoom room, byte[] bytes, Client? except, SendOption sendOption)
    {
        foreach (Client client in room.Clients)
        {
            if (client != except)
            {
                Send(client.Connection, bytes, sendOption);
            }
        }
    }

    private static void Send(Connection connection, byte[] bytes, SendOption sendOption = SendOption.Reliable)
    {
        try
        {
            connection.SendBytes(bytes, sendOption);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HazelException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"Could not send to {connection.EndPoint}: {exception.Message}");
        }
    }

    private static void SendJoinFailure(Connection connection, int reason)
    {
        Send(connection, ProtocolCodec.Message(1, writer => writer.WriteInt32(reason)));
    }

    private static void SendDisconnect(Connection connection, int reason)
    {
        SendJoinFailure(connection, reason);
    }

    private int AllocateGameId()
    {
        Span<byte> name = stackalloc byte[sizeof(int)];
        for (int attempts = 0; attempts < 10000; attempts++)
        {
            for (int i = 0; i < name.Length; i++)
            {
                name[i] = (byte)Random.Shared.Next('A', 'Z' + 1);
            }

            int id = BinaryPrimitives.ReadInt32LittleEndian(name);
            if (!_rooms.ContainsKey(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique game code.");
    }

    private static bool TryReadHandshake(byte[] data, out string playerName)
    {
        playerName = string.Empty;
        const int reliableHeaderLength = 2;
        const int handshakePrefixLength = 1;
        int versionOffset = reliableHeaderLength + handshakePrefixLength;
        if (data.Length < versionOffset + sizeof(int) || data[reliableHeaderLength] != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(versionOffset, sizeof(int))) !=
            ServerConstants.ProtocolVersion)
        {
            return false;
        }

        try
        {
            int playerNameOffset = versionOffset + sizeof(int);
            using var stream = new MemoryStream(data, playerNameOffset, data.Length - playerNameOffset, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            playerName = reader.ReadString().Trim();
            return playerName.Length is > 0 and <= 50 && stream.Position == stream.Length;
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _listener.NewConnection -= OnNewConnection;
        _announcementListener.NewConnection -= OnAnnouncementConnection;
        Connection[] connections;
        lock (_gate)
        {
            connections = _clients.Keys.Concat(_auxiliaryConnections).Distinct().ToArray();
            foreach (Connection connection in connections)
            {
                connection.DataReceived -= OnDataReceived;
                connection.Disconnected -= OnDisconnected;
                connection.Disconnected -= OnAuxiliaryDisconnected;
            }
            _clients.Clear();
            _auxiliaryConnections.Clear();
            _rooms.Clear();
        }

        foreach (Connection connection in connections)
        {
            connection.Dispose();
        }

        _listener.Close();
        _listener.Dispose();
        _announcementListener.Close();
        _announcementListener.Dispose();
        _telemetryListener.Stop();
        _telemetryListener.Close();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private sealed class Client(int id, Connection connection, string name)
    {
        public int Id { get; } = id;
        public Connection Connection { get; } = connection;
        public string Name { get; } = name;
        public GameRoom? Room { get; set; }
    }

    private sealed class GameRoom(int id, Client? host)
    {
        public int Id { get; } = id;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public List<Client> Clients { get; } = new();
        public List<Client> RejoinCandidates { get; } = new();
        public Client? Host { get; set; } = host;
        public bool IsPublic { get; set; }
        public bool Started { get; set; }
        public bool Ended { get; set; }
        public bool RolesAssigned { get; set; }
        public Dictionary<int, int> PlayerObjectOwners { get; } = new();
        public Dictionary<int, int> PlayerControlNetIds { get; } = new();
        public HashSet<int> ImpostorClientIds { get; } = new();
        public HashSet<int> DeadClientIds { get; } = new();
    }
}

sealed class ProtocolMessage(byte tag, byte[] rawBytes, byte[] payload)
{
    private readonly BinaryReader _reader = new(new MemoryStream(payload, writable: false), Encoding.UTF8);
    private Stream Stream => _reader.BaseStream;

    public byte Tag { get; } = tag;
    public byte[] RawBytes { get; } = rawBytes;

    public int ReadInt32()
    {
        EnsureRemaining(sizeof(int));
        return _reader.ReadInt32();
    }

    public byte ReadByte()
    {
        EnsureRemaining(1);
        return _reader.ReadByte();
    }

    public bool ReadBoolean()
    {
        EnsureRemaining(1);
        return _reader.ReadBoolean();
    }

    public string ReadString()
    {
        int length = ReadPackedInt32();
        if (length < 0 || length > Stream.Length - Stream.Position)
        {
            throw new InvalidDataException("String length exceeds the message payload.");
        }
        byte[] bytes = _reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public int ReadPackedInt32()
    {
        uint value = 0;
        int shift = 0;
        while (shift < 35)
        {
            byte current = ReadByte();
            if (shift == 28 && (current & 0xF0) != 0)
            {
                throw new InvalidDataException("Packed integer exceeds 32 bits.");
            }
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return unchecked((int)value);
            }
            shift += 7;
        }
        throw new InvalidDataException("Packed integer is too long.");
    }

    public byte[] ReadRemainingBytes()
    {
        byte[] bytes = _reader.ReadBytes(checked((int)(Stream.Length - Stream.Position)));
        if (Stream.Position != Stream.Length)
        {
            throw new EndOfStreamException();
        }
        return bytes;
    }

    public ProtocolMessage ReadMessage()
    {
        EnsureRemaining(sizeof(ushort) + sizeof(byte));
        int payloadLength = _reader.ReadUInt16();
        byte tag = _reader.ReadByte();
        EnsureRemaining(payloadLength);
        byte[] raw = new byte[payloadLength + sizeof(ushort) + sizeof(byte)];
        BinaryPrimitives.WriteUInt16LittleEndian(raw, (ushort)payloadLength);
        raw[sizeof(ushort)] = tag;
        byte[] payload = _reader.ReadBytes(payloadLength);
        if (payload.Length != payloadLength)
        {
            throw new EndOfStreamException();
        }
        payload.CopyTo(raw, sizeof(ushort) + sizeof(byte));
        return new ProtocolMessage(tag, raw, payload);
    }

    private void EnsureRemaining(int count)
    {
        if (Stream.Position + count > Stream.Length)
        {
            throw new EndOfStreamException();
        }
    }
}

static class ProtocolCodec
{
    public static byte[] Message(byte tag, Action<PacketWriter> writePayload)
    {
        PacketWriter writer = new();
        writer.StartMessage(tag);
        writePayload(writer);
        writer.EndMessage();
        return writer.ToArray();
    }

    public static byte[] Combine(params byte[][] messages)
    {
        byte[] combined = new byte[messages.Sum(message => message.Length)];
        int offset = 0;
        foreach (byte[] message in messages)
        {
            message.CopyTo(combined, offset);
            offset += message.Length;
        }
        return combined;
    }

    public static void ReadMessages(byte[] data, Action<ProtocolMessage> onMessage)
    {
        int position = 0;
        while (position < data.Length)
        {
            if (data.Length - position < 3)
            {
                throw new InvalidDataException("Incomplete message header.");
            }

            int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
            int messageLength = payloadLength + 3;
            if (messageLength > data.Length - position)
            {
                throw new InvalidDataException("Message length exceeds the packet.");
            }

            byte[] raw = data.AsSpan(position, messageLength).ToArray();
            byte[] payload = data.AsSpan(position + 3, payloadLength).ToArray();
            byte tag = data[position + 2];
            onMessage(new ProtocolMessage(tag, raw, payload));
            position += messageLength;
        }
    }

    public static string GameName(int id)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, id);
        return Encoding.ASCII.GetString(bytes);
    }

    public static void RunSelfTests()
    {
        byte[] nested = Message(7, writer =>
        {
            writer.WriteInt32(32);
            writer.WriteInt32(2);
            writer.WriteInt32(2);
            writer.WritePackedInt32(1);
            writer.WritePackedInt32(9);
        });

        int seen = 0;
        ReadMessages(nested, message =>
        {
            if (message.Tag != 7 || message.ReadInt32() != 32 || message.ReadInt32() != 2 ||
                message.ReadInt32() != 2 || message.ReadPackedInt32() != 1 || message.ReadPackedInt32() != 9)
            {
                throw new InvalidOperationException("Message round-trip failed.");
            }
            seen++;
        });

        if (seen != 1 || GameName(unchecked((int)0x44434241)) != "ABCD")
        {
            throw new InvalidOperationException("Game-name encoding test failed.");
        }

        if (!Message(0, _ => { }).AsSpan().SequenceEqual(new byte[] { 0, 0, 0 }) ||
            !Message(0, writer => writer.WriteInt32(32)).AsSpan()
                .SequenceEqual(new byte[] { 4, 0, 0, 32, 0, 0, 0 }))
        {
            throw new InvalidOperationException("Message header encoding test failed.");
        }

        foreach (int value in new[] { int.MinValue, -1, 0, 1, 127, 128, 16384, int.MaxValue })
        {
            byte[] packed = Message(9, writer => writer.WritePackedInt32(value));
            ReadMessages(packed, message =>
            {
                if (message.ReadPackedInt32() != value)
                {
                    throw new InvalidOperationException($"Packed integer round-trip failed for {value}.");
                }
            });
        }
    }
}

ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _data = data;
    private int _position;

    public int ReadPackedInt32()
    {
        uint value = 0;
        int shift = 0;
        while (shift < 35)
        {
            if (_position >= _data.Length)
            {
                throw new InvalidDataException("Packed integer is truncated.");
            }
            byte current = _data[_position++];
            if (shift == 28 && (current & 0xF0) != 0)
            {
                throw new InvalidDataException("Packed integer exceeds 32 bits.");
            }
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return unchecked((int)value);
            }
            shift += 7;
        }
        throw new InvalidDataException("Packed integer is too long.");
    }

    public string ReadString()
    {
        int length = ReadPackedInt32();
        if (length < 0 || length > _data.Length - _position)
        {
            throw new InvalidDataException("String length exceeds the packet.");
        }
        string value = Encoding.UTF8.GetString(_data.Slice(_position, length));
        _position += length;
        return value;
    }
}

static class InnerNetIntegrationTest
{
    private static readonly Dictionary<Hazel.Udp.UdpClientConnection, System.Threading.Channels.Channel<byte[]>> Incoming = new();
    private static readonly object IncomingGate = new();

    public static async Task RunAsync()
    {
        int port = GetAvailablePort();
        await using InnerNetServer server = new(IPAddress.Loopback, port);
        using CancellationTokenSource serverStop = new();
        Task runServer = server.RunAsync(serverStop.Token);

        using Hazel.Udp.UdpClientConnection host = Connect("Host", port);
        int gameId = await CreateAndJoinHost(host);

        using Hazel.Udp.UdpClientConnection guest = Connect("Guest", port);
        guest.SendBytes(ProtocolCodec.Message(1, writer => writer.WriteInt32(gameId)), SendOption.Reliable);
        byte[] guestJoined = await ReceivePacket(guest);
        int hostId = -1;
        int guestId = -1;
        int guestJoinMessageCount = 0;
        ProtocolCodec.ReadMessages(guestJoined, message =>
        {
            if (guestJoinMessageCount == 0)
            {
                if (message.Tag != 7 || message.ReadInt32() != gameId)
                {
                    throw new InvalidOperationException("Guest join response was missing or out of order.");
                }
                guestId = message.ReadInt32();
                hostId = message.ReadInt32();
                if (message.ReadPackedInt32() != 1 || message.ReadPackedInt32() != hostId)
                {
                    throw new InvalidOperationException("Guest roster was invalid.");
                }
            }
            else if (guestJoinMessageCount == 1)
            {
                if (message.Tag != 10 || message.ReadInt32() != gameId ||
                    message.ReadByte() != 1 || message.ReadBoolean())
                {
                    throw new InvalidOperationException("Initial private visibility state was missing or out of order.");
                }
            }
            else
            {
                throw new InvalidOperationException("Guest join received unexpected extra messages.");
            }
            guestJoinMessageCount++;
        });
        if (guestJoinMessageCount != 2)
        {
            throw new InvalidOperationException("Guest did not receive both join and visibility messages.");
        }

        byte[] hostSawGuest = await ReceivePacket(host);
        RequireSingleMessage(hostSawGuest, 1, message =>
        {
            if (message.ReadInt32() != gameId)
            {
                throw new InvalidOperationException("Host received a join for the wrong game.");
            }
        });

        await RequireGameListCount(guest, expectedCount: 0);

        await SetRoomPublic(host, gameId, isPublic: true, guest);
        await RequireVisibleGame(guest, gameId);

        await SetRoomPublic(host, gameId, isPublic: false, guest);
        await RequireGameListCount(guest, expectedCount: 0);

        guest.SendBytes(ProtocolCodec.Message(10, writer =>
        {
            writer.WriteInt32(gameId);
            writer.WriteByte(1);
            writer.WriteBoolean(true);
        }), SendOption.Reliable);
        await AssertNoPacket(guest, "A non-host privacy change was acknowledged.");
        await RequireGameListCount(guest, expectedCount: 0);

        using Hazel.Udp.UdpClientConnection secondHost = Connect("Second Host", port);
        int secondGameId = await CreateAndJoinHost(secondHost);
        if (secondGameId == gameId)
        {
            throw new InvalidOperationException("Two concurrently hosted games received the same ID.");
        }
        await SetRoomPublic(secondHost, secondGameId, isPublic: true);
        await RequireVisibleGame(guest, secondGameId, expectedPlayers: 1, expectedHostName: "Second Host");

        await SetRoomPublic(host, gameId, isPublic: true, guest);
        await RequireGameListCount(guest, expectedCount: 2);
        await SetRoomPublic(secondHost, secondGameId, isPublic: false);
        await RequireVisibleGame(guest, gameId);

        byte[] hostPlayerSpawn = GameDataPacket(
            gameId, SpawnedPlayerObject(ownerId: hostId, netId: 101));
        host.SendBytes(hostPlayerSpawn, SendOption.Reliable);
        await RequireReceivedBytes(guest, hostPlayerSpawn);

        byte[] unauthorizedSettings = GameDataRpc(
            gameId, targetClientId: null, callIds: [2, 3], targetNetId: 101);
        guest.SendBytes(unauthorizedSettings, SendOption.Reliable);
        await AssertNoPacket(host, "A non-host settings RPC was relayed to the host.");

        byte[] targetedUnauthorizedSettings = GameDataRpc(gameId, hostId, callIds: [2], targetNetId: 101);
        guest.SendBytes(targetedUnauthorizedSettings, SendOption.Reliable);
        await AssertNoPacket(host, "A non-host targeted settings RPC was relayed to the host.");

        byte[] castVote = TargetedRpc(gameId, hostId, targetNetId: 900, callId: 2, payload: [7, 3]);
        guest.SendBytes(castVote, SendOption.Reliable);
        await RequireReceivedBytes(host, castVote);

        byte[] clearVote = TargetedRpc(
            gameId, targetClientId: guestId, targetNetId: 900, callId: 3, payload: []);
        host.SendBytes(clearVote, SendOption.Reliable);
        await RequireReceivedBytes(guest, clearVote);

        byte[] votingComplete = VotingCompleteRpc(gameId, targetNetId: 900, states: [1, 0]);
        host.SendBytes(votingComplete, SendOption.Reliable);
        await RequireReceivedBytes(guest, votingComplete);

        guest.SendBytes(ProtocolCodec.Message(2, writer => writer.WriteInt32(gameId)), SendOption.Reliable);
        await AssertNoPacket(host, "A non-host game-start request was relayed to the host.");
        await RequireVisibleGame(guest, gameId);

        byte[] authorizedSettings = GameDataRpc(
            gameId, targetClientId: null, callIds: [2, 3], targetNetId: 101);
        host.SendBytes(authorizedSettings, SendOption.Reliable);
        byte[] relayedSettings = await ReceivePacket(guest);
        if (!authorizedSettings.AsSpan().SequenceEqual(relayedSettings))
        {
            throw new InvalidOperationException("Host settings RPC was not relayed.");
        }

        byte[] gameData = ProtocolCodec.Message(5, writer =>
        {
            writer.WriteInt32(gameId);
            writer.StartMessage(1);
            writer.WritePackedInt32(42);
            writer.EndMessage();
        });
        host.SendBytes(gameData, SendOption.Reliable);
        byte[] relayedData = await ReceivePacket(guest);
        if (!gameData.AsSpan().SequenceEqual(relayedData))
        {
            throw new InvalidOperationException("Game data was not relayed byte-for-byte.");
        }

        byte[] hostStart = ProtocolCodec.Message(2, writer => writer.WriteInt32(gameId));
        host.SendBytes(hostStart, SendOption.Reliable);
        byte[] hostStartEcho = await ReceivePacket(host);
        if (!hostStart.AsSpan().SequenceEqual(hostStartEcho))
        {
            throw new InvalidOperationException("Host did not receive its start-game broadcast.");
        }
        byte[] relayedStart = await ReceivePacket(guest);
        if (!hostStart.AsSpan().SequenceEqual(relayedStart))
        {
            throw new InvalidOperationException("Host game-start message was not relayed.");
        }
        await SetRoomPublic(secondHost, secondGameId, isPublic: true);
        await RequireVisibleGame(guest, secondGameId, expectedPlayers: 1, expectedHostName: "Second Host");

        using Hazel.Udp.UdpClientConnection announcementClient = ConnectWithData([1, 0],
            ServerConstants.AnnouncementPort);
        SpanReader announcement = new(await ReceivePacket(announcementClient));
        if (announcement.ReadPackedInt32() != 1 || announcement.ReadString() != "Local offline server.")
        {
            throw new InvalidOperationException("Local announcement service returned an invalid response.");
        }
        Task announcementDisconnected = WaitForDisconnect(announcementClient);

        using HttpClient telemetryClient = new();
        using HttpResponseMessage telemetryResponse = await telemetryClient.PostAsync(
            ServerConstants.LocalTelemetryUrl,
            new StringContent("id=local&data=offline"));
        if (telemetryResponse.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException("Local telemetry sink returned an unexpected response.");
        }

        guest.Dispose();
        byte[] hostSawGuestLeave = await ReceivePacket(host);
        RequireSingleMessage(hostSawGuestLeave, 4, message =>
        {
            if (message.ReadInt32() != gameId)
            {
                throw new InvalidOperationException("Host received a leave for the wrong game.");
            }
        });

        await SetRoomPublic(secondHost, secondGameId, isPublic: false);
        await TestDisconnectEndgameAndPlayAgain(port);
        await TestCrewDisconnectEndgame(port);
        await TestSoleImpostorHostDisconnect(port);
        await TestJoinRejections(port);

        Task hostDisconnected = WaitForDisconnect(host);
        Task secondHostDisconnected = WaitForDisconnect(secondHost);
        server.Stop();
        await Task.WhenAll(hostDisconnected, secondHostDisconnected, announcementDisconnected)
            .WaitAsync(TimeSpan.FromSeconds(3));
        secondHost.Dispose();
        lock (IncomingGate)
        {
            Incoming.Clear();
        }
        serverStop.Cancel();
        try
        {
            await runServer;
        }
        catch (OperationCanceledException) when (serverStop.IsCancellationRequested)
        {
        }
        Console.WriteLine("Vote relay, host migration, play-again rejoin, sole-impostor disconnect endgames, host-only controls, matchmaking, relay, announcements, and telemetry tests passed.");
    }

    private static async Task TestDisconnectEndgameAndPlayAgain(int port)
    {
        using Hazel.Udp.UdpClientConnection impostorHost = Connect("Impostor Host", port);
        int gameId = await CreateAndJoinHost(impostorHost);

        using Hazel.Udp.UdpClientConnection impostorPeer = Connect("Impostor Peer", port);
        int hostId = await JoinRoom(impostorPeer, gameId);
        int impostorPeerId = await RequirePlayerJoined(impostorHost, gameId);

        using Hazel.Udp.UdpClientConnection firstCrew = Connect("First Crew", port);
        int firstCrewHostId = await JoinRoom(firstCrew, gameId);
        int firstCrewId = await RequirePlayerJoined(impostorHost, gameId);
        await RequirePlayerJoined(impostorPeer, gameId);

        using Hazel.Udp.UdpClientConnection secondCrew = Connect("Second Crew", port);
        int secondCrewHostId = await JoinRoom(secondCrew, gameId);
        int secondCrewId = await RequirePlayerJoined(impostorHost, gameId);
        await RequirePlayerJoined(impostorPeer, gameId);
        await RequirePlayerJoined(firstCrew, gameId);
        if (hostId != firstCrewHostId || hostId != secondCrewHostId)
        {
            throw new InvalidOperationException("Room host ID changed while joining players.");
        }

        await SetRoomPublic(impostorHost, gameId, isPublic: true, impostorPeer);
        await RequireVisibilityState(firstCrew, gameId, isPublic: true);
        await RequireVisibilityState(secondCrew, gameId, isPublic: true);

        byte[] start = ProtocolCodec.Message(2, writer => writer.WriteInt32(gameId));
        impostorHost.SendBytes(start, SendOption.Reliable);
        await RequireReceivedBytes(impostorHost, start);
        await RequireReceivedBytes(impostorPeer, start);
        await RequireReceivedBytes(firstCrew, start);
        await RequireReceivedBytes(secondCrew, start);

        byte[] spawnData = ProtocolCodec.Combine(
            SpawnedPlayerObject(ownerId: hostId, netId: 101),
            SpawnedPlayerObject(ownerId: impostorPeerId, netId: 102));
        impostorHost.SendBytes(GameDataPacket(gameId, spawnData), SendOption.Reliable);
        await ReceivePacket(impostorPeer);
        await ReceivePacket(firstCrew);
        await ReceivePacket(secondCrew);

        impostorHost.SendBytes(GameDataPacket(gameId, SetInfectedRpc(101, 102)), SendOption.Reliable);
        await ReceivePacket(impostorPeer);
        await ReceivePacket(firstCrew);
        await ReceivePacket(secondCrew);

        impostorHost.Dispose();
        await RequirePlayerLeft(impostorPeer, gameId, hostId, impostorPeerId);
        await RequirePlayerLeft(firstCrew, gameId, hostId, impostorPeerId);
        await RequirePlayerLeft(secondCrew, gameId, hostId, impostorPeerId);

        impostorPeer.Dispose();
        await RequirePlayerLeft(firstCrew, gameId, impostorPeerId, firstCrewId);
        await RequirePlayerLeft(secondCrew, gameId, impostorPeerId, firstCrewId);
        await RequireEndGame(firstCrew, gameId, reason: 5);
        await RequireEndGame(secondCrew, gameId, reason: 5);

        secondCrew.SendBytes(ProtocolCodec.Message(1, writer => writer.WriteInt32(gameId)), SendOption.Reliable);
        RequireSingleMessage(await ReceivePacket(secondCrew), 12, message =>
        {
            if (message.ReadInt32() != gameId || message.ReadInt32() != secondCrewId)
            {
                throw new InvalidOperationException("A non-host play-again request did not wait for the host.");
            }
        });

        firstCrew.SendBytes(ProtocolCodec.Message(1, writer => writer.WriteInt32(gameId)), SendOption.Reliable);
        RequireJoinedAndVisible(
            await ReceivePacket(firstCrew), gameId, firstCrewId, firstCrewId, secondCrewId, isPublic: true);
        await RequireRejoinedWaitingGuest(secondCrew, gameId, secondCrewId, firstCrewId, isPublic: true);
        await RequireVisibleGame(secondCrew, gameId, expectedPlayers: 2, expectedHostName: "First Crew");
    }

    private static async Task TestCrewDisconnectEndgame(int port)
    {
        using Hazel.Udp.UdpClientConnection impostor = Connect("Crew Test Host", port);
        int gameId = await CreateAndJoinHost(impostor);

        using Hazel.Udp.UdpClientConnection crew = Connect("Remaining Crew", port);
        int hostId = await JoinRoom(crew, gameId);
        int crewId = await RequirePlayerJoined(impostor, gameId);

        using Hazel.Udp.UdpClientConnection departingCrew = Connect("Departing Crew", port);
        int departingHostId = await JoinRoom(departingCrew, gameId);
        int departingCrewId = await RequirePlayerJoined(impostor, gameId);
        await RequirePlayerJoined(crew, gameId);
        if (hostId != departingHostId)
        {
            throw new InvalidOperationException("Crew test room host ID changed unexpectedly.");
        }

        byte[] start = ProtocolCodec.Message(2, writer => writer.WriteInt32(gameId));
        impostor.SendBytes(start, SendOption.Reliable);
        await RequireReceivedBytes(impostor, start);
        await RequireReceivedBytes(crew, start);
        await RequireReceivedBytes(departingCrew, start);
        impostor.SendBytes(GameDataPacket(gameId, SpawnedPlayerObject(ownerId: hostId, netId: 201)),
            SendOption.Reliable);
        await ReceivePacket(crew);
        await ReceivePacket(departingCrew);
        impostor.SendBytes(GameDataPacket(gameId, SetInfectedRpc(201)), SendOption.Reliable);
        await ReceivePacket(crew);
        await ReceivePacket(departingCrew);

        departingCrew.Dispose();
        await RequirePlayerLeft(impostor, gameId, departingCrewId, hostId);
        await RequirePlayerLeft(crew, gameId, departingCrewId, hostId);
        await RequireEndGame(impostor, gameId, reason: 6);
        await RequireEndGame(crew, gameId, reason: 6);
        _ = crewId;
    }

    private static async Task TestSoleImpostorHostDisconnect(int port)
    {
        using Hazel.Udp.UdpClientConnection impostorHost = Connect("Sole Impostor Host", port);
        int gameId = await CreateAndJoinHost(impostorHost);

        using Hazel.Udp.UdpClientConnection crew = Connect("Crew Player", port);
        int hostId = await JoinRoom(crew, gameId);
        int crewId = await RequirePlayerJoined(impostorHost, gameId);

        byte[] lobbyPlayerSpawns = ProtocolCodec.Combine(
            SpawnedPlayerObject(ownerId: hostId, netId: 301),
            SpawnedPlayerObject(ownerId: crewId, netId: 302));
        impostorHost.SendBytes(GameDataPacket(gameId, lobbyPlayerSpawns), SendOption.Reliable);
        await ReceivePacket(crew);

        byte[] start = ProtocolCodec.Message(2, writer => writer.WriteInt32(gameId));
        impostorHost.SendBytes(start, SendOption.Reliable);
        await RequireReceivedBytes(impostorHost, start);
        await RequireReceivedBytes(crew, start);

        impostorHost.SendBytes(GameDataPacket(gameId, SetInfectedRpc(301)), SendOption.Reliable);
        await ReceivePacket(crew);

        impostorHost.Dispose();
        await RequirePlayerLeftAndEndGame(
            crew, gameId, leavingClientId: hostId, expectedHostId: crewId, reason: 5);
    }

    private static async Task TestJoinRejections(int port)
    {
        using Hazel.Udp.UdpClientConnection startedHost = Connect("Started Host", port);
        int startedGameId = await CreateAndJoinHost(startedHost);
        startedHost.SendBytes(ProtocolCodec.Message(2, writer => writer.WriteInt32(startedGameId)),
            SendOption.Reliable);
        await RequireReceivedBytes(startedHost,
            ProtocolCodec.Message(2, writer => writer.WriteInt32(startedGameId)));

        using (Hazel.Udp.UdpClientConnection startedJoiner = Connect("Late Joiner", port))
        {
            startedJoiner.SendBytes(
                ProtocolCodec.Message(1, writer => writer.WriteInt32(startedGameId)), SendOption.Reliable);
            await RequireJoinFailure(startedJoiner, reason: 2);
        }

    }

    private static async Task RequireJoinFailure(Hazel.Udp.UdpClientConnection client, int reason)
    {
        RequireSingleMessage(await ReceivePacket(client), 1, message =>
        {
            if (message.ReadInt32() != reason)
            {
                throw new InvalidOperationException($"Expected join rejection reason {reason}.");
            }
        });
    }

    private static async Task<int> JoinRoom(Hazel.Udp.UdpClientConnection client, int gameId)
    {
        client.SendBytes(ProtocolCodec.Message(1, writer => writer.WriteInt32(gameId)), SendOption.Reliable);
        int clientId = 0;
        int hostId = 0;
        int messageCount = 0;
        ProtocolCodec.ReadMessages(await ReceivePacket(client), message =>
        {
            if (messageCount == 0)
            {
                if (message.Tag != 7 || message.ReadInt32() != gameId)
                {
                    throw new InvalidOperationException("Client did not receive a valid room join.");
                }
                clientId = message.ReadInt32();
                hostId = message.ReadInt32();
                int others = message.ReadPackedInt32();
                for (int i = 0; i < others; i++)
                {
                    _ = message.ReadPackedInt32();
                }
            }
            else if (messageCount == 1)
            {
                if (message.Tag != 10 || message.ReadInt32() != gameId ||
                    message.ReadByte() != 1 || message.ReadBoolean())
                {
                    throw new InvalidOperationException("Client received invalid initial room visibility.");
                }
            }
            else
            {
                throw new InvalidOperationException("Client received unexpected room join messages.");
            }
            messageCount++;
        });
        if (messageCount != 2 || clientId <= 0 || hostId <= 0)
        {
            throw new InvalidOperationException("Client join response was incomplete.");
        }
        return hostId;
    }

    private static async Task<int> RequirePlayerJoined(Hazel.Udp.UdpClientConnection client, int gameId)
    {
        int joinedClientId = 0;
        RequireSingleMessage(await ReceivePacket(client), 1, message =>
        {
            if (message.ReadInt32() != gameId)
            {
                throw new InvalidOperationException("Player joined the wrong game.");
            }
            joinedClientId = message.ReadInt32();
            _ = message.ReadInt32();
        });
        return joinedClientId;
    }

    private static async Task RequirePlayerLeft(
        Hazel.Udp.UdpClientConnection client,
        int gameId,
        int leavingClientId,
        int expectedHostId)
    {
        RequireSingleMessage(await ReceivePacket(client), 4, message =>
        {
            if (message.ReadInt32() != gameId || message.ReadInt32() != leavingClientId ||
                message.ReadInt32() != expectedHostId)
            {
                throw new InvalidOperationException("Player leave did not migrate the host in join order.");
            }
        });
    }

    private static async Task RequireEndGame(
        Hazel.Udp.UdpClientConnection client,
        int gameId,
        byte reason)
    {
        RequireSingleMessage(await ReceivePacket(client), 8, message =>
        {
            if (message.ReadInt32() != gameId || message.ReadByte() != reason || message.ReadBoolean())
            {
                throw new InvalidOperationException($"Expected disconnect end-game reason {reason}.");
            }
        });
    }

    private static async Task RequirePlayerLeftAndEndGame(
        Hazel.Udp.UdpClientConnection client,
        int gameId,
        int leavingClientId,
        int expectedHostId,
        byte reason)
    {
        int expectedMessage = 0;
        while (expectedMessage < 2)
        {
            ProtocolCodec.ReadMessages(await ReceivePacket(client), message =>
            {
                if (expectedMessage == 0)
                {
                    if (message.Tag != 4 || message.ReadInt32() != gameId ||
                        message.ReadInt32() != leavingClientId || message.ReadInt32() != expectedHostId)
                    {
                        throw new InvalidOperationException("Host migration message was invalid.");
                    }
                }
                else if (message.Tag != 8 || message.ReadInt32() != gameId ||
                         message.ReadByte() != reason || message.ReadBoolean())
                {
                    throw new InvalidOperationException($"Expected disconnect end-game reason {reason}.");
                }
                expectedMessage++;
            });
        }
    }

    private static byte[] SpawnedPlayerObject(int ownerId, int netId)
    {
        return ProtocolCodec.Message(4, writer =>
        {
            writer.WritePackedInt32(1);
            writer.WritePackedInt32(ownerId);
            writer.WriteByte(1);
            writer.WritePackedInt32(1);
            writer.WritePackedInt32(netId);
            writer.StartMessage(1);
            writer.EndMessage();
        });
    }

    private static byte[] SetInfectedRpc(params int[] netIds)
    {
        return ProtocolCodec.Message(2, writer =>
        {
            writer.WritePackedInt32(1);
            writer.WriteByte(3);
            writer.WritePackedInt32(netIds.Length);
            foreach (int netId in netIds)
            {
                writer.WritePackedInt32(netId);
            }
        });
    }

    private static byte[] GameDataPacket(int gameId, byte[] gameData)
    {
        return ProtocolCodec.Message(5, writer =>
        {
            writer.WriteInt32(gameId);
            writer.WriteBytes(gameData);
        });
    }

    private static async Task RequireReceivedBytes(Hazel.Udp.UdpClientConnection client, byte[] expected)
    {
        byte[] actual = await ReceivePacket(client);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException("Client received unexpected relayed game data.");
        }
    }

    private static void RequireJoinedAndVisible(
        byte[] packet,
        int gameId,
        int clientId,
        int hostId,
        int otherClientId,
        bool isPublic = false)
    {
        int messageCount = 0;
        ProtocolCodec.ReadMessages(packet, message =>
        {
            if (messageCount == 0)
            {
                if (message.Tag != 7 || message.ReadInt32() != gameId || message.ReadInt32() != clientId ||
                    message.ReadInt32() != hostId || message.ReadPackedInt32() != 1 ||
                    message.ReadPackedInt32() != otherClientId)
                {
                    throw new InvalidOperationException("Play-again did not restore the expected lobby roster.");
                }
            }
            else if (messageCount == 1)
            {
                if (message.Tag != 10 || message.ReadInt32() != gameId ||
                    message.ReadByte() != 1 || message.ReadBoolean() != isPublic)
                {
                    throw new InvalidOperationException("Play-again did not preserve the room visibility.");
                }
            }
            else
            {
                throw new InvalidOperationException("Play-again sent unexpected extra messages.");
            }
            messageCount++;
        });
        if (messageCount != 2)
        {
            throw new InvalidOperationException("Play-again lobby state was incomplete.");
        }
    }

    private static async Task RequireRejoinedWaitingGuest(
        Hazel.Udp.UdpClientConnection client,
        int gameId,
        int clientId,
        int hostId,
        bool isPublic = false)
    {
        int messageCount = 0;
        while (messageCount < 3)
        {
            ProtocolCodec.ReadMessages(await ReceivePacket(client), message =>
            {
                if (messageCount == 0)
                {
                    if (message.Tag != 1 || message.ReadInt32() != gameId ||
                        message.ReadInt32() != hostId || message.ReadInt32() != hostId)
                    {
                        throw new InvalidOperationException(
                            $"The remade lobby did not announce its new host (tag {message.Tag}).");
                    }
                }
                else if (messageCount == 1)
                {
                    if (message.Tag != 7 || message.ReadInt32() != gameId || message.ReadInt32() != clientId ||
                        message.ReadInt32() != hostId || message.ReadPackedInt32() != 1 ||
                        message.ReadPackedInt32() != hostId)
                    {
                        throw new InvalidOperationException("The waiting client received an invalid lobby roster.");
                    }
                }
                else if (messageCount == 2)
                {
                    if (message.Tag != 10 || message.ReadInt32() != gameId ||
                        message.ReadByte() != 1 || message.ReadBoolean() != isPublic)
                    {
                        throw new InvalidOperationException("The waiting client received incorrect lobby visibility.");
                    }
                }
                else
                {
                    throw new InvalidOperationException("The waiting client received unexpected lobby messages.");
                }
                messageCount++;
            });
        }
    }

    private static Hazel.Udp.UdpClientConnection Connect(string playerName, int port)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ServerConstants.ProtocolVersion);
            writer.Write(playerName);
        }

        return ConnectWithData(stream.ToArray(), port);
    }

    private static Hazel.Udp.UdpClientConnection ConnectWithData(byte[] connectData, int port)
    {
        var client = new Hazel.Udp.UdpClientConnection(
            new NetworkEndPoint(IPAddress.Loopback, port, IPMode.IPv4));
        System.Threading.Channels.Channel<byte[]> incoming =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        client.DataReceived += (_, eventArgs) => incoming.Writer.TryWrite(eventArgs.Bytes);
        lock (IncomingGate)
        {
            Incoming.Add(client, incoming);
        }
        client.Connect(connectData, 5000);
        return client;
    }

    private static async Task<int> CreateAndJoinHost(Hazel.Udp.UdpClientConnection host)
    {
        host.SendBytes(ProtocolCodec.Message(0, _ => { }), SendOption.Reliable);
        byte[] created = await ReceivePacket(host);
        int gameId = 0;
        RequireSingleMessage(created, 0, message => gameId = message.ReadInt32());

        host.SendBytes(ProtocolCodec.Message(1, writer => writer.WriteInt32(gameId)), SendOption.Reliable);
        byte[] joined = await ReceivePacket(host);
        int joinedMessageCount = 0;
        ProtocolCodec.ReadMessages(joined, message =>
        {
            if (joinedMessageCount == 0)
            {
                if (message.Tag != 7 || message.ReadInt32() != gameId || message.ReadInt32() <= 0 ||
                    message.ReadInt32() <= 0 || message.ReadPackedInt32() != 0)
                {
                    throw new InvalidOperationException("Host join response was invalid.");
                }
            }
            else if (joinedMessageCount == 1)
            {
                if (message.Tag != 10 || message.ReadInt32() != gameId ||
                    message.ReadByte() != 1 || message.ReadBoolean())
                {
                    throw new InvalidOperationException("Host initial visibility response was invalid.");
                }
            }
            else
            {
                throw new InvalidOperationException("Host join received unexpected extra messages.");
            }
            joinedMessageCount++;
        });
        if (joinedMessageCount != 2)
        {
            throw new InvalidOperationException("Host did not receive both join and visibility messages.");
        }
        return gameId;
    }

    private static byte[] GameDataRpc(
        int gameId,
        int? targetClientId,
        byte[] callIds,
        int targetNetId = 1)
    {
        return ProtocolCodec.Message(targetClientId is null ? (byte)5 : (byte)6, writer =>
        {
            writer.WriteInt32(gameId);
            if (targetClientId is int targetId)
            {
                writer.WritePackedInt32(targetId);
            }

            foreach (byte callId in callIds)
            {
                writer.StartMessage(2);
                writer.WritePackedInt32(targetNetId);
                writer.WriteByte(callId);
                writer.WritePackedInt32(1);
                writer.WriteByte(0);
                writer.EndMessage();
            }
        });
    }

    private static byte[] TargetedRpc(
        int gameId,
        int targetClientId,
        int targetNetId,
        byte callId,
        byte[] payload)
    {
        return ProtocolCodec.Message(6, writer =>
        {
            writer.WriteInt32(gameId);
            writer.WritePackedInt32(targetClientId);
            writer.StartMessage(2);
            writer.WritePackedInt32(targetNetId);
            writer.WriteByte(callId);
            writer.WriteBytes(payload);
            writer.EndMessage();
        });
    }

    private static byte[] VotingCompleteRpc(int gameId, int targetNetId, byte[] states)
    {
        return ProtocolCodec.Message(5, writer =>
        {
            writer.WriteInt32(gameId);
            writer.StartMessage(2);
            writer.WritePackedInt32(targetNetId);
            writer.WriteByte(1);
            writer.WritePackedInt32(states.Length);
            writer.WriteBytes(states);
            writer.WritePackedInt32(0);
            writer.WriteBoolean(false);
            writer.EndMessage();
        });
    }

    private static async Task SetRoomPublic(
        Hazel.Udp.UdpClientConnection host,
        int gameId,
        bool isPublic,
        Hazel.Udp.UdpClientConnection? roomPeer = null)
    {
        host.SendBytes(ProtocolCodec.Message(10, writer =>
        {
            writer.WriteInt32(gameId);
            writer.WriteByte(1);
            writer.WriteBoolean(isPublic);
        }), SendOption.Reliable);
        await RequireVisibilityState(host, gameId, isPublic);
        if (roomPeer is not null)
        {
            await RequireVisibilityState(roomPeer, gameId, isPublic);
        }
    }

    private static async Task RequireVisibilityState(
        Hazel.Udp.UdpClientConnection client,
        int gameId,
        bool isPublic)
    {
        RequireSingleMessage(await ReceivePacket(client), 10, message =>
        {
            if (message.ReadInt32() != gameId || message.ReadByte() != 1 || message.ReadBoolean() != isPublic)
            {
                throw new InvalidOperationException(
                    $"Expected room {ProtocolCodec.GameName(gameId)} visibility to be {isPublic}.");
            }
        });
    }

    private static async Task RequireGameListCount(Hazel.Udp.UdpClientConnection client, int expectedCount)
    {
        client.SendBytes(ProtocolCodec.Message(9, writer => writer.WriteBoolean(false)), SendOption.Reliable);
        RequireSingleMessage(await ReceivePacket(client), 9, message =>
        {
            int count = message.ReadPackedInt32();
            if (count != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Expected {expectedCount} public match(es), but the server listed {count}.");
            }
        });
    }

    private static async Task RequireVisibleGame(
        Hazel.Udp.UdpClientConnection client,
        int gameId,
        int expectedPlayers = 2,
        string expectedHostName = "Host",
        int expectedGameCount = 1)
    {
        client.SendBytes(ProtocolCodec.Message(9, writer => writer.WriteBoolean(false)), SendOption.Reliable);
        RequireSingleMessage(await ReceivePacket(client), 9, message =>
        {
            int count = message.ReadPackedInt32();
            List<string> listings = new();
            int? foundPlayerCount = null;
            string? foundHostName = null;
            for (int i = 0; i < count; i++)
            {
                int listedId = message.ReadInt32();
                int listedPlayers = message.ReadPackedInt32();
                _ = message.ReadPackedInt32();
                string listedHost = message.ReadString();
                listings.Add(ProtocolCodec.GameName(listedId));
                if (listedId == gameId)
                {
                    foundPlayerCount = listedPlayers;
                    foundHostName = listedHost;
                }
            }

            if (count != expectedGameCount || foundPlayerCount != expectedPlayers ||
                foundHostName != expectedHostName)
            {
                throw new InvalidOperationException(
                    $"Expected only {ProtocolCodec.GameName(gameId)} ({expectedPlayers}, {expectedHostName}); " +
                    $"received {count} listing(s): {string.Join(", ", listings)}.");
            }
        });
    }

    private static async Task<byte[]> ReceivePacket(Hazel.Udp.UdpClientConnection connection)
    {
        System.Threading.Channels.Channel<byte[]> incoming;
        lock (IncomingGate)
        {
            incoming = Incoming[connection];
        }
        return await incoming.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AssertNoPacket(Hazel.Udp.UdpClientConnection connection, string error)
    {
        System.Threading.Channels.Channel<byte[]> incoming;
        lock (IncomingGate)
        {
            incoming = Incoming[connection];
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(250));
        try
        {
            byte[] packet = await incoming.Reader.ReadAsync(timeout.Token);
            throw new InvalidOperationException($"{error} Unexpected packet: {Convert.ToHexString(packet)}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
    }

    private static Task WaitForDisconnect(Hazel.Udp.UdpClientConnection connection)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += (_, _) => completion.TrySetResult();
        return completion.Task;
    }

    private static void RequireSingleMessage(byte[] packet, byte expectedTag, Action<ProtocolMessage> validate)
    {
        int messages = 0;
        ProtocolCodec.ReadMessages(packet, message =>
        {
            if (message.Tag != expectedTag)
            {
                throw new InvalidOperationException($"Expected message tag {expectedTag}, got {message.Tag}.");
            }
            validate(message);
            messages++;
        });
        if (messages != 1)
        {
            throw new InvalidOperationException($"Expected one message, got {messages}.");
        }
    }

    private static int GetAvailablePort()
    {
        using var socket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}

static class ServerConstants
{
    public const int ProtocolVersion = 36947700;
    public const int DefaultPort = 22023;
    public const int AnnouncementPort = 22024;
    public const int TelemetryPort = 22025;
    public const string RetiredAddress = "18.218.20.65";
    public const string LocalAddress = "127.0.0.1";
    public const string LocalTelemetryUrl = "http://127.0.0.1:22025/telemetry.php";
}

sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;
    private readonly Stack<long> _messages = new();

    public PacketWriter()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public void StartMessage(byte tag)
    {
        long lengthPosition = _stream.Position;
        _writer.Write((ushort)0);
        _writer.Write(tag);
        _messages.Push(lengthPosition);
    }

    public void EndMessage()
    {
        if (_messages.Count == 0)
        {
            throw new InvalidOperationException("No message is open.");
        }

        long lengthPosition = _messages.Pop();
        long endPosition = _stream.Position;
        long payloadLength = endPosition - (lengthPosition + sizeof(ushort) + sizeof(byte));
        if (payloadLength > ushort.MaxValue)
        {
            throw new InvalidOperationException("Message payload exceeds the protocol maximum.");
        }

        _stream.Position = lengthPosition;
        _writer.Write((ushort)payloadLength);
        _stream.Position = endPosition;
    }

    public void WriteInt32(int value) => _writer.Write(value);
    public void WriteByte(byte value) => _writer.Write(value);
    public void WriteBoolean(bool value) => _writer.Write(value);
    public void WriteBytes(byte[] value) => _writer.Write(value);

    public void WritePackedInt32(int value)
    {
        uint remaining = unchecked((uint)value);
        while (remaining >= 0x80)
        {
            _writer.Write((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        _writer.Write((byte)remaining);
    }

    public void WriteString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WritePackedInt32(bytes.Length);
        _writer.Write(bytes);
    }

    public byte[] ToArray()
    {
        if (_messages.Count != 0)
        {
            throw new InvalidOperationException("A message is still open.");
        }

        return _stream.ToArray();
    }
}
