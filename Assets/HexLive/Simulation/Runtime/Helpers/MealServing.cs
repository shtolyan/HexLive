using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§54.17: roasting preserves the whole preparation until it is divided at a surface.</summary>
public static class MealServing
{
    public const string InteractionId = "serve.meal";
    public const string GroundInteractionId = "serve.ground";
    public const int ServeDurationTicks = 36;
    private const int MinimumServeTicks = 30;

    /// <summary>§54.14A: how far she looks for a stump/workbench/table or a
    /// palm to fell before serving on the ground (six hexes ≈ 13.5 wu).</summary>
    public const int SurfaceSearchTiles = 6;

    /// <summary>§54.14A: serving on the ground — same blade, yield and time as
    /// on a surface. Deliberately absent from every catalog definition: it is
    /// an in-place pack action, so no world object advertises it.</summary>
    internal static readonly InteractionDefinition GroundInteraction = new()
    {
        Id = GroundInteractionId, Type = InteractionType.Serve, DurationTicks = ServeDurationTicks,
        RequiredCapabilities = { GearCapability.Butcher }
    };

    public static bool IsSurface(string id) =>
        id is ContentIds.PalmStump or ContentIds.Workbench or ContentIds.ModernTable;

    public static bool IsReadyToServe(string id) => id is
        ContentIds.MeatRoasted or ContentIds.CrabRoasted or ContentIds.WolfCarcassCooked or
        HumanMeatCatalog.ArmCooked or HumanMeatCatalog.MaleArmCooked or
        HumanMeatCatalog.LegCooked or HumanMeatCatalog.MaleLegCooked or
        HumanMeatCatalog.TorsoCooked or HumanMeatCatalog.MaleTorsoCooked;

    public static string PreparedArtId(string id) => id switch
    {
        ContentIds.MeatRoasted => ContentIds.MeatCooked,
        ContentIds.CrabRoasted => ContentIds.CrabCooked,
        _ => id
    };

    internal static string MigrateLegacyCookedId(string id) => id switch
    {
        ContentIds.MeatCooked => ContentIds.MeatRoasted,
        ContentIds.CrabCooked => ContentIds.CrabRoasted,
        _ => id
    };

    internal static int PortionGrams(ItemInstance item) => PortionGrams(item.DefinitionId, item.ResourceAmount);

    internal static int PortionGrams(string id, float amount)
    {
        if (!IsReadyToServe(id)) return 0;
        if (HumanMeatCatalog.IsTorso(id)) return LegacyAdjustedGrams(amount, 6, 20_000);
        if (HumanMeatCatalog.IsLeg(id)) return LegacyAdjustedGrams(amount, 3, 10_000);
        if (HumanMeatCatalog.IsArm(id)) return LegacyAdjustedGrams(amount, 2, 5_000);
        if (id == ContentIds.CrabRoasted) return FoodMath.BiteGrams;
        return (id == ContentIds.WolfCarcassCooked ? 3 : 1) * FoodMath.MeatPortionGrams;
    }

    private static int LegacyAdjustedGrams(float amount, int portions, int oldFullGrams)
    {
        var newGrams = portions * FoodMath.MeatPortionGrams;
        // Fresh preparations and whole legacy parts used 0/1. Larger values
        // are the old remaining grams and preserve only that share of output.
        if (amount <= 1f) return newGrams;
        var remaining = Math.Clamp(amount / oldFullGrams, 0f, 1f);
        // §54.14A: a nibbled legacy part never serves into nothing — the
        // smallest non-empty remainder still gives one 100 g bite.
        return Math.Max(FoodMath.BiteGrams,
            (int)(remaining * newGrams / FoodMath.BiteGrams) * FoodMath.BiteGrams);
    }

    internal static string OutputFor(string id) =>
        id == ContentIds.CrabRoasted ? ContentIds.CrabCooked : ContentIds.MeatCooked;

