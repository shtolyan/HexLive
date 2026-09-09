using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

SimDataFile.Require(Path.Combine(AppContext.BaseDirectory, "simdata.json"));
if (args.Length != 2) throw new ArgumentException("Expected: <source world.dat> <output directory>");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
const string expectedSha = "d403257bd7965bc172199fdf45f034e7e843fdc2397de58bfd095bd6ee74890f";
const string commit = "2236e4a97a72f125d4cb889a398558666fc533d3";
const int seed = -12054716;
const int tick = 5812;
var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
var bytes = File.ReadAllBytes(source);
if (Hash(bytes) != expectedSha) throw new InvalidDataException("Source SHA mismatch.");
if (WorldSaveSerializer.BlobVersion != 64) throw new InvalidDataException("Wrong historical assembly.");
Directory.CreateDirectory(output);
var restored = Restore();
var savedSleeper = restored.Entities.Npcs[new EntityId(2)];
if (savedSleeper.Execution.CurrentInteraction != InteractionType.Sleep || !savedSleeper.Execution.TargetObject.HasValue)
    throw new InvalidDataException("Saved NPC2 is not sleeping on a target object.");
var savedBed = restored.Entities.Objects[savedSleeper.Execution.TargetObject.Value];
var hutBeds = restored.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.BedBasic && (o.Variant == ContentIds.HutBedVariant || restored.Tiles.Items[o.Tile].Flags.HasFlag(TileFlags.HasFloor))).OrderBy(o => o.Id.Value).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new { npcs = restored.Entities.Npcs.Values.Select(n => new { id=n.Id.Value, interaction=n.Execution.CurrentInteraction.ToString(), target=n.Execution.TargetObject?.Value, position=n.Position }), beds = restored.Entities.Objects.Values.Where(o=>ContentIds.IsBed(o.DefinitionId)).Select(o=>new { id=o.Id.Value, o.DefinitionId, o.Variant, o.Tile, o.RotationDegrees, o.IsOccupied, user=o.CurrentUser?.Value }) }, options));
if (hutBeds.Length != 2) throw new InvalidDataException($"Expected exactly two saved hut beds, found {hutBeds.Length}.");
var cases = new List<object>();
Export(restored, "saved-npc2", 2, savedBed.Id.Value, false, "Canonical v64 Read and Export; no engine ticks or scenario modifications.");
foreach (var originalBed in hutBeds)
{
    var world = Restore();
    var bed = world.Entities.Objects[originalBed.Id];
    var npc = world.Entities.Npcs[new EntityId(1)];
    var stopped = new List<int>();
    if (bed.CurrentUser is { } occupant && occupant != npc.Id)
    {
        Stop(world, occupant);
        stopped.Add(occupant.Value);
    }
    Stop(world, npc.Id);
    stopped.Add(npc.Id.Value);
    if (!BedSleep.TryEnter(world, npc, bed, world.Tick + 120, npc.CurrentJunction))
        throw new InvalidDataException($"Historical BedSleep refused saved bed {bed.Id.Value}.");
    Export(world, $"controlled-npc1-bed-{bed.Id.Value}", 1, bed.Id.Value, true,
        $"Independent restore, historical SetManualControl+Stop for NPCs {string.Join(',', stopped)}, then BedSleep.TryEnter(NPC1, saved bed, tick+120). No engine ticks or manual pose edits.");
}
var manifest = new {
    sourceSaveSha256 = expectedSha, sourceSavePath = source, sourceBytes = bytes.Length,
    outerVersion = 3, blobVersion = 64, serializerCommit = commit, seed, tick,
    mode = "Feud", modeValue = 0,
    simDataSha256 = Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "simdata.json"))),
    serializerSourceSha256 = Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "WorldSaveSerializer.cs"))),
    assemblySha256 = Hash(File.ReadAllBytes(typeof(WorldSaveSerializer).Assembly.Location)),
    exportedAtUtc = DateTime.UtcNow, cases,
    limitation = "Historical v64 reader/exporter, not a production migration. Controlled cases are explicitly prepared independent copies; no source save write, no version substitution, no simulation steps."
};
File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, options));
if (Hash(File.ReadAllBytes(source)) != expectedSha) throw new InvalidDataException("Source changed during export.");
Console.WriteLine($"Exported {cases.Count} snapshots to {output}; source SHA unchanged.");

WorldState Restore()
{
    using var stream = new MemoryStream(bytes, writable:false);
    using var reader = new BinaryReader(stream);
    if (reader.ReadInt32() != 0x48584C56 || reader.ReadInt32() != 3 || reader.ReadInt32() != seed || reader.ReadInt32() != tick)
        throw new InvalidDataException("Unexpected header.");
    reader.ReadInt64();
    reader.ReadSingle();
    if (reader.ReadInt32() != (int)GameMode.Feud) throw new InvalidDataException("Unexpected mode.");
    var world = new WorldStateFactory().Create(PrototypeWorldDefinitionFactory.Create(seed, GameMode.Feud));
    WorldSaveSerializer.Read(world, reader);
    if (stream.Position != stream.Length || world.Tick != tick) throw new InvalidDataException("Incomplete read or tick mismatch.");
    return world;
}
void Stop(WorldState world, EntityId id)
{
    var method = typeof(BedSleep).Assembly.GetType("HexLive.Simulation.Runtime.ManualCommandExecutor", throwOnError:true)!.GetMethod("Apply", BindingFlags.Public|BindingFlags.Static)!;
    foreach (var command in new ISimulationCommand[] { new SetManualControlCommand(id, true), new StopCommand(id) })
    {
        var result = (ManualCommandAdmission)method.Invoke(null, new object[] { world, command })!;
        if (!result.Accepted) throw new InvalidDataException($"Controlled fixture command rejected: {result.Order}/{result.Reason}");
    }
}
void Export(WorldState world, string name, int npcId, int bedId, bool controlled, string preparation)
{
    WorldSnapshotExporter.IncludeDebugDetails = true;
    WorldSnapshotExporter.IncludeJunctionFlags = true;
    var snapshot = WorldSnapshotExporter.Export(world);
    var file = name + ".json";
    var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, options);
    File.WriteAllBytes(Path.Combine(output, file), json);
    cases.Add(new { name, npcId, bedId, file, sha256 = Hash(json), controlled, preparation });
}
static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
