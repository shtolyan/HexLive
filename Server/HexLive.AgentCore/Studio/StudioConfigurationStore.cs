using System.Security.Cryptography;
using System.Text.Json;

namespace HexLive.AgentCore.Studio;

public sealed record StudioConfiguration(int Version, AgentProfile[] Agents, ServerProfile[] Servers);
public sealed record StudioConfigurationSnapshot(StudioConfiguration Configuration, string Revision);

/// <summary>Only settings and credential references; execution state is deliberately not persisted.</summary>
public sealed class StudioConfigurationStore
{
    private readonly string _root;
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public StudioConfigurationStore(string root)
    {
        _root = Path.GetFullPath(root); Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(_root, "profiles.json");
    }
    public async Task<StudioConfigurationSnapshot> ReadAsync(CancellationToken token = default)
    {
        if (!File.Exists(_path)) return new(new(1, [], []), "missing");
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("ConfigurationTooLarge");
        var bytes = await File.ReadAllBytesAsync(_path, token);
        var value = JsonSerializer.Deserialize<StudioConfiguration>(bytes, Json)
            ?? throw new InvalidDataException("InvalidConfiguration");
        Validate(value);
        return new(value, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public async Task<StudioConfigurationSnapshot> SaveAsync(StudioConfigurationSnapshot original,
        StudioConfiguration value, CancellationToken token = default)
    {
        Validate(value);
        using var lease = new FileStream(Path.Combine(_root, ".profiles.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if ((await ReadAsync(token)).Revision != original.Revision) throw new IOException("ConfigurationVersionConflict");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("ConfigurationTooLarge");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(value, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static void Validate(StudioConfiguration value)
    {
        if (value.Version != 1 || value.Agents == null || value.Servers == null ||
            value.Agents.Length > 64 || value.Servers.Length > 64 ||
            value.Agents.Select(x => x.Id).Distinct().Count() != value.Agents.Length ||
            value.Servers.Select(x => x.Id).Distinct().Count() != value.Servers.Length)
            throw new InvalidDataException("InvalidConfiguration");
        var workspaces = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var agent in value.Agents)
        {
            // Incomplete drafts can be stored, but AgentProfile.Validate forbids starting them.
            if (agent.Id == Guid.Empty || string.IsNullOrWhiteSpace(agent.Name) || agent.Name.Length > 48 ||
                agent.Name.Any(char.IsControl) || !Path.IsPathFullyQualified(agent.Workspace) || agent.Model == null ||
                !Enum.IsDefined(agent.Model.Provider) || agent.HeartbeatSeconds is < 5 or > 3600 ||
                !workspaces.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(agent.Workspace))))
                throw new InvalidDataException("InvalidAgentDraftOrSharedWorkspace");
        }
        foreach (var server in value.Servers) server.Validate();
    }
}