    internal static bool HasKnife(NPCState npc) => npc.Body.CanUseToolsOrWeapons &&
        GearCatalog.HasCapability(npc.Inventory.Items, GearCapability.Butcher);

    internal static ItemInstance? FirstPreparation(NPCState npc) =>
        npc.Inventory.Items.Find(item => IsReadyToServe(item.DefinitionId));

    /// <summary>§54.14A: a blank in the pack plus a Butcher blade is always a
    /// meal — a surface only changes where it is served, never whether.</summary>
    internal static bool CanServe(NPCState npc) => HasKnife(npc) && FirstPreparation(npc) != null;

    internal static bool IsUsableSurface(WorldState world, NPCState npc, WorldObjectState surface) =>
        IsSurface(surface.DefinitionId) && !surface.IsCraftProject && !BuildSiteMath.IsSite(surface) &&
        surface.Fragment == npc.Fragment && surface.Junctions.Count > 0 &&
        (!surface.IsOccupied || surface.CurrentUser == npc.Id) && CampPropertyMath.CanUse(world, npc, surface) &&
        world.Content.ObjectDefinitions.TryGetValue(surface.DefinitionId, out var definition) &&
        definition.Interactions.Exists(action => action.Type == InteractionType.Serve);

    /// <summary>§54.14A: the nearest usable surface within
    /// <see cref="SurfaceSearchTiles"/> hexes. Rings of tiles through the
    /// per-tile object index (§158.1) — never a pass over every object.</summary>
    internal static WorldObjectState? FindSurface(WorldState world, NPCState npc, int maxTiles = SurfaceSearchTiles) =>
        FindNear(world, npc, maxTiles, candidate => IsUsableSurface(world, npc, candidate));

    /// <summary>§54.14A: a palm she can fell with what she carries — the stump
    /// it leaves is the surface. Same bounded ring search.</summary>
    internal static WorldObjectState? FindPalmForStump(WorldState world, NPCState npc, out InteractionDefinition? chop)
    {
        chop = null;
        var palm = FindNear(world, npc, SurfaceSearchTiles, candidate => ChopFor(world, npc, candidate) != null &&
            candidate.Fragment == npc.Fragment && candidate.Junctions.Count > 0 && !candidate.IsOccupied &&
            !npc.Memory.IsShunned(candidate.Id, world.Tick) && CampPropertyMath.CanUse(world, npc, candidate));
        if (palm != null) chop = ChopFor(world, npc, palm);
        return palm;
    }

