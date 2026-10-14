using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;

if (args.Length is 4 or 5 && args[0] == "replay")
{
    MotionReplay.Run(args[1], args[2], int.Parse(args[3]), args.Length == 5 ? int.Parse(args[4]) : 6000);
    return;
}
if (args.Length == 5 && args[0] == "route")
{
    MotionReplay.Run(args[1], args[2], int.Parse(args[3]), 80,
        args[4].Split(',').Select(int.Parse).ToArray());
    return;
}
if (args.Length != 2 || args[0] != "watch")
    throw new ArgumentException("watch OUTPUT.jsonl | replay DIRECTORY OUTPUT.jsonl NPC_ID [TICKS] | route DIRECTORY OUTPUT.jsonl NPC_ID NODE_IDS_CSV");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
using var socket = new ClientWebSocket();
socket.Options.AddSubProtocol("hexlive.v1");
socket.Options.SetRequestHeader("X-HexLive-Accepts", "gzip");
await socket.ConnectAsync(new Uri("wss://play.62-146-235-120.sslip.io/watch"), timeout.Token);
Handshake? handshake = null;
WorldSnapshot? snapshot = null;
var count = 0;
using var output = new StreamWriter(args[1], false, new UTF8Encoding(false));
var watch = Stopwatch.StartNew();
while (watch.Elapsed.TotalSeconds < 20 || snapshot == null)
{
    using var message = new MemoryStream();
    var buffer = new byte[64 * 1024];
    WebSocketReceiveResult part;
    do
    {
        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        if (part.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Expected binary world stream");
        message.Write(buffer, 0, part.Count);
        if (message.Length > 64 * 1024 * 1024) throw new InvalidDataException("Frame exceeds bound");
    } while (!part.EndOfMessage);
    var bytes = message.ToArray();
    if ((FrameKind)bytes[0] == FrameKind.Compressed) bytes = Frame.Decompress(bytes[1..]);
    using var reader = new BinaryReader(new MemoryStream(bytes[1..]), Encoding.UTF8);
    var kind = (FrameKind)bytes[0];
    if (kind == FrameKind.Handshake)
    {
        handshake = Handshake.Read(reader);
        if (handshake.ControlEnabled || handshake.AssignedNpcIds.Count != 0)
            throw new InvalidDataException("Motion observer must remain anonymous without assignments");
        output.WriteLine(JsonSerializer.Serialize(new { type = "handshake", utc = DateTime.UtcNow,
            world = handshake.WorldId, seed = handshake.Seed, tick = handshake.Tick, protocol = Handshake.ProtocolVersion,
            controlEnabled = false, assignedNpcCount = 0, ordersIssued = false }));
    }
    else if (kind == FrameKind.Snapshot)
    {
        snapshot = new WorldSnapshot(); WorldSnapshotCodec.Read(reader, snapshot);
    }
    else if (kind == FrameKind.SnapshotDelta)
    {
        if (snapshot == null) throw new InvalidDataException("Delta before full snapshot");
        SnapshotDeltaReader.Apply(reader, snapshot, snapshot.Tick);
    }
    else continue;
    if (snapshot == null || kind == FrameKind.Handshake) continue;
    if (count++ == 0)
        Console.WriteLine(JsonSerializer.Serialize(new { type = "roster", tick = snapshot.Tick,
            npcs = snapshot.Npcs.Select(n => new { id = n.Id.Value, name = n.DisplayName, profile = n.ProfileId,
                x = n.Position.X, z = n.Position.Y, rotation = n.RotationDegrees,
                goal = n.CurrentGoal, move = n.MovementStatus, target = n.TargetObjectId }) }));
    output.WriteLine(JsonSerializer.Serialize(new { type = "motion", utc = DateTime.UtcNow, tick = snapshot.Tick,
        npcs = snapshot.Npcs.Select(n => new { id = n.Id.Value, x = n.Position.X, z = n.Position.Y,
            rotation = n.RotationDegrees, goal = n.CurrentGoal, move = n.MovementStatus,
            interaction = n.CurrentInteraction, execution = n.ExecutionStatus, target = n.TargetObjectId }) }));
}
await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Read-only motion trace complete", timeout.Token);
Console.WriteLine(JsonSerializer.Serialize(new { type = "complete", frames = count, seconds = watch.Elapsed.TotalSeconds,
    world = handshake?.WorldId, ordersIssued = false, output = Path.GetFullPath(args[1]) }));
