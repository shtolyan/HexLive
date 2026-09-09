using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Mcp;

/// <summary>§160: advertise executable action domains, using the same catalogs as admission.</summary>
public static class McpActionSchemas
{
    public static JsonElement Enrich(string tool, JsonElement schema)
    {
        var result = JsonNode.Parse(schema.GetRawText())!.AsObject();
        result["additionalProperties"] = false;
        var properties = result["properties"]!.AsObject();
        void Choices(string field, IEnumerable<string> choices) =>
            properties[field]!["enum"] = JsonSerializer.SerializeToNode(choices.Distinct().OrderBy(x => x, StringComparer.Ordinal));
        switch (tool)
        {
            case "self_action": Choices("kind", Enum.GetNames<SelfActionKind>()); break;
            case "aid_person": Choices("kind", Enum.GetValues<AidKind>().Where(x => x != AidKind.None).Select(x => x.ToString())); break;
            case "craft_item": Choices("recipeGoal", RecipeCatalog.ByGoal.Keys.Select(x => x.ToString())); break;
            case "manage_inventory":
                Choices("source", Enum.GetNames<InventoryItemSource>());
                Choices("action", Enum.GetNames<InventoryAction>());
                break;
            case "interact":
                // Object interactions, not internal plan markers or retired enum ordinals.
                var definitions = new Dictionary<string, ObjectDefinition>(PrototypeContentCatalog.CreateDefaults());
                WorldObjectLibrary.ApplyTo(definitions);
                Choices("interaction", definitions.Values.SelectMany(d => d.Interactions)
                    .Where(i => i.Type != InteractionType.Bury).Select(i => i.Type.ToString()));
                break;
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
