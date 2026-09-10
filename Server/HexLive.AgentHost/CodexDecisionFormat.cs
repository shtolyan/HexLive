using System.Text.Json.Nodes;

namespace HexLive.AgentHost;

/// <summary>Closed CLI schema for the open MCP argument maps; game protocol stays unchanged.</summary>
public static class CodexDecisionFormat
{
    public const string Instructions = "\nCodex transport format: every arguments field is an array of {name,value} pairs, " +
        "not a JSON object. Values are strings, numbers, booleans or null. Use [] for no arguments. " +
        "The host converts these pairs to the MCP argument object before validation. Names must be unique. " +
        "Include condition:null on steps without a condition and repeat:1 for a single execution. Follow the supplied output schema.";

    public static string Schema(string gameSchema)
    {
        var root = JsonNode.Parse(gameSchema)!;
        Visit(root);
        return root.ToJsonString();

        static void Visit(JsonNode node)
        {
            if (node is JsonArray list) { foreach (var child in list) if (child != null) Visit(child); return; }
            if (node is not JsonObject obj) return;
            if (obj["properties"] is JsonObject properties)
            {
                if (properties.ContainsKey("arguments")) properties["arguments"] = JsonNode.Parse("""
                    {"type":"array","maxItems":32,"items":{"type":"object","additionalProperties":false,
                    "properties":{"name":{"type":"string"},"value":{"type":["string","number","boolean","null"]}},
                    "required":["name","value"]}}
                    """);
                obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            }
            foreach (var child in obj.ToArray()) if (child.Value != null) Visit(child.Value);
        }
    }

    public static string Decode(string wireJson)
    {
        var root = JsonNode.Parse(wireJson) ?? throw new InvalidDataException("EmptyCodexDecision");
        Visit(root);
        return root.ToJsonString();

        static void Visit(JsonNode node)
        {
            if (node is JsonArray list) { foreach (var child in list) if (child != null) Visit(child); return; }
            if (node is not JsonObject obj) return;
            if (obj.TryGetPropertyValue("arguments", out var arguments))
            {
                if (arguments is not JsonArray pairs || pairs.Count > 32)
                    throw new InvalidDataException("InvalidCodexArgumentPairs");
                var map = new JsonObject();
                foreach (var entry in pairs)
                {
                    if (entry is not JsonObject pair || pair.Count != 2 ||
                        pair["name"] is not JsonValue nameNode || !nameNode.TryGetValue<string>(out var name) ||
                        string.IsNullOrWhiteSpace(name) || !pair.ContainsKey("value") || map.ContainsKey(name) ||
                        pair["value"] is JsonObject or JsonArray)
                        throw new InvalidDataException("InvalidCodexArgumentPairs");
                    map[name] = pair["value"]?.DeepClone();
                }
                obj["arguments"] = map;
            }
            foreach (var child in obj.ToArray())
                if (child.Key != "arguments" && child.Value != null) Visit(child.Value);
        }
    }
}
