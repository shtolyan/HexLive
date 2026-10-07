using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
// §59.5: one read-only projection of action items for snapshots and inventory.
public static class ActionItemResolver
{
    // Single source of truth for the ordinary hand prop. Simulation already knows
    // the active verb, goal, target inventory item and carried items; presentation
    // should not have to guess these from strings.
    public static ItemInstance HeldBottle(NPCState npc)
    {
        if (npc.Execution.CurrentInteraction == InteractionType.HydrateOther && npc.Execution.ActionItemsBound)
        {
            var supply = npc.Execution.ActionSupply;
            return BottleInventoryMath.IsBottle(supply) && InventoryMath.ContainsReference(npc.Inventory.Items, supply)
                ? supply : null;
        }
        var target = npc.Execution.TargetInventoryItem;
        if (npc.Execution.CurrentInteraction == InteractionType.FillVessel && npc.Execution.ActionItemsBound)
            return BottleInventoryMath.IsBottle(target) && InventoryMath.ContainsReference(npc.Inventory.Items, target)
                ? target : null;
        if (BottleInventoryMath.IsBottle(target) &&
            InventoryMath.ContainsReference(npc.Inventory.Items, target)) return target;
        return npc.Execution.CurrentInteraction switch
        {
            InteractionType.Drink or InteractionType.HydrateOther => BottleInventoryMath.FirstDrinkable(npc),
            InteractionType.FillVessel => BottleInventoryMath.FirstWithRoomFor(npc, WaterKind.Coconut),
            InteractionType.FillBottle or InteractionType.TakeVessel => BottleInventoryMath.FirstEmpty(npc),
            _ => null
        };
    }

