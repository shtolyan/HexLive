using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{
// Server authorization is deliberately outside the package-free simulation.
public sealed class AdminCommand
{
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int NpcId { get; set; }
    public int OtherNpcId { get; set; }
    public int ObjectId { get; set; }
    public string Target { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    [NonSerialized] public string ResolvedDefinitionId = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    public float Value { get; set; }
    public bool Add { get; set; }
    public bool? Enabled { get; set; }
    public bool Mutual { get; set; }
    public float? GroundX { get; set; }
    public float? GroundZ { get; set; }
    public int? TileQ { get; set; }
    public int? TileR { get; set; }
}

public sealed class AdminCommandResult
{
    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string DefinitionId { get; set; } = string.Empty;
    public static AdminCommandResult Ok(AdminCommand c, int id = 0) =>
        new() { Accepted = true, OperationId = c.OperationId, EntityId = id, DefinitionId = c.ResolvedDefinitionId };
    public static AdminCommandResult Reject(AdminCommand c, string reason) =>
        new() { OperationId = c.OperationId, Reason = reason };
}

public static class AdminWorldCommands
{
    public static readonly string[] Kinds = { "heal", "restore_limb", "fit_prosthetic",
        "repair_prosthetic", "set_need", "set_attribute", "set_skill", "set_trait", "set_relationship", "give_item", "remove_item",
        "clear_inventory", "give_garment", "equip_garment", "rename_npc", "set_faction", "assign_npc", "spawn_npc", "delete_npc",
        "create_building", "complete_building", "stock_building", "repair_building", "demolish_building", "open_portal" };
    public static readonly string[] Needs = { "Hunger", "Thirst", "Energy", "Comfort", "Social",
        "Compassion", "ThermalDiscomfort", "Stamina", "Breath", "Hygiene", "Blood", "Stress" };
    public static readonly string[] RelationshipFields = Enum.GetNames(typeof(RelationshipField));
    public static bool IsDestructive(string kind) => kind is "remove_item" or "clear_inventory"
        or "delete_npc" or "demolish_building";

    public static AdminCommandResult Execute(WorldState world, AdminCommand c)
    {
        if (c.Kind == null || c.Target == null || c.DefinitionId == null || c.Text == null)
            return AdminCommandResult.Reject(c, "InvalidInput");
        if (c.Kind == "open_portal")
        {
            var camp = world.Portal.Camp;
            if (c.Target.Length != 0 && !PortalWorld.TryParseCamp(c.Target, out camp))
                return AdminCommandResult.Reject(c, "InvalidCamp");
            var portalProblem = PortalWorld.OpenImmediately(world, camp);
            return portalProblem == null ? AdminCommandResult.Ok(c) : AdminCommandResult.Reject(c, portalProblem);
        }
        if (AdminWorldObjects.Handles(c.Kind)) return AdminWorldObjects.Execute(world, c);
        if (!world.Entities.Npcs.TryGetValue(new EntityId(c.NpcId), out var npc) || npc.Health <= 0)
            return AdminCommandResult.Reject(c, "NpcNotAlive");
        if (!Kinds.Contains(c.Kind)) return AdminCommandResult.Reject(c, "UnknownCommand");
        var wasProne = npc.Body.IsCrawling;
        string problem;
        switch (c.Kind)
        {
            case "restore_limb": case "fit_prosthetic": case "repair_prosthetic":
                problem = Limbs(world, npc, c); break;
            case "heal":
                if (!TryParts(c.Target, false, out var parts)) return AdminCommandResult.Reject(c, "UnknownBodyPart");
                foreach (var part in parts) HealPart(npc, part);
                if (string.IsNullOrEmpty(c.Target) || c.Target == "all")
                {
                    npc.Needs.Blood = 1; npc.Body.BloodDeficit = 0;
                    npc.Mind.DyingCause = DyingCause.None; npc.Mind.DyingReserve = 0;
                    if (npc.Needs.Energy >= 0.15f) npc.Mind.ComaCause = ComaCause.None;
                    npc.Mind.FaintedUntilTick = world.Tick;
                }
                npc.Health = npc.Body.VitalHealth(); problem = string.Empty; break;
            case "set_attribute":
                if (!Enum.TryParse<AttributeKind>(c.Target, true, out var attribute) ||
                    !Enum.IsDefined(typeof(AttributeKind), attribute) || !Finite(c.Value))
                    return AdminCommandResult.Reject(c, "InvalidAttribute");
                var v = c.Value + (c.Add ? npc.Attributes.Get(attribute) : 0);
                if (v < 0 || v > 1) return AdminCommandResult.Reject(c, "ValueOutOfRange");
                npc.Attributes.Set(attribute, v); problem = string.Empty; break;
            case "set_skill": problem = SetSkill(npc, c); break;
            case "set_trait": problem = SetTrait(npc, c); break;
            case "set_relationship": problem = SetRelationship(world, npc, c); break;
            case "give_garment": case "equip_garment": problem = AdminGarments.Execute(world, npc, c); break;
            case "set_need": problem = SetNeed(npc, c); break;
            case "give_item": case "remove_item": case "clear_inventory": problem = Inventory(world, npc, c); break;
            case "rename_npc":
                if (string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > 48 || c.Text.Any(char.IsControl))
                    return AdminCommandResult.Reject(c, "InvalidName");
                npc.DisplayName = c.Text.Trim(); problem = string.Empty; break;
            case "assign_npc":
                if (!Guid.TryParseExact(c.Target, "N", out _) || !FactionRelations.IsGirlCamp(npc.Faction))
                    return AdminCommandResult.Reject(c, "InvalidAssignment");
                problem = string.Empty; break;
            case "set_faction":
                if (!Enum.TryParse<Faction>(c.Target, true, out var faction) || !Enum.IsDefined(typeof(Faction), faction))
                    return AdminCommandResult.Reject(c, "InvalidFaction");
                npc.Faction = faction; problem = string.Empty; break;
            default: return AdminCommandResult.Reject(c, "UnknownCommand");
        }
        if (problem.Length != 0) return AdminCommandResult.Reject(c, problem);
        EquipmentMath.Recalculate(world, npc);
        MortalityHelpers.GrantStandUpGrace(world, npc, wasProne);
        return AdminCommandResult.Ok(c, npc.Id.Value);
    }

    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string SetSkill(NPCState npc, AdminCommand command)
    {
        if (!Enum.TryParse<SkillKind>(command.Target, true, out var skill) ||
            !Enum.IsDefined(typeof(SkillKind), skill) || !Finite(command.Value))
            return "InvalidSkill";
        var value = command.Value + (command.Add ? npc.Skills.Get(skill) : 0);
        if (!(value >= 0 && value <= 1)) return "ValueOutOfRange";
        npc.Skills.Set(skill, value);
        return string.Empty;
    }

    private static string SetTrait(NPCState npc, AdminCommand command)
    {
        if (!TraitSet.TryParse(command.Target, out var trait) || !command.Enabled.HasValue)
            return "InvalidTrait";

        if (!command.Enabled.Value)
        {
            npc.Traits.Remove(trait);
            return string.Empty;
        }

        TraitKind? opposite = trait switch
        {
            TraitKind.Slob => TraitKind.Neat,
            TraitKind.Neat => TraitKind.Slob,
            TraitKind.Lazy => TraitKind.Diligent,
            TraitKind.Diligent => TraitKind.Lazy,
            TraitKind.Coward => TraitKind.Brave,
            TraitKind.Brave => TraitKind.Coward,
            _ => null,
        };
        if (opposite.HasValue) npc.Traits.Remove(opposite.Value);
        npc.Traits.Add(trait);
        return string.Empty;
    }

    private enum RelationshipField { Trust, Familiarity, Affinity, Authority }

    private static string SetRelationship(WorldState world, NPCState npc, AdminCommand command)
    {
        if (!RelationshipFields.Contains(command.Target, StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<RelationshipField>(command.Target, true, out var field) || !Finite(command.Value))
            return "InvalidRelationship";

        var otherId = new EntityId(command.OtherNpcId);
        if (otherId == npc.Id || !world.Entities.Npcs.TryGetValue(otherId, out var other) || other.Health <= 0)
            return "InvalidRelationshipTarget";

        var minimum = field is RelationshipField.Trust or RelationshipField.Affinity ? -1f : 0f;
        var value = NextValue(npc.Social, otherId);
        var reverse = command.Mutual ? NextValue(other.Social, npc.Id) : 0;
        if (!(value >= minimum && value <= 1) || !(reverse >= minimum && reverse <= 1))
            return "ValueOutOfRange";

        Set(npc.Social.GetOrCreate(otherId), value);
        if (command.Mutual) Set(other.Social.GetOrCreate(npc.Id), reverse);
        return string.Empty;

        float NextValue(SocialState social, EntityId target)
        {
            if (!command.Add || !social.Relationships.TryGetValue(target, out var relation))
                return command.Value;
            return command.Value + (field switch
            {
                RelationshipField.Trust => relation.Trust,
                RelationshipField.Familiarity => relation.Familiarity,
                RelationshipField.Affinity => relation.Affinity,
                RelationshipField.Authority => relation.Authority,
                _ => 0,
            });
        }

        void Set(RelationshipData relation, float amount)
        {
            switch (field)
            {
                case RelationshipField.Trust: relation.Trust = amount; break;
                case RelationshipField.Familiarity: relation.Familiarity = amount; break;
                case RelationshipField.Affinity: relation.Affinity = amount; break;
                case RelationshipField.Authority: relation.Authority = amount; break;
            }
        }
    }

    private static bool TryParts(string target, bool limbsOnly, out BodyPart[] parts)
    {
        parts = Array.Empty<BodyPart>();
        if (string.IsNullOrEmpty(target) || target == "all")
        {
            parts = Enum.GetValues(typeof(BodyPart)).Cast<BodyPart>()
                .Where(p => !limbsOnly || IsLimb(p)).ToArray();
            return true;
        }
        if (!Enum.TryParse<BodyPart>(target, true, out var part) ||
            !Enum.IsDefined(typeof(BodyPart), part) || (limbsOnly && !IsLimb(part))) return false;
        parts = new[] { part }; return true;
    }
    private static bool IsLimb(BodyPart p) => p is BodyPart.ArmL or BodyPart.ArmR or BodyPart.LegL or BodyPart.LegR;
    private static void HealPart(NPCState npc, BodyPart part)
    {
        npc.Wounds.RemoveAll(w => w.Zone == part);
        var condition = npc.Body.Condition(part);
        condition.BluntDamage = condition.CriticalTrauma = condition.SplintSupport = 0;
        npc.Body.Parts[part] = npc.Body.IsSevered(part) ? 0 : 1;
    }
    private static string Limbs(WorldState world, NPCState npc, AdminCommand c)
    {
        if (!TryParts(c.Target, true, out var selected)) return "UnknownBodyPart";
        var parts = selected.Where(npc.Body.IsSevered).Where(p => c.Kind != "repair_prosthetic" || npc.Body.Condition(p).Prosthetic != null).ToArray();
        if (string.IsNullOrEmpty(c.Target) && parts.Length > 1) return "AmbiguousLimb";
        if (parts.Length == 0) return c.Kind == "restore_limb" ? string.Empty : "NoMissingLimb";
        var replacements = new Dictionary<BodyPart, ProstheticState>();
        var returned = new List<ItemInstance>();
        foreach (var part in parts)
        {
            var existing = npc.Body.Condition(part).Prosthetic;
            if (c.Kind == "repair_prosthetic")
            {
                if (existing == null) return "NoProsthetic";
                continue;
            }
            if (c.Kind == "fit_prosthetic")
            {
                var candidate = AdminProsthetics.Create(world, part, c.DefinitionId);
                if (candidate == null) return "IncompatibleProsthetic";
                if (existing != null && existing.DefinitionId == candidate.DefinitionId &&
                    existing.Condition >= existing.MaxCondition) continue;
                replacements[part] = candidate;
            }
            if (existing != null) returned.Add(new ItemInstance(existing.DefinitionId)
                { Durability = existing.MaxCondition <= 0 ? 0 : existing.Condition / existing.MaxCondition,
                    OwnerId = npc.Id.Value });
        }
        if (returned.Count > 0 && !Fits(npc.Inventory, returned)) return "InventoryFull";
        foreach (var part in parts)
        {
            var condition = npc.Body.Condition(part);
            if (c.Kind == "repair_prosthetic") condition.Prosthetic.Condition = condition.Prosthetic.MaxCondition;
            else if (c.Kind == "restore_limb")
            {
                condition.Prosthetic = null; npc.Body.Severed.Remove(part); HealPart(npc, part);
            }
            else if (replacements.TryGetValue(part, out var device))
            {
                HealPart(npc, part); condition.Prosthetic = device;
            }
        }
        npc.Inventory.Items.AddRange(returned);
        return string.Empty;
    }
    private static string Inventory(WorldState world, NPCState npc, AdminCommand c)
    {
        if (c.Kind == "clear_inventory") { npc.Inventory.Items.Clear(); return string.Empty; }
        if (c.Count < 1 || c.Count > 1000) return "InvalidCount";
        if (!world.Content.ObjectDefinitions.TryGetValue(c.DefinitionId, out var definition) ||
            !definition.Interactions.Any(i => i.Type == InteractionType.PickUp)) return "NotCarryable";
        if (c.Kind == "remove_item")
        {
            var items = npc.Inventory.Items.Where(i => i.DefinitionId == c.DefinitionId).Take(c.Count).ToArray();
            if (items.Length != c.Count) return "NotEnoughItems";
            // Remove by reference index: ItemInstance.Equals deliberately compares definition only.
            foreach (var item in items) npc.Inventory.Items.RemoveAt(npc.Inventory.Items.FindIndex(i => ReferenceEquals(i, item)));
            return string.Empty;
        }
        var added = Enumerable.Range(0, c.Count).Select(_ => new ItemInstance(c.DefinitionId) { OwnerId = npc.Id.Value }).ToArray();
        if (!Fits(npc.Inventory, added)) return "InventoryFull";
        npc.Inventory.Items.AddRange(added); return string.Empty;
    }
    private static bool Fits(InventoryState inventory, IEnumerable<ItemInstance> added)
    {
        var copy = new InventoryState { Capacity = inventory.Capacity };
        copy.Items.AddRange(inventory.Items); copy.Items.AddRange(added);
        copy.HolsterSlotIds.UnionWith(inventory.HolsterSlotIds);
        return copy.UsedSlots <= copy.Capacity;
    }
    private static string SetNeed(NPCState npc, AdminCommand c)
    {
        var names = c.Target == "all" ? Needs : new[] { c.Target };
        if (!Finite(c.Value) || c.Value < 0 || c.Value > 1 || names.Any(n => !Needs.Contains(n))) return "InvalidNeed";
        foreach (var name in names)
        {
            var best = name is "Hunger" or "Thirst" or "ThermalDiscomfort" or "Stress" ? 0f : 1f;
            typeof(NPCNeeds).GetProperty(name).SetValue(npc.Needs, c.Target == "all" ? best : c.Value);
            if (name == "Blood") npc.Body.BloodDeficit = 1 - npc.Needs.Blood;
        }
        return string.Empty;
    }
}

public static class AdminProsthetics
{
    public static ProstheticState Create(WorldState world, BodyPart part, string id)
    {
        var arm = part is BodyPart.ArmL or BodyPart.ArmR;
        if (!arm && part is not (BodyPart.LegL or BodyPart.LegR)) return null;
        var candidates = new[] { arm ? ContentIds.WoodenArm : ContentIds.WoodenLeg,
            arm ? ContentIds.MechanicalArm : ContentIds.MechanicalLeg }
            .Where(world.Content.ObjectDefinitions.ContainsKey).Select(candidate => CreateDevice(part, candidate));
        return string.IsNullOrEmpty(id) || id == "best"
            ? candidates.OrderByDescending(p => p.Function).ThenByDescending(p => p.MaxCondition)
                .ThenBy(p => p.DefinitionId, StringComparer.Ordinal).FirstOrDefault()
            : candidates.FirstOrDefault(p => p.DefinitionId == id);
    }
    internal static ProstheticState CreateDevice(BodyPart part, string id)
    {
        var arm = part is BodyPart.ArmL or BodyPart.ArmR;
        var mechanical = id == ContentIds.MechanicalArm || id == ContentIds.MechanicalLeg;
        var durability = mechanical ? Spec118.MechanicalProstheticDurability : Spec118.WoodenProstheticDurability;
        return new ProstheticState { DefinitionId = id, Part = part, Condition = durability, MaxCondition = durability,
            Mechanical = mechanical, Function = mechanical ? (arm ? Spec118.MechanicalArmFunction : Spec118.MechanicalLegFunction)
                : (arm ? Spec118.WoodenArmFunction : Spec118.WoodenLegFunction) };
    }
}
}
