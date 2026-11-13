using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§54.17: roasting preserves the whole preparation until it is divided at a surface.</summary>
public static class MealServing
{
    public const string InteractionId = "serve.meal";

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
        return (int)(remaining * newGrams / FoodMath.BiteGrams) * FoodMath.BiteGrams;
    }

    internal static string OutputFor(string id) =>
        id == ContentIds.CrabRoasted ? ContentIds.CrabCooked : ContentIds.MeatCooked;

    internal static bool HasKnife(NPCState npc) => npc.Body.CanUseToolsOrWeapons &&
        GearCatalog.HasCapability(npc.Inventory.Items, GearCapability.Butcher);

    internal static ItemInstance? FirstPreparation(NPCState npc) =>
        npc.Inventory.Items.Find(item => IsReadyToServe(item.DefinitionId));

    internal static bool IsUsableSurface(WorldState world, NPCState npc, WorldObjectState surface) =>
        IsSurface(surface.DefinitionId) && !surface.IsCraftProject && !BuildSiteMath.IsSite(surface) &&
        surface.Fragment == npc.Fragment && surface.Junctions.Count > 0 &&
        (!surface.IsOccupied || surface.CurrentUser == npc.Id) && CampPropertyMath.CanUse(world, npc, surface) &&
        world.Content.ObjectDefinitions.TryGetValue(surface.DefinitionId, out var definition) &&
        definition.Interactions.Exists(action => action.Type == InteractionType.Serve);

    internal static WorldObjectState? FindSurface(WorldState world, NPCState npc, int maxTiles = int.MaxValue)
    {
        WorldObjectState? best = null;
        var nearest = float.PositiveInfinity;
        foreach (var surface in world.Entities.Objects.Values)
        {
            if (!IsUsableSurface(world, npc, surface) ||
                HexSpatialMath.HexDistance(npc.Tile, surface.Tile) > maxTiles) continue;
            var distance = HexSpatialMath.Distance(npc.Position, HexSpatialMath.TileToWorld(surface.Tile));
            if (distance > nearest || distance == nearest && best != null && surface.Id.Value >= best.Id.Value)
                continue;
            var definition = world.Content.ObjectDefinitions[surface.DefinitionId];
            if (!PlanningSystem.HasUsableBesideJunction(world, npc, surface.Junctions[0],
                    SpatialQueries.BesideReach(definition.ObstacleRadius), surface)) continue;
            nearest = distance;
            best = surface;
        }
        return best;
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
                Id = InteractionId, Type = InteractionType.Serve, DurationTicks = 24,
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