    public static string HeldId(WorldState world, NPCState npc)
    {
        if (!npc.Body.HasUsableHand) return string.Empty;
        // §108 / bug #21: общий draw/holster-контур. Боевой intent принадлежит
        // цели, а конкретное оружие — MeleeSwing; представление ничего не
        // угадывает. Поэтому GroupHunt/Defend/Raid показывают оружие уже на
        // подходе и автоматически прячут его после снятия цели.
        var readiedWeapon = Runtime.MeleeSwing.ReadiedWeapon(npc);
        if (!string.IsNullOrEmpty(readiedWeapon))
        {
            return readiedWeapon;
        }

        if (npc.Execution.CurrentInteraction is not { } interaction)
        {
            return string.Empty;
        }

        switch (interaction)
        {
            case InteractionType.Eat:
            {
                var groundCoconut = ResolveGroundCoconutInteractionItem(world, npc, InteractionType.Eat);
                return !string.IsNullOrEmpty(groundCoconut)
                    ? groundCoconut
                    : ResolveInventoryInteractionItem(world, npc, InteractionType.Eat);
            }

            case InteractionType.Drink:
            {
                var groundCoconut = ResolveGroundCoconutInteractionItem(world, npc, InteractionType.Drink);
                if (!string.IsNullOrEmpty(groundCoconut))
                {
                    return groundCoconut;
                }

                var drink = ResolveInventoryInteractionItem(world, npc, InteractionType.Drink);
                if (!string.IsNullOrEmpty(drink))
                {
                    return drink;
                }

                return InventoryContains(npc, "tool.bottle") ? "tool.bottle" : string.Empty;
            }

            case InteractionType.FillBottle:
                return InventoryContains(npc, "tool.bottle") ? "tool.bottle" : string.Empty;

            // §55.4 (bug #317): перелив — бутылка в рабочей руке, кокос-источник
            // едет отдельным полем OffhandItemId (ResolveOffhandItem).
            case InteractionType.FillVessel:
                return HeldBottle(npc)?.DefinitionId ?? string.Empty;

            case InteractionType.Harvest:
            case InteractionType.Process:
            case InteractionType.Butcher:
                return WorkTool(world, npc)?.DefinitionId ?? string.Empty;

            // Bug #333: наложение шины показывает шину в руке (поза — контур
            // лечения, NpcActorView.treating/kneelingCraft).
            case InteractionType.Splint:
                return InventoryContains(npc, ContentIds.Splint)
                    ? ContentIds.Splint
                    : string.Empty;

            case InteractionType.Fuel:
                return InventoryContains(npc, "resource.stick") ? "resource.stick" : string.Empty;

            case InteractionType.Ignite:
                return InventoryContains(npc, GearCatalog.Lighter)
                    ? GearCatalog.Lighter
                    : string.Empty;

            case InteractionType.Craft:
                return WorkTool(world, npc)?.DefinitionId ?? string.Empty;

            case InteractionType.Build:
            {
                // §54.12: at a furniture build-site the hand shows the material
                // actually being DEPOSITED — the current stage's shortfall she
                // carries. (The old hut-era "Build = carry a log" spawned a log
                // in her hand while she laid bed sticks.) A stocked site shows
                // the hammer for the raise, nothing for a hand-lashed one.
                var buildTarget = npc.Execution.TargetObject ?? npc.Plan.TargetObjectId;
                if (buildTarget is { } siteId &&
                    world.Entities.Objects.TryGetValue(siteId, out var site) &&
                    Runtime.BuildSiteMath.IsSite(site))
                {
                    if (Runtime.BuildSiteMath.IsDemolitionSite(site))
                    {
                        return GearCatalog.BestToolFor(npc.Inventory.Items, GearCapability.ChopWood,
                            npc.Body.IntactHands)?.DefinitionId ?? string.Empty;
                    }

                    foreach (var material in Runtime.BuildSiteMath.AllMaterials)
                    {
                        if (Runtime.BuildSiteMath.Needs(site, material) &&
                            InventoryContains(npc, material))
                        {
                            return material;
                        }
                    }

                    return Runtime.BuildSiteMath.IsStocked(site) && InventoryContains(npc, "tool.hammer")
                        ? "tool.hammer"
                        : string.Empty;
                }

                // The hut anchor (retired) and any non-site Build: the old log carry.
                return InventoryContains(npc, "resource.log") ? "resource.log" : string.Empty;
            }

            case InteractionType.BuildRaft:
                return InventoryContains(npc, "resource.log") ? "resource.log" : string.Empty;

            case InteractionType.FeedOther:
            case InteractionType.HydrateOther:
            case InteractionType.MedicateOther:
                return Supply(world, npc)?.DefinitionId ?? string.Empty;

            // §137.7 r2: the treatment clip is "Searching Pockets" — the
            // visible object that makes that motion read as first aid is the
            // dressing itself. Execution has already validated/reserved the
            // supply before either interaction becomes active; the physical
            // item remains authoritative and is still spent only on completion.
            case InteractionType.TreatOther:
                return npc.Execution.ActionItemsBound ? Supply(world, npc)?.DefinitionId ?? string.Empty : ContentIds.Bandage;
            case InteractionType.TreatSelf:
                return ContentIds.Bandage;

            default:
                return string.Empty;
        }
    }

