using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>§160: validate advertised action arguments before acquiring or replacing control.</summary>
public sealed class AgentActionContract
{
    private readonly Dictionary<string, JsonElement> _schemas = new(StringComparer.Ordinal);

    public AgentActionContract(JsonElement catalog)
    {
        foreach (var tool in catalog.GetProperty("tools").EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            if (AgentProviders.IsAllowedTool(name)) _schemas[name] = tool.GetProperty("inputSchema").Clone();
        }
    }

    public Dictionary<string, JsonElement> BindAndValidate(CompanionAction action, int npcId)
    {
        if (!_schemas.TryGetValue(action.Tool, out var schema)) Fail("ActionToolUnavailable");
        if (action.Arguments.ValueKind != JsonValueKind.Object) Fail("InvalidArgumentType");
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in action.Arguments.EnumerateObject())
            if (!arguments.TryAdd(property.Name, property.Value.Clone())) Fail("DuplicateArgument");
        // Actor identity belongs to the attachment, never to model output.
        arguments["npcId"] = JsonSerializer.SerializeToElement(npcId);
        Validate(JsonSerializer.SerializeToElement(arguments), schema);
        return arguments;
    }

    private static void Validate(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var valid = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(t => Matches(value, t.GetString()))
                : Matches(value, type.GetString());
            if (!valid) Fail("InvalidArgumentType");
        }
        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(choice => System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(choice.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText()))))
            Fail("InvalidArgumentEnum");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            schema.TryGetProperty("properties", out var properties);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) Fail("DuplicateArgument");
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child))
                    Validate(property.Value, child);
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.Object)
                    Validate(property.Value, extra);
                else if (extra.ValueKind == JsonValueKind.False)
                    Fail("UnknownArgument");
            }
            if (schema.TryGetProperty("required", out var required) &&
                required.EnumerateArray().Any(field => !names.Contains(field.GetString()!)))
                Fail("MissingRequiredArgument");
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) Validate(item, items);
    }

    private static bool Matches(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => false
    };

    private static void Fail(string code) => throw new AgentActionValidationException(code);
}

public sealed class AgentActionValidationException(string code) : InvalidOperationException(code)
{
    public string ReasonCode { get; } = code;
}