    private static InteractionDefinition? ChopFor(WorldState world, NPCState npc, WorldObjectState candidate)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(candidate.DefinitionId, out var definition) ||
            !definition.HasTag("Palm")) return null;
        return definition.Interactions.Find(action => action.Type == InteractionType.Harvest &&
            action.RequiredCapabilities.Count > 0 && DecisionSystem.HasAnyCapability(npc, action.RequiredCapabilities));
    }

    private static WorldObjectState? FindNear(WorldState world, NPCState npc, int maxTiles,
        Func<WorldObjectState, bool> accept)
    {
        WorldObjectState? best = null;
        var nearest = float.PositiveInfinity;
        var center = npc.Tile;
        for (var ring = 0; ring <= maxTiles; ring++)
        {
            if (best != null && LocalSearch.RingLowerBound(ring) > nearest) break;
            for (var dq = -ring; dq <= ring; dq++)
            for (var dr = Math.Max(-ring, -dq - ring); dr <= Math.Min(ring, -dq + ring); dr++)
            {
                if (Math.Max(Math.Abs(dq), Math.Max(Math.Abs(dr), Math.Abs(dq + dr))) != ring ||
                    !world.Caches.ObjectsByTile.TryGetValue(new TileCoord(center.Q + dq, center.R + dr), out var ids))
                    continue;
                foreach (var id in ids)
                {
                    if (!world.Entities.Objects.TryGetValue(id, out var candidate)) continue;
                    var distance = HexSpatialMath.Distance(npc.Position, HexSpatialMath.TileToWorld(candidate.Tile));
                    if (distance > nearest || distance == nearest && best != null && candidate.Id.Value >= best.Id.Value ||
                        !accept(candidate)) continue;
                    var definition = world.Content.ObjectDefinitions[candidate.DefinitionId];
                    if (!PlanningSystem.HasUsableBesideJunction(world, npc, candidate.Junctions[0],
                            SpatialQueries.BesideReach(definition.ObstacleRadius), candidate)) continue;
                    nearest = distance;
                    best = candidate;
                }
            }
        }
        return best;
    }

    /// <summary>§54.14A: work time of one serving — the same base 36 ticks
    /// scaled by the blade and her hands, on a surface or on the ground.</summary>
    internal static int ServeTicks(NPCState npc, GoalType goal)
    {
        var tool = GearCatalog.BestToolFor(npc.Inventory.Items, GearCapability.Butcher, npc.Body.IntactHands);
        var speed = tool is null ? 1f : GearCatalog.For(tool.DefinitionId).HarvestSpeedMult;
        return Math.Max(MinimumServeTicks, AttributeMath.WorkTicks(npc, GearCatalog.ScaleTicks(ServeDurationTicks, speed),
            InteractionType.Serve, goal));
    }

    /// <summary>§54.14A: replace the blank by its portions. Nothing is removed
    /// unless at least one bite comes out, so serving can never destroy food.</summary>
    internal static bool TryServe(WorldState world, NPCState npc, ItemInstance blank, out int total)
    {
        total = 0;
        if (!IsReadyToServe(blank.DefinitionId) || !HasKnife(npc)) return false;
        total = PortionGrams(blank);
        if (total <= 0 || !InventoryMath.RemoveReference(npc.Inventory.Items, blank)) return false;
        var outputId = OutputFor(blank.DefinitionId);
        var portionSize = outputId == ContentIds.CrabCooked ? FoodMath.BiteGrams : FoodMath.MeatPortionGrams;
        for (var left = total; left > 0; left -= portionSize)
            ExecutionSystem.GiveOrDrop(world, npc, new ItemInstance(outputId) { ResourceAmount = Math.Min(left, portionSize) });
        return true;
    }

    internal static void ApplyActionRules(Dictionary<string, ObjectDefinition> definitions)
    {
        foreach (var definition in definitions.Values)
        {
            if (IsReadyToServe(definition.Id))
            {
                definition.Tags.RemoveAll(tag => tag == ObjectTags.Food);
                definition.Interactions.RemoveAll(action => action.Type == InteractionType.Eat);
            }
            if (!IsSurface(definition.Id) ||
                definition.Interactions.Exists(action => action.Id == InteractionId)) continue;
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = InteractionId, Type = InteractionType.Serve, DurationTicks = ServeDurationTicks,
                RequiredCapabilities = { GearCapability.Butcher }
            });
        }
    }

    internal static void AppendDefinitions(Dictionary<string, ObjectDefinition> definitions)
    {
        AddPreparation(ContentIds.MeatRoasted, "Roasted meat");
        AddPreparation(ContentIds.CrabRoasted, "Roasted crab");
        AddPreparation(ContentIds.WolfCarcassRaw, "Skinned wolf carcass", raw: true);
        AddPreparation(ContentIds.WolfCarcassCooked, "Roasted wolf carcass");

        void AddPreparation(string id, string name, bool raw = false)
        {
            var definition = new ObjectDefinition { Id = id, DisplayName = name };
            definition.Tags.Add(ObjectTags.Resource);
            if (raw) definition.Tags.Add(ObjectTags.RawMeat);
            definition.Interactions.Add(new InteractionDefinition
            {
                Id = "pickup." + id, Type = InteractionType.PickUp, DurationTicks = 4
            });
            definitions.Add(id, definition);
        }
    }
}
}