    // §55.4 (bug #317): предмет ВТОРОЙ руки. Сегодня один случай — перелив
    // FillVessel: бутылка в рабочей руке (ResolveHeldItem), кокос-источник во
    // второй, чтобы вид сыграл крафт-позу с двумя ёмкостями.
    public static string OffhandId(NPCState npc)
    {
        if (npc.Execution.CurrentInteraction == InteractionType.FillVessel)
        {
            var source = npc.Execution.ActionSupply;
            return npc.Body.CanUseTwoHanded && source is not null &&
                InventoryMath.ContainsReference(npc.Inventory.Items, source)
                ? source.DefinitionId : string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveGroundCoconutInteractionItem(
        WorldState world,
        NPCState npc,
        InteractionType interaction)
    {
        var target = npc.Execution.TargetObject ?? npc.Plan.TargetObjectId;
        if (target is not { } targetId ||
            !world.Entities.Objects.TryGetValue(targetId, out var worldObject) ||
            !IsCoconutDefinition(worldObject.DefinitionId) ||
            !DefinitionHasInteraction(world, worldObject.DefinitionId, interaction))
        {
            return string.Empty;
        }

        return worldObject.DefinitionId;
    }

    private static bool IsCoconutDefinition(string definitionId) =>
        definitionId.StartsWith("food.coconut", StringComparison.Ordinal);

    private static string ResolveInventoryInteractionItem(
        WorldState world,
        NPCState npc,
        InteractionType interaction)
    {
        if (npc.Plan.TargetItemDefinitionId is { } target &&
            InventoryContains(npc, target) &&
            DefinitionHasInteraction(world, target, interaction))
        {
            return target;
        }

        foreach (var item in npc.Inventory.Items)
        {
            if (DefinitionHasInteraction(world, item.DefinitionId, interaction))
            {
                return item.DefinitionId;
            }
        }

        return string.Empty;
    }

    private static bool DefinitionHasInteraction(
        WorldState world,
        string definitionId,
        InteractionType interaction)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition))
        {
            return false;
        }

        foreach (var candidate in definition.Interactions)
        {
            if (candidate.Type == interaction)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InventoryContains(NPCState npc, string definitionId)
    {
        foreach (var item in npc.Inventory.Items)
        {
            if (item.DefinitionId == definitionId)
            {
                return true;
            }
        }

        return false;
    }

    public static ItemInstance Supply(WorldState world, NPCState npc)
    {
        var kind = npc.Execution.CurrentInteraction switch
        {
            InteractionType.FeedOther => AidKind.Feed,
            InteractionType.HydrateOther => AidKind.Hydrate,
            InteractionType.TreatOther => AidKind.Treat,
            InteractionType.MedicateOther => AidKind.Medicate,
            _ => AidKind.None
        };
        var item = npc.Execution.ActionItemsBound ? npc.Execution.ActionSupply
            : AidSupply.SelectItem(world, npc, kind);
        return item is not null && InventoryMath.ContainsReference(npc.Inventory.Items, item) ? item : null;
    }

    public static ItemInstance WorkTool(WorldState world, NPCState npc)
    {
        if (npc.Execution.ActionItemsBound)
        {
            var tool = npc.Execution.ActionTool;
            return tool is not null && InventoryMath.ContainsReference(npc.Inventory.Items, tool) ? tool : null;
        }
        // Compatibility for snapshots of a restored or externally staged action.
        if (npc.Execution.TargetObject is not { } id || !world.Entities.Objects.TryGetValue(id, out var target) ||
            !world.Content.ObjectDefinitions.TryGetValue(target.DefinitionId, out var definition)) return null;
        var step = npc.Plan.CurrentStepIndex >= 0 && npc.Plan.CurrentStepIndex < npc.Plan.Steps.Count
            ? npc.Plan.Steps[npc.Plan.CurrentStepIndex] : null;
        var action = ExecutionSystem.ResolveInteraction(world, npc, definition,
            npc.Execution.CurrentInteraction, step?.InteractionId);
        return GearCatalog.BestToolFor(npc.Inventory.Items,
            GearCatalog.RequiredCapabilities(action, definition), npc.Body.IntactHands);
    }

    public static ItemInstance HeldInstance(WorldState world, NPCState npc)
    {
        var id = HeldId(world, npc);
        if (string.IsNullOrEmpty(id)) return null;
        if (npc.Execution.CurrentInteraction is InteractionType.FeedOther or InteractionType.HydrateOther or InteractionType.MedicateOther or InteractionType.TreatOther)
            return Supply(world, npc);
        if (npc.Execution.CurrentInteraction is InteractionType.Harvest or InteractionType.Process or InteractionType.Butcher or InteractionType.Craft)
            return WorkTool(world, npc);
        if (id == ContentIds.Bottle) return HeldBottle(npc);
        return npc.Inventory.Items.Find(item => item.DefinitionId == id);
    }
}
}
