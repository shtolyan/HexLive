using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Agents
{

// Spec 35.5: items are instances with runtime state, not bare definition
// ids. Equality is by DefinitionId on purpose: recipe code written against
// the old string lists ("consume one firewood") keeps its exact semantics —
// Contains/Remove match the first instance of that definition. Code that
// cares which physical item is meant must search the list itself.
public sealed class ItemInstance : System.IEquatable<ItemInstance>
{
    public string DefinitionId { get; }

    // Spec 35.5: 0 = dry; > 0.5 = soaked (zero warmth, movement penalty).
    public float Wetness { get; set; }

    // Spec 35.6 (mechanics pending): rides along at full condition.
    public float Durability { get; set; } = 1f;

    public ItemInstance(string definitionId)
    {
        DefinitionId = definitionId;
    }

    public static implicit operator string(ItemInstance item) => item.DefinitionId;

    public static implicit operator ItemInstance(string definitionId) => new(definitionId);

    public bool Equals(ItemInstance? other) => other is not null && other.DefinitionId == DefinitionId;

    public override bool Equals(object? obj) => obj is ItemInstance other && Equals(other);

    public override int GetHashCode() => DefinitionId.GetHashCode();

    public override string ToString() => DefinitionId;
}

// Minimal carrying model (spec 29B + 35.5 instances).
public sealed class InventoryState
{
    public List<ItemInstance> Items { get; } = new();

    // Spec 35.2: 10 slots — the tool belt era.
    public int Capacity { get; set; } = 10;

    public bool HasSpace => Items.Count < Capacity;

    public string? FindFirstFood(ContentCatalog content)
    {
        foreach (var item in Items)
        {
            if (!content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition))
            {
                continue;
            }

            foreach (var interaction in definition.Interactions)
            {
                if (interaction.Type == InteractionType.Eat)
                {
                    return item.DefinitionId;
                }
            }
        }

        return null;
    }
}

}
