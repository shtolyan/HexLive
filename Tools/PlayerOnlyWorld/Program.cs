using System.Text.Json;
using HexLive.Server;
using HexLive.Simulation.Bootstrap;

if (args.Length != 3 || args[0] is not ("audit" or "apply")) throw new ArgumentException("audit|apply <world-root> <expected-world-id>; service must be stopped for apply");
var root = Path.GetFullPath(args[1]);
var id = JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(root, "worlds", "active.json")))!;
if (id != args[2] || !Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Active world differs");
var dir = Path.Combine(root, "worlds", id);
var record = JsonSerializer.Deserialize<ServerWorldRecord>(File.ReadAllText(Path.Combine(dir, "world.json")))!;
var configPath = Path.Combine(dir, "creation.xml");
var config = File.Exists(configPath) ? WorldCreationCodec.Decode(File.ReadAllText(configPath)) : null;
using var host = new WorldHost(record.Seed, record.Mode, Path.Combine(dir, "world.sav"), Path.Combine(dir, "simdata.json"), false, creationConfig: config, worldId: id);
// Assignments are private. The CLI projects only ids, never credentials or player identifiers.
var protectedIds = new List<int>();
var assignments = Path.Combine(dir, "players.json");
if (File.Exists(assignments))
{
    using var document = JsonDocument.Parse(File.ReadAllText(assignments));
    Collect(document.RootElement);
    void Collect(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name.Equals("npcIds", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var value in p.Value.EnumerateArray()) protectedIds.Add(value.GetInt32());
                else if (p.Name.Equals("npcId", StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out var n)) protectedIds.Add(n);
                else Collect(p.Value);
            }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var value in e.EnumerateArray()) Collect(value);
    }
}
var audit = host.Read(w => new { world = id, tick = w.Tick, players = w.PlayerCharacterCreations.Select(p => p.NpcId).OrderBy(n => n).ToArray(), npcs = w.Entities.Npcs.Keys.Select(n => n.Value).OrderBy(n => n).ToArray() });
if (args[0] == "audit") { Console.WriteLine(JsonSerializer.Serialize(audit)); return; }
var removed = host.Read(w => PlayerOnlyWorldPolicy.Apply(w, protectedIds));
host.Read(w =>
{
    w.CreationConfig.WorldId = id;
    w.CreationConfig.Name = record.Name;
    if (string.IsNullOrEmpty(w.CreationConfig.CreatorPlayerId)) w.CreationConfig.CreatorPlayerId = Guid.Empty.ToString("N");
    var errors = WorldCreation.Validate(w.CreationConfig, PrototypeWorldDefinitionFactory.Create(record.Seed,record.Mode));
    if (errors.Count != 0) throw new InvalidOperationException("Invalid players-only configuration: " + string.Join(",",errors.Select(e => e.Path + ":" + e.Code)));
    return true;
});
host.Save();
WorldLibrary.AtomicWrite(configPath, host.CreationConfigText);
Console.WriteLine(JsonSerializer.Serialize(new { result = "applied", audit.world, audit.tick, audit.players, removed, playersOnly = true }));
