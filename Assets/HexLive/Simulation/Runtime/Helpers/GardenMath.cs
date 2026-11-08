using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§173.8: crop targets, seed errands and shared planting claims.</summary>
internal static class GardenMath
{
    internal const int MinRadiusTiles = 2;
    internal const int RadiusTiles = 4;
    internal const float PlantSpacing = HexSpatialMath.HexRadius * 0.5f;

    internal sealed class Task
    {
        public PlantGrowthCatalog.Profile Crop;
        public int SeedIndex = -1;
        public PerceivedObject Source;
        public InteractionType Interaction;
    }

    internal static IEnumerable<WorldObjectState> NearbyPlants(WorldState world, TileCoord home)
    {
        for (var q = -RadiusTiles; q <= RadiusTiles; q++)
        for (var r = Math.Max(-RadiusTiles, -q - RadiusTiles);
             r <= Math.Min(RadiusTiles, -q + RadiusTiles); r++)
        {
            var tile = new TileCoord(home.Q + q, home.R + r);
            if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var ids)) continue;
            foreach (var id in ids)
                if (world.Entities.Objects.TryGetValue(id, out var plant)) yield return plant;
        }
    }

    internal static int Count(WorldState world, Faction camp, PlantGrowthCatalog.Profile crop)
    {
        if (ColonyQueries.Home(world, camp) is not { } home) return 0;
        var count = 0;
        foreach (var plant in NearbyPlants(world, home))
            if (ColonyQueries.InCamp(world, plant.Tile, camp) &&
                (plant.DefinitionId == crop.MatureDefinitionId ||
                 Array.IndexOf(crop.StageDefinitionIds, plant.DefinitionId) >= 0)) count++;
        foreach (var worker in world.Entities.Npcs.Values)
            if (worker.Faction == camp && worker.Plan.Status == PlanStatus.Active &&
                worker.Plan.Goal == GoalType.TendGarden &&
                worker.Plan.TargetObjectId == null &&
                worker.Plan.TargetItemDefinitionId == crop.SeedDefinitionId) count++;
        return count;
    }

    internal static bool TryChooseTask(WorldState world, NPCState npc, out Task task)
    {
        task = null;
        if (ColonyQueries.Home(world, npc.Faction) is not { } home) return false;
        var bestDeficit = 0;
        foreach (var crop in PlantGrowthCatalog.All)
        {
            var deficit = crop.GardenTargetCount - Count(world, npc.Faction, crop);
            if (deficit <= bestDeficit) continue;
            var seedIndex = npc.Inventory.Items.FindIndex(i => i.DefinitionId == crop.SeedDefinitionId);
            var candidate = new Task { Crop = crop, SeedIndex = seedIndex };
            if (seedIndex < 0)
            {
                if (!npc.Inventory.HasSpace) continue;
                // Use perception (including its remembered candidates), not distant live objects.
                foreach (var seen in npc.Perception.Objects)
                {
                    if (!seen.IsReachable || seen.IsOccupied ||
                        HexSpatialMath.HexDistance(seen.Tile, home) > 12 ||
                        !ColonyQueries.InCamp(world, seen.Tile, npc.Faction) ||
                        !world.Entities.Objects.TryGetValue(seen.Id, out var source) ||
                        source.Owner is { } owner && owner != npc.Id ||
                        !world.Content.ObjectDefinitions.TryGetValue(source.DefinitionId, out var definition)) continue;
                    foreach (var action in definition.Interactions)
                    {
                        var pickup = source.DefinitionId == crop.SeedDefinitionId && action.Type == InteractionType.PickUp;
                        var yieldsSeeds = action.Type == InteractionType.Harvest &&
                            action.Yields.Exists(y => y.DefinitionId == crop.SeedDefinitionId && y.Count > 0);
                        if (!pickup && !yieldsSeeds) continue;
                        var required = GearCatalog.RequiredCapabilities(action, definition);
                        if (required != GearCapability.None &&
                            GearCatalog.BestToolFor(npc.Inventory.Items, required, npc.Body.IntactHands) == null) continue;
                        if (candidate.Source == null ||
                            pickup && candidate.Interaction != InteractionType.PickUp ||
                            pickup == (candidate.Interaction == InteractionType.PickUp) && seen.Distance < candidate.Source.Distance)
                        {
                            candidate.Source = seen;
                            candidate.Interaction = action.Type;
                        }
                    }
                }
                if (candidate.Source == null) continue;
            }
            task = candidate;
            bestDeficit = deficit;
        }
        return task != null;
    }

    internal static bool TryPickSpot(WorldState world, NPCState npc, out Junction point, out TileCoord tile)
    {
        point = null;
        tile = default;
        if (ColonyQueries.Home(world, npc.Faction) is not { } home || npc.CurrentJunction is not { } from) return false;
        var candidates = new List<Junction>();
        LocalSearch.CollectWithinTiles(world, home, RadiusTiles, candidates);
        candidates.Sort((a, b) =>
        {
            var byDistance = HexSpatialMath.Distance(npc.Position, a.WorldPosition)
                .CompareTo(HexSpatialMath.Distance(npc.Position, b.WorldPosition));
            return byDistance != 0 ? byDistance : a.Id.Value.CompareTo(b.Id.Value);
        });
        var plants = new List<WorldObjectState>(NearbyPlants(world, home));
        var danger = PathfindingSystem.DangerRing(world);
        foreach (var candidate in candidates)
        {
            if (danger.Contains(candidate.Id)) continue;
            foreach (var coord in candidate.Tiles)
            {
                var distance = HexSpatialMath.HexDistance(coord, home);
                if (distance < MinRadiusTiles || distance > RadiusTiles ||
                    !ColonyQueries.InCamp(world, coord, npc.Faction) ||
                    !world.Tiles.Items.TryGetValue(coord, out var ground) ||
                    ground.Flags.HasFlag(TileFlags.HasFloor) ||
                    !PlantGrowthSystem.CanPlantAt(world, candidate.Id, coord, npc.Id)) continue;
                var crowded = false;
                foreach (var plant in plants)
                    if ((PlantGrowthCatalog.TryGetByStage(plant.DefinitionId, out _, out _) ||
                         world.Content.ObjectDefinitions.TryGetValue(plant.DefinitionId, out var def) && def.HasTag("SeedPlant")) &&
                        plant.Junctions.Count > 0 &&
                        world.Junctions.Items.TryGetValue(plant.Junctions[0], out var anchor) &&
                        HexSpatialMath.Distance(candidate.WorldPosition, anchor.WorldPosition) < PlantSpacing)
                    { crowded = true; break; }
                foreach (var worker in world.Entities.Npcs.Values)
                    if (worker.Id != npc.Id && worker.Plan.Status == PlanStatus.Active &&
                        worker.Plan.Goal == GoalType.TendGarden && worker.Plan.TargetObjectId == null &&
                        worker.Plan.TargetJunctionId is { } reserved &&
                        world.Junctions.Items.TryGetValue(reserved, out var target) &&
                        HexSpatialMath.Distance(candidate.WorldPosition, target.WorldPosition) < PlantSpacing)
                    { crowded = true; break; }
                if (crowded || !Connectivity.Reachable(world, from, candidate.Id,
                        PlanningSystem.CanUseCriticalTraversal(npc))) continue;
                point = candidate;
                tile = coord;
                return true;
            }
        }
        return false;
    }
}
}
