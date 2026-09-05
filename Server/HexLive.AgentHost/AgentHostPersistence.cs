using System.Text.Json;
namespace HexLive.AgentHost;

public sealed class AgentHostStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true };
    private readonly string _path;

    public AgentHostStatusStore(string path) => _path = path;

    public void Write(bool running, string phase, int npcId, bool attached,
        bool playerPresent, string error = "")
    {
        WriteAtomic(_path, JsonSerializer.Serialize(new
        {
            running,
            phase,
            npcId,
            attached,
            playerPresent,
            error,
            updatedUtc = DateTimeOffset.UtcNow,
        }, JsonOptions));
    }

    internal static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        TryProtect(temporary);
        File.Move(temporary, path, true);
        TryProtect(path);
    }

    private static void TryProtect(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class AgentTurnOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true };
    private readonly string _path;
    private readonly List<PendingAgentTurn> _items;

    public AgentTurnOutbox(string path)
    {
        _path = path;
        try
        {
            _items = File.Exists(path)
                ? JsonSerializer.Deserialize<List<PendingAgentTurn>>(File.ReadAllText(path), JsonOptions) ?? new()
                : new();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Agent outbox is invalid; it was not overwritten.", ex);
        }
    }

    public IReadOnlyList<PendingAgentTurn> Items => _items.ToArray();

    public void Add(PendingAgentTurn item)
    {
        if (_items.Any(x => x.TurnId == item.TurnId)) return;
        if (_items.Count >= 32)
            throw new InvalidOperationException("Agent outbox is full; pending turns must be reconciled first.");
        _items.Add(item);
        Save();
    }

    public void MarkLocalCommitted(string turnId)
    {
        var item = _items.FirstOrDefault(x => x.TurnId == turnId);
        if (item == null) return;
        item.LocalCommitted = true;
        Save();
    }

    public void Remove(string turnId)
    {
        _items.RemoveAll(x => x.TurnId == turnId);
        Save();
    }

    private void Save() => AgentHostStatusStore.WriteAtomic(
        _path, JsonSerializer.Serialize(_items, JsonOptions));
}

public sealed class PendingAgentTurn
{
    public string TurnId { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public MashaWorldHandle World { get; set; } = new("", "", 0, 0);
    public CompanionDecision Decision { get; set; } = new();
    public bool LocalCommitted { get; set; }
}
