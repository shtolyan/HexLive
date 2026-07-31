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

    // Persistent garment soil. Unlike body hygiene this belongs to the item,
    // follows it through dress/undress, and is washed only while worn at water.
    public float Dirtiness { get; set; }

    public float Bloodiness { get; set; }

    // Portable container contents, in drink charges. Pierced coconuts use this
    // like the NPC bottle: pickup/drop preserves the remaining water.
    public float ResourceAmount { get; set; }

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
    // §gear-personal: RETIRED — the bottle (and any future gear) lives by the
    // same rules as everything: takes a slot, can be dropped, lost and fetched.
    // Kept as a seam in case a future design reintroduces a bound slot.
    public static bool IsPersonalEffect(string definitionId) => false;

    // Spec §52.8: the TYPED weapon slots granted by worn holsters — a tool id
    // per slot (HolsterCatalog). Depends only on WornItems, so it is rewritten
    // by EquipmentMath.RecalculateCapacity on every worn change and never goes
    // stale as items come and go. Which tool actually sits in a slot is derived
    // live from Items (IsHolstered) — no second item store, no bookkeeping to
    // desync. Each slot frees ONE matching carried tool from the pocket budget.
    public readonly HashSet<string> HolsterSlotIds = new();

    /// <summary>
    /// Is this exact carried instance the one parked in a holster slot? True for
    /// the FIRST carried item of a slotted id — a second identical tool has no
    /// slot left and rides in a normal pocket.
    /// </summary>
    public bool IsHolstered(ItemInstance item)
    {
        if (item is null || HolsterSlotIds.Count == 0 ||
            !HolsterSlotIds.Contains(item.DefinitionId))
        {
            return false;
        }

        foreach (var candidate in Items)
        {
            if (candidate.DefinitionId == item.DefinitionId)
            {
                return ReferenceEquals(candidate, item);
            }
        }

        return false;
    }

    /// <summary>Is a tool for this slot id currently carried (slot filled)?</summary>
    public bool IsSlotFilled(string definitionId)
    {
        if (!HolsterSlotIds.Contains(definitionId))
        {
            return false;
        }

        foreach (var candidate in Items)
        {
            if (candidate.DefinitionId == definitionId)
            {
                return true;
            }
        }

        return false;
    }

    // §54.10: bulk resources STACK — a bundle of identical leaves/sticks/logs/etc.
    // rides in ONE pocket slot (up to the stack size). Items stays a flat list of
    // instances so recipes, weapon checks and per-item removals keep working; slot
    // counting and snapshot/UI presentation fold stackable resources together.
    public const int StackSize = 20;

    // §54.2: leaves stack 3× deeper — a bed's mattress is ~46-50 leaves, so a
    // whole bed's worth of leaves rides in a single pocket instead of three.
    public const int LeafStackSize = 60;

    // Per-item stack depth (leaves get the deep stack; everything else the default).
    public static int StackSizeFor(string definitionId) =>
        definitionId == "resource.palm_leaf" ? LeafStackSize : StackSize;

    public static bool IsStackable(string definitionId) =>
        !string.IsNullOrEmpty(definitionId) &&
        definitionId.StartsWith("resource.", System.StringComparison.Ordinal);

    // Pocketed items only — personal effects (the bottle) ride free; stackable
    // bulk resources fold into one slot per StackSize of the same definition.
    public int UsedSlots
    {
        get
        {
            var loose = 0;
            Dictionary<string, int>? stacks = null;
            HashSet<string>? slotTaken = null;
            foreach (var item in Items)
            {
                if (IsPersonalEffect(item.DefinitionId))
                {
                    continue;
                }

                // Spec §52.8: a tool riding in a worn holster's typed slot costs
                // no pocket. One per slot — a second identical tool still counts.
                if (HolsterSlotIds.Count > 0 && HolsterSlotIds.Contains(item.DefinitionId) &&
                    (slotTaken == null || !slotTaken.Contains(item.DefinitionId)))
                {
                    (slotTaken ??= new HashSet<string>()).Add(item.DefinitionId);
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

    // Kept for non-coconut drinkables; whole coconuts are opened on the ground.
    public string? FindFirstDrink(ContentCatalog content)
    {
        foreach (var item in Items)
        {
            if (item.DefinitionId == "food.coconut_pierced" && item.ResourceAmount <= 0f)
            {
                continue;
            }

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
