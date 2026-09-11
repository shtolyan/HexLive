using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

// Read-only reference capabilities. A skill never grants a new MCP permission.
public sealed class AgentReferenceReader(McpClient mcp, string? workspace = null, int? npcId = null)
{
    public static bool Allowed(string operation) => operation is "spec.read" or "skills.list" or "skills.read" or "recipes.read" or "build.read" or "inventory.drop.read";

    public async Task<MemoryReadResult> ReadAsync(string operation, JsonElement arguments, CancellationToken token)
    {
        if (!Allowed(operation)) throw new InvalidDataException("InvalidReferenceOperation");
        string String(string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";
        var offset = arguments.TryGetProperty("offset", out var off) && off.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
        string text, name;
        int total;
        if (operation == "inventory.drop.read")
        {
            if (npcId is not > 0) throw new InvalidDataException("ReferenceActorRequired");
            if (!arguments.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var sourceIndex) || sourceIndex < 0)
                throw new InvalidDataException("ReferenceItemRequired");
            var radius = 2;
            if (arguments.TryGetProperty("approachRadiusTiles", out var requestedRadius) &&
                (requestedRadius.ValueKind != JsonValueKind.Number || !requestedRadius.TryGetInt32(out radius) || radius is < 1 or > 6))
                throw new InvalidDataException("InvalidDropSearchRadius");
            var response = await mcp.CallToolAsync("read_inventory_drop", new
                { npcId = npcId.Value, index = sourceIndex, expectedDefinitionId = String("expectedDefinitionId"), approachRadiusTiles = radius }, token).ConfigureAwait(false);
            name = "inventory-drop:" + npcId + ":" + sourceIndex;
            text = response.GetRawText(); total = text.Length;
            text = offset >= total ? "" : text[offset..];
        }
        else if (operation == "spec.read")
        {
            var section = String("section");
            if (section.Length == 0) throw new InvalidDataException("ReferenceSectionRequired");
            var response = await mcp.CallToolAsync("read_spec", new { section, offset }, token).ConfigureAwait(false);
            name = "spec:" + response.GetProperty("section").GetString();
            text = response.GetProperty("text").GetString() ?? "";
            total = response.GetProperty("totalChars").GetInt32();
        }
        else if (operation is "recipes.read" or "build.read")
        {
            var response = await mcp.CallToolAsync(operation == "recipes.read" ? "read_recipes" : "read_build_catalog",
                new { definitionId = String("definitionId") }, token).ConfigureAwait(false);
            name = (operation == "recipes.read" ? "recipes:" : "build:") + (String("definitionId").Length == 0 ? "index" : String("definitionId"));
            text = response.GetRawText();
            total = text.Length;
            text = offset >= total ? "" : text[offset..];
        }
        else
        {
            var catalog = AgentSkillCatalog.Read(workspace);
            if (operation == "skills.list")
            {
                name = "skills:index";
                text = JsonSerializer.Serialize(catalog.Select(s => new
                    { id = s.Key, title = s.Value.GetProperty("title").GetString(), version = s.Value.GetProperty("version").GetInt32() }), AgentMemoryArchive.Json);
            }
            else
            {
                var id = String("id");
                if (!catalog.TryGetValue(id, out var skill)) throw new InvalidDataException("UnknownSkill");
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
