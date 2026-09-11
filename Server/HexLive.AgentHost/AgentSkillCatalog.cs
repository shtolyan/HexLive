using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

/// <summary>Profile procedures are data; they cannot extend the MCP allowlist.</summary>
public static class AgentSkillCatalog
{
    public static Dictionary<string, JsonElement> Read(string? workspace)
    {
        var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(AgentPromptFiles.Read("skills.json"))
            ?? throw new InvalidDataException("InvalidSkillCatalog");
        if (workspace == null) return result;
        var directory = new DirectoryInfo(Path.Combine(workspace, "skills"));
        if (!directory.Exists) return result;
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("InvalidSkillsDirectory");
        var path = new FileInfo(Path.Combine(directory.FullName, "catalog.json"));
        if (!path.Exists) return result;
        if ((path.Attributes & FileAttributes.ReparsePoint) != 0 || path.Length is < 2 or > 262144)
            throw new InvalidDataException("InvalidWorkspaceSkillCatalog");
        var bytes = File.ReadAllBytes(path.FullName);
        if (bytes.Length > 262144) throw new InvalidDataException("InvalidWorkspaceSkillCatalog");
        var local = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(new UTF8Encoding(false, true).GetString(bytes))
            ?? throw new InvalidDataException("InvalidWorkspaceSkillCatalog");
        if (local.Count > 64) throw new InvalidDataException("TooManyWorkspaceSkills");
        foreach (var (id, skill) in local)
        {
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant) || skill.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("InvalidWorkspaceSkill");
            // Omitted status means candidate. Candidates are not supplied as executable guidance.
            if (!skill.TryGetProperty("status", out var status)) continue;
            if (status.ValueKind != JsonValueKind.String) throw new InvalidDataException("InvalidWorkspaceSkillStatus");
            if (status.GetString() == "candidate") continue;
            if (status.GetString() != "validated" ||
                !skill.TryGetProperty("validationReport", out var report) || report.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(report.GetString()) ||
                !skill.TryGetProperty("version", out var version) || !version.TryGetInt32(out var n) || n < 1 ||
                !skill.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()) ||
                !skill.TryGetProperty("procedure", out var procedure) || procedure.ValueKind != JsonValueKind.Array || procedure.GetArrayLength() is < 1 or > 64 ||
                procedure.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString())))
                throw new InvalidDataException("InvalidValidatedSkill");
            if (result.TryGetValue(id, out var bundled) && n <= bundled.GetProperty("version").GetInt32())
                throw new InvalidDataException("WorkspaceSkillVersionMustIncrease");
            result[id] = skill;
        }
        return result;
    }
}
