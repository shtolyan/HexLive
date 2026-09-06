using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Bootstrap;

namespace HexLive.Server;

public sealed class ServerWorldRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Seed { get; set; }
    public GameMode Mode { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public long CatalogRevision { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public bool CreationCompleted { get; set; }
}

// Only the supervisor holds the world switch lock. Disk identity never comes from a path supplied by HTTP.
public sealed class WorldLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; }
    public string ActiveId { get; private set; }
    public bool MigratedOnStartup { get; private set; }
    public WorldLibrary(string legacySave, int seed, GameMode mode, string catalog, long revision, string? legacyAssignmentsPath = null)
    {
        Root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(legacySave))!, "worlds");
        Directory.CreateDirectory(Root);
        var active = Path.Combine(Root, "active.json");
        if (File.Exists(active))
        {
            ActiveId = JsonSerializer.Deserialize<string>(File.ReadAllText(active))!;
            _ = Read(ActiveId);
            return;
        }
        MigratedOnStartup = true;
        var record = new ServerWorldRecord { Id = Guid.NewGuid().ToString("N"), Name = "Legacy world", Seed = seed, Mode = mode, CatalogRevision = revision, Ready = true };
        Prepare(record, null, catalog);
        if (File.Exists(legacySave)) File.Copy(legacySave, SavePath(record.Id));
        var assignments = legacyAssignmentsPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(legacySave))!, "hexlive-players.json");
        if (File.Exists(assignments)) File.Copy(assignments, AssignmentPath(record.Id));
        ActiveId = record.Id;
        Activate(record.Id);
    }
    public static string StartupSavePath(string legacySave)
    {
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(legacySave))!, "worlds");
        var active = Path.Combine(root, "active.json");
        if (!File.Exists(active)) return legacySave;
        var id = JsonSerializer.Deserialize<string>(File.ReadAllText(active));
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid active world pointer.");
        return Path.Combine(root, id!, "world.sav");
    }
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid world id.");
        return Path.Combine(Root, id);
    }
    public string SavePath(string id) => Path.Combine(DirectoryFor(id), "world.sav");
    public string AssignmentPath(string id) => Path.Combine(DirectoryFor(id), "players.json");
    public string CatalogPath(string id) => Path.Combine(DirectoryFor(id), "simdata.json");
    public WorldCreationConfig? Config(string id)
    {
        var path = Path.Combine(DirectoryFor(id), "creation.xml");
        return File.Exists(path) ? WorldCreationCodec.Decode(File.ReadAllText(path)) : null;
    }
    public ServerWorldRecord Read(string id) => JsonSerializer.Deserialize<ServerWorldRecord>(
        File.ReadAllText(Path.Combine(DirectoryFor(id), "world.json")), Json) ?? throw new InvalidDataException("Invalid world metadata.");
    public IReadOnlyList<ServerWorldRecord> List(bool includePreparing = false) => Directory.EnumerateDirectories(Root)
        .Where(d => Guid.TryParseExact(Path.GetFileName(d), "N", out _) && File.Exists(Path.Combine(d, "world.json")))
        .Select(d => Read(Path.GetFileName(d))).Where(w => includePreparing || w.Ready).OrderByDescending(w => w.CreatedUtc).ToArray();
    public void Prepare(ServerWorldRecord record, WorldCreationConfig? config, string catalog)
    {
        var dir = DirectoryFor(record.Id);
        if (Directory.Exists(dir)) throw new IOException("World already exists.");
        Directory.CreateDirectory(dir);
        AtomicWrite(CatalogPath(record.Id), catalog);
        if (config != null) AtomicWrite(Path.Combine(dir, "creation.xml"), WorldCreationCodec.Encode(config));
        AtomicWrite(Path.Combine(dir, "world.json"), JsonSerializer.Serialize(record, Json));
    }
    public void MarkReady(string id)
    {
        var record = Read(id); record.Ready = true;
        AtomicWrite(Path.Combine(DirectoryFor(id), "world.json"), JsonSerializer.Serialize(record, Json));
    }
    public bool CreationSucceeded(ServerWorldRecord record) => record.CreationCompleted || ActiveId == record.Id;
    public void MarkCreationCompleted(string id)
    {
        var record = Read(id);
        if (record.CreationCompleted || string.IsNullOrEmpty(record.RequestId)) return;
        record.CreationCompleted = true;
        AtomicWrite(Path.Combine(DirectoryFor(id), "world.json"), JsonSerializer.Serialize(record, Json));
    }
    public void StoreCatalog(string id, string catalog, long revision)
    {
        var record = Read(id); record.CatalogRevision = revision;
        AtomicWrite(CatalogPath(id), catalog);
        AtomicWrite(Path.Combine(DirectoryFor(id), "world.json"), JsonSerializer.Serialize(record, Json));
    }
    public void Activate(string id)
    {
        _ = Read(id);
        AtomicWrite(Path.Combine(Root, "active.json"), JsonSerializer.Serialize(id));
        ActiveId = id;
    }
    public static void AtomicWrite(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file))
            { writer.Write(contents); writer.Flush(); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
