using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

// Read-only reference capabilities. A skill never grants a new MCP permission.
public sealed class AgentReferenceReader(McpClient mcp)
{
    public static bool Allowed(string operation) => operation is "spec.read" or "skills.list" or "skills.read";

    public async Task<MemoryReadResult> ReadAsync(string operation, JsonElement arguments, CancellationToken token)
    {
        if (!Allowed(operation)) throw new InvalidDataException("InvalidReferenceOperation");
        string String(string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
        var offset = arguments.TryGetProperty("offset", out var off) && off.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
        string text, name;
        int total;
        if (operation == "spec.read")
        {
            var section = String("section");
            if (section.Length == 0) throw new InvalidDataException("ReferenceSectionRequired");
            var response = await mcp.CallToolAsync("read_spec", new { section, offset }, token).ConfigureAwait(false);
            name = "spec:" + response.GetProperty("section").GetString();
            text = response.GetProperty("text").GetString() ?? "";
            total = response.GetProperty("totalChars").GetInt32();
        }
        else
        {
            using var catalog = JsonDocument.Parse(AgentPromptFiles.Read("skills.json"));
            if (operation == "skills.list")
            {
                name = "skills:index";
                text = JsonSerializer.Serialize(catalog.RootElement.EnumerateObject().Select(s => new
                    { id = s.Name, title = s.Value.GetProperty("title").GetString(), version = s.Value.GetProperty("version").GetInt32() }), AgentMemoryArchive.Json);
            }
            else
            {
                var id = String("id");
                if (!catalog.RootElement.TryGetProperty(id, out var skill)) throw new InvalidDataException("UnknownSkill");
                name = "skill:" + id;
                text = JsonSerializer.Serialize(skill, AgentMemoryArchive.Json);
            }
            total = text.Length;
            text = offset >= total ? "" : text[offset..];
        }
        // Preserve truthful pagination even when the server returns a larger page.
        if (text.Length > 5000) text = text[..5000];
        int? next = offset + text.Length < total ? offset + text.Length : null;
        var source = name + ":" + offset + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var envelope = JsonSerializer.Serialize(new { sourceId = source, kind = "reference", name, offset,
            totalChars = total, nextOffset = next, text }, AgentMemoryArchive.Json);
        return new(envelope, [source], next);
    }
}
