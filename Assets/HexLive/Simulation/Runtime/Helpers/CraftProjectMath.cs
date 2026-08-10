using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §119 persistent item crafting. The output object is spawned at 0% and owns
/// its paid ingredient instances until it becomes a normal item at 100%.
/// </summary>
internal static class CraftProjectMath
{
    internal static bool CanBeginCycle(
        WorldState world, NPCState npc, GoalType goal, WorldObjectState station)
    {
        if (!RecipeCatalog.UsesPersistentProject(goal) ||
            !RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe) ||
            !StationMatches(world, station, recipe.Station))
        {
            return false;
        }

        if (TryFindProject(world, npc, goal, station, requireNearby: true, out var project))
        {
            return !project.IsOccupied || project.CurrentUser == npc.Id;
        }

        if (StationHasProject(world, station))
        {
            return false; // one tabletop, one unfinished output
        }

        return HasAtomicBill(world, npc, recipe, station);
    }

    internal static bool HasReachableProject(WorldState world, NPCState npc, GoalType goal)
    {
        return FindReachableProject(world, npc, goal) is not null;
    }

    // Bug #86: a 100% project is deliberately no longer IsCraftProject, but it
    // is still the result which satisfied the craft demand. Without this seam
    // CraftKnife saw only the crafter's pack, outbid GatherTools, and paid for
    // another knife while the previous one was lying at her feet.
    internal static bool HasReachableCompletedOutput(
        WorldState world, NPCState npc, GoalType goal)
    {
        var output = RecipeCatalog.OutputOf(goal);
        if (string.IsNullOrEmpty(output)) return false;

        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable || perceived.DefinitionId != output ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var candidate) ||
                !candidate.Fragment.Equals(npc.Fragment) || candidate.IsOccupied ||
                !InventoryMath.CanMakeRoomFor(world, npc, output))
            {
                continue;
            }

            // A normal ground tool has no craft counters at all; a persistent
            // project carries them and is usable only at 100%. Both are a
            // finished output which must be picked up before another copy is
            // manufactured (bug #92 generalises the knife-only #86 fix).
            if (!candidate.IsCraftProject &&
                (candidate.CraftWorkRequired <= 0 ||
                 candidate.CraftWorkDone >= candidate.CraftWorkRequired))
            {
                return true;
            }
        }

        return false;
    }

    internal static WorldObjectState FindReachableProject(
        WorldState world, NPCState npc, GoalType goal)
    {
        var output = RecipeCatalog.OutputOf(goal);
        WorldObjectState best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (!candidate.IsCraftProject || candidate.DefinitionId != output ||
                !candidate.Fragment.Equals(npc.Fragment) ||
                (candidate.IsOccupied && candidate.CurrentUser != npc.Id))
            {
                continue;
            }

            // A station-bound project is resumable only while its authored
            // station still exists. Orphans are spilled by the slow cleanup,
            // but the planner must never route a worker to one in the interim.
            if (candidate.CraftStationObjectId is { } stationId &&
                !world.Entities.Objects.ContainsKey(stationId))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(candidate.Tile, npc.Tile);
            if (distance < bestDistance ||
                (distance == bestDistance && (best is null || candidate.Id.Value < best.Id.Value)))
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    internal static bool TryBeginCycle(
        WorldState world, NPCState npc, GoalType goal, WorldObjectState station,
        out WorldObjectState project)
    {
        project = null;
        if (!RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe) ||
            !RecipeCatalog.UsesPersistentProject(goal) ||
            !StationMatches(world, station, recipe.Station))
        {
            return false;
        }

        if (!TryFindProject(world, npc, goal, station, requireNearby: true, out project))
        {
            if (StationHasProject(world, station))
            {
                return false;
            }
            project = CreateProject(world, npc, goal, recipe, station);
            if (project is null)
            {
                return false;
            }
        }

        if (project.IsOccupied && project.CurrentUser != npc.Id)
        {
            return false;
        }

        project.IsOccupied = true;
        project.CurrentUser = npc.Id;
        npc.Execution.CraftProjectId = project.Id;
        npc.Execution.CraftCycleStartWork = project.CraftWorkDone;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CraftProjectCycleStarted",
                $"Project={project.Id.Value} Output={project.DefinitionId} " +
                $"Progress={project.CraftWorkDone}/{project.CraftWorkRequired}");
        }
        return true;
    }

    internal static bool CompleteCycle(WorldState world, NPCState npc, GoalType goal)
    {
        if (npc.Execution.CraftProjectId is not { } projectId ||
            !world.Entities.Objects.TryGetValue(projectId, out var project) ||
            project.CraftWorkRequired <= 0 ||
            project.DefinitionId != RecipeCatalog.OutputOf(goal) ||
            !project.IsOccupied ||
            project.CurrentUser != npc.Id)
        {
            npc.Execution.CraftProjectId = null;
            return false;
        }

        // UpdateCycleProgress runs before this method and is allowed to expose
        // the final visual frame as exactly 100%. At that point IsCraftProject
        // is deliberately false, but this NPC still owns the active cycle and
        // must run the completion adapter (notably CraftBandage's counters).

        project.CraftWorkDone = System.Math.Min(project.CraftWorkRequired,
            System.Math.Max(project.CraftWorkDone,
                npc.Execution.CraftCycleStartWork + Spec119.CraftCycleWork));
        project.IsOccupied = false;
        project.CurrentUser = null;
        npc.Execution.CraftProjectId = null;
        npc.Execution.CraftCycleStartWork = 0;

        if (project.CraftWorkDone >= project.CraftWorkRequired)
        {
            project.CraftWorkDone = project.CraftWorkRequired;
            project.Contents.Clear(); // paid ingredients are consumed only now
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CraftProjectCompleted",
                    $"Project={project.Id.Value} Output={project.DefinitionId} Work={project.CraftWorkDone}");
            }

            // Herbal dressings predate physical inventory and are still stored
            // as the two medical counters consumed by every aid path. Keep the
            // authored output object for the whole 0..99% craft, then stow the
            // finished wrap immediately so existing treatment semantics remain
            // atomic and no unusable item.bandage is left on the ground.
            if (goal == GoalType.CraftBandage)
            {
                npc.Needs.Bandages++;
                npc.Needs.HerbalBandages++;
                WorldObjectMutations.DespawnObject(world, project.Id);
                Trace.Emit(world, npc.Id, "BandageCrafted",
                    $"Bandages={npc.Needs.Bandages} Herbal={npc.Needs.HerbalBandages}");
            }
        }
        else
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CraftProjectProgress",
                    $"Project={project.Id.Value} Output={project.DefinitionId} " +
                    $"Progress={project.CraftWorkDone}/{project.CraftWorkRequired}");
            }
        }

        return true;
    }

    internal static void UpdateCycleProgress(WorldState world, NPCState npc)
    {
        if (npc.Execution.CraftProjectId is not { } projectId ||
            !world.Entities.Objects.TryGetValue(projectId, out var project) ||
            !project.IsCraftProject)
        {
            return;
        }

        var total = System.Math.Max(1, npc.Execution.EndTick - npc.Execution.StartTick);
        var elapsed = System.Math.Clamp(world.Tick - npc.Execution.StartTick, 0, total);
        var credited = npc.Execution.CraftCycleStartWork +
            (int)System.MathF.Floor(Spec119.CraftCycleWork * (elapsed / (float)total));
        project.CraftWorkDone = System.Math.Min(project.CraftWorkRequired,
            System.Math.Max(project.CraftWorkDone, credited));
    }

    internal static void ReleaseWorker(WorldState world, NPCState npc)
    {
        if (npc.Execution.CraftProjectId is { } id &&
            world.Entities.Objects.TryGetValue(id, out var project) &&
            project.CurrentUser == npc.Id)
        {
            project.IsOccupied = false;
            project.CurrentUser = null;
        }
        npc.Execution.CraftProjectId = null;
        npc.Execution.CraftCycleStartWork = 0;
    }

    internal static void CancelOrphanedProjects(WorldState world)
    {
        List<ObjectId> orphaned = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.IsCraftProject && obj.CraftStationObjectId is { } stationId &&
                !world.Entities.Objects.ContainsKey(stationId))
            {
                (orphaned ??= new List<ObjectId>()).Add(obj.Id);
            }
        }

        if (orphaned is null) return;
        foreach (var id in orphaned) Cancel(world, id, "station destroyed");
    }

    internal static bool Cancel(WorldState world, ObjectId projectId, string reason)
    {
        if (!world.Entities.Objects.TryGetValue(projectId, out var project) ||
            !project.IsCraftProject || project.Junctions.Count == 0)
        {
            return false;
        }

        var anchor = project.Junctions[0];
        var ingredients = new List<ItemInstance>(project.Contents);
        project.Contents.Clear();
        foreach (var item in ingredients)
        {
            var dropped = WorldObjectMutations.SpawnObject(
                world, item.DefinitionId, project.Fragment, project.Tile, anchor);
            CopyItem(item, dropped);
        }
        WorldObjectMutations.DespawnObject(world, project.Id);
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "CraftProjectCancelled",
                $"Project={projectId.Value} reason={reason}; spilled={ingredients.Count}");
        }
        return true;
    }

    internal static WorldObjectState FindStationForGoal(
        WorldState world, NPCState npc, GoalType goal)
    {
        var tag = RecipeCatalog.StationOf(goal);
        if (string.IsNullOrEmpty(tag)) return null;
        WorldObjectState best = null;
        var bestDistance = int.MaxValue;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains(tag)) continue;
            var distance = HexSpatialMath.HexDistance(npc.Tile, obj.Tile);
            if (distance < bestDistance ||
                (distance == bestDistance && (best is null || obj.Id.Value < best.Id.Value)))
            {
                best = obj;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static WorldObjectState CreateProject(
        WorldState world, NPCState npc, GoalType goal, Recipe recipe, WorldObjectState station)
    {
        if (!HasAtomicBill(world, npc, recipe, station)) return null;
        var outputId = RecipeCatalog.OutputOf(goal);
        if (string.IsNullOrEmpty(outputId)) return null;

        var anchor = station is not null && station.Junctions.Count > 0
            ? station.Junctions[0]
            : npc.CurrentJunction;
        if (anchor is not { } junction) return null;
        var tile = station?.Tile ?? npc.Tile;
        var project = WorldObjectMutations.SpawnObject(
            world, outputId, npc.Fragment, tile, junction);
        project.RotationDegrees = station?.RotationDegrees ?? npc.RotationDegrees;
        project.CraftStationObjectId = station?.Id;
        project.CraftWorkRequired = recipe.BaseWorkTicks;
        project.CraftWorkDone = 0;
        project.CraftBatchCount = 1;

        // Validation above makes this mutation atomic: either the whole bill
        // moves into the project, or no item and no project changes state.
        foreach (var ingredient in recipe.Inputs)
        {
            var remaining = ingredient.Count;
            var ground = GroundInputs(world, npc, station, ingredient.Id);
            for (var i = 0; i < ground.Count && remaining > 0; i++, remaining--)
            {
                var source = ground[i];
                project.Contents.Add(new ItemInstance(source.DefinitionId)
                {
                    Wetness = source.Wetness,
                    Durability = source.Durability,
                    ResourceAmount = source.ResourceAmount,
                    Dirtiness = source.Dirtiness,
                    Bloodiness = source.Bloodiness
                });
                WorldObjectMutations.DespawnObject(world, source.Id);
            }
            while (remaining-- > 0)
            {
                var index = npc.Inventory.Items.IndexOf(ingredient.Id);
                var item = npc.Inventory.Items[index];
                npc.Inventory.Items.RemoveAt(index);
                project.Contents.Add(item);
            }
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CraftProjectCreated",
                $"Project={project.Id.Value} Output={outputId} Work=0/{project.CraftWorkRequired} " +
                $"Ingredients={project.Contents.Count} Station={station?.Id.Value.ToString() ?? "ground"}");
        }
        return project;
    }

    private static bool HasAtomicBill(
        WorldState world, NPCState npc, Recipe recipe, WorldObjectState station)
    {
        foreach (var ingredient in recipe.Inputs)
        {
            var available = DecisionSystem.CountInventory(npc, ingredient.Id) +
                GroundInputs(world, npc, station, ingredient.Id).Count;
            if (available < ingredient.Count) return false;
        }
        return true;
    }

    private static List<WorldObjectState> GroundInputs(
        WorldState world, NPCState npc, WorldObjectState station, string definitionId)
    {
        var tile = station?.Tile ?? npc.Tile;
        var found = new List<WorldObjectState>();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == definitionId && !obj.IsOccupied && !obj.IsCraftProject &&
                obj.Contents.Count == 0 && string.IsNullOrEmpty(obj.BuildProduct) &&
                obj.Fragment.Equals(npc.Fragment) &&
                HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
            {
                found.Add(obj);
            }
        }
        found.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return found;
    }

    private static bool TryFindProject(
        WorldState world, NPCState npc, GoalType goal, WorldObjectState station,
        bool requireNearby, out WorldObjectState project)
    {
        project = null;
        var output = RecipeCatalog.OutputOf(goal);
        var bestDistance = int.MaxValue;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (!candidate.IsCraftProject || candidate.DefinitionId != output ||
                !candidate.Fragment.Equals(npc.Fragment) ||
                (candidate.IsOccupied && candidate.CurrentUser != npc.Id)) continue;
            var match = station is not null
                ? candidate.CraftStationObjectId == station.Id
                : !candidate.CraftStationObjectId.HasValue;
            if (!match) continue;
            var distance = HexSpatialMath.HexDistance(candidate.Tile, npc.Tile);
            if (requireNearby && station is null && distance > 1) continue;
            if (distance < bestDistance ||
                (distance == bestDistance && (project is null || candidate.Id.Value < project.Id.Value)))
            {
                project = candidate;
                bestDistance = distance;
            }
        }
        return project is not null;
    }

    private static bool StationHasProject(WorldState world, WorldObjectState station)
    {
        if (station is null) return false;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (candidate.IsCraftProject && candidate.CraftStationObjectId == station.Id)
            {
                return true;
            }
        }
        return false;
    }

    private static bool StationMatches(
        WorldState world, WorldObjectState station, string stationTag)
    {
        if (string.IsNullOrEmpty(stationTag)) return station is null;
        return station is not null &&
            world.Content.ObjectDefinitions.TryGetValue(station.DefinitionId, out var definition) &&
            definition.Tags.Contains(stationTag);
    }

    private static void CopyItem(ItemInstance item, WorldObjectState target)
    {
        target.Wetness = item.Wetness;
        target.Durability = item.Durability;
        target.ResourceAmount = item.ResourceAmount;
        target.Dirtiness = item.Dirtiness;
        target.Bloodiness = item.Bloodiness;
    }
}

}
