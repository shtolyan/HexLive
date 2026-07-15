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

    // Spec §52: no longer a fixed number — the pack is 2 hand slots plus the
    // pockets of every worn garment, recomputed by EquipmentMath.Recalculate.
    // Defaulted so any host that never dresses an NPC still carries a little.
    public int Capacity { get; set; } = 2;

    // Spec §52: personal effects hang on the body (a belt / strap), not in a
    // pocket, so they never count against the pocket budget and stay carryable
    // even naked. The bottle is one (spec 29H: "always there"); the future
    // weapon slot (spec §52) will join it.
    public static bool IsPersonalEffect(string definitionId) =>
        definitionId == "tool.bottle";

    // §54.10: bulk raw materials STACK — a bundle of identical leaves/sticks/etc.
    // rides in ONE pocket slot (up to the stack size), so hauling a bed's worth of
    // pieces is a trip or two, not a dozen. Slots are NOT expanded; only the
    // accounting stacks. Items stays a flat list of instances, so every existing
    // Add/Remove/Contains/Count path is unchanged — only slot counting groups.
    public const int StackSize = 20;

    // §54.2: leaves stack 3× deeper — a bed's mattress is ~46-50 leaves, so a
    // whole bed's worth of leaves rides in a single pocket instead of three.
    public const int LeafStackSize = 60;

    // Per-item stack depth (leaves get the deep stack; everything else the default).
    public static int StackSizeFor(string definitionId) =>
        definitionId == "resource.palm_leaf" ? LeafStackSize : StackSize;

    private static readonly HashSet<string> StackableIds = new()
    {
        "resource.palm_leaf", "resource.stick", "resource.stone",
        "resource.fiber", "resource.rope",
    };

    public static bool IsStackable(string definitionId) => StackableIds.Contains(definitionId);

    // Pocketed items only — personal effects (the bottle) ride free; stackable
    // bulk resources fold into one slot per StackSize of the same definition.
    public int UsedSlots
    {
        get
        {
            var loose = 0;
            Dictionary<string, int>? stacks = null;
            foreach (var item in Items)
            {
                if (IsPersonalEffect(item.DefinitionId))
                {
                    continue;
                }

                if (IsStackable(item.DefinitionId))
                {
                    stacks ??= new Dictionary<string, int>();
                    stacks.TryGetValue(item.DefinitionId, out var c);
                    stacks[item.DefinitionId] = c + 1;
                }
                else
                {
                    loose++;
                }
            }

            if (stacks != null)
            {
                foreach (var kv in stacks)
                {
                    var size = StackSizeFor(kv.Key);
                    loose += (kv.Value + size - 1) / size; // ceil to whole slots
                }
            }

            return loose;
        }
    }

    public bool HasSpace => UsedSlots < Capacity;

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

    // §55: the first carried item that can be drunk (a whole coconut). Mirrors
    // FindFirstFood — thirst now comes from cracking a coconut, not a bottle.
    public string? FindFirstDrink(ContentCatalog content)
    {
        foreach (var item in Items)
        {
            if (!content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition))
            {
                continue;
            }

            foreach (var interaction in definition.Interactions)
            {
                if (interaction.Type == InteractionType.Drink)
                {
                    return item.DefinitionId;
                }
            }
        }

        return null;
    }
}

}
