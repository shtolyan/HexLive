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
    internal const string ResumeInteractionId = "craft.resume";

    internal static bool TryGetSupport(WorldState world, WorldObjectState output,
        out WorldObjectState station)
    {
        station = null;
        if (output == null || output.CraftWorkRequired <= 0 ||
            output.CraftStationObjectId is not { } stationId ||
            stationId == output.Id ||
            !world.Entities.Objects.TryGetValue(stationId, out var candidate) ||
            candidate.CraftWorkRequired > 0 ||
            candidate.CraftJunction is not { } workPoint ||
            !candidate.Fragment.Equals(output.Fragment) || !candidate.Tile.Equals(output.Tile) ||
            candidate.Junctions.Count == 0 || output.Junctions.Count == 0 ||
            !candidate.Junctions[0].Equals(output.Junctions[0]) ||
            !world.Junctions.Items.ContainsKey(workPoint)) return false;
        station = candidate;
        return true;
    }

    internal static bool CanReachSupportedOutput(WorldState world, NPCState npc,
        WorldObjectState output, out JunctionId point)
    {
        point = default;
        if (!TryGetSupport(world, output, out var station) ||
            !station.Fragment.Equals(npc.Fragment) || npc.CurrentJunction is not { } from ||
            !world.Content.ObjectDefinitions.TryGetValue(station.DefinitionId, out var definition)) return false;
        point = station.CraftJunction.Value;
        return Connectivity.Reachable(world, from, point, npc.Body.CanJump) &&
            SpatialQueries.CanTouchAcross(world, point, station.Junctions[0],
                InteractionReach.ForObject(definition.ObstacleRadius), station, InteractionReach.RimMode);
    }
    // §138.5 (#233/#234): §119.2 is an economy duplicate guard, not
    // crafting physics. Autonomous demand must pick up an available output;
    // an accepted manual craft order means the player explicitly asked for one
    // more item. Manual craft goals cannot be assigned by auction/planning, so
    // a manual item-craft goal is the durable proof of that accepted order.
    internal static bool IsPlayerOrderedCraft(NPCState npc, GoalType goal) =>
        ManualControlMath.IsManual(npc) &&
        NpcControlPolicy.IsManualCraftGoal(goal);

    internal static bool CanBeginCycle(
        WorldState world, NPCState npc, GoalType goal, WorldObjectState station)
    {
        if (!RecipeCatalog.UsesPersistentProject(goal) ||
            !RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe) ||
            !StationMatches(world, station, recipe.Station))
        {
            return false;
        }

        // §119.2: this is the last, atomic defence against duplicate output.
        // Decision and Planning normally redirect the demand to PickUp, but a
        // result may enter perception between those passes. No item recipe is
        // allowed to pay another bill while its finished output is visible.
        if (!IsPlayerOrderedCraft(npc, goal) &&
            HasReachableCompletedOutput(world, npc, goal))
        {
            return false;
        }

        if (TryFindProject(world, npc, goal, station, requireNearby: true, out var project))
        {
            return !project.IsOccupied || project.CurrentUser == npc.Id;
        }

        if (PinnedProject(npc, goal).HasValue) return false;

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

    // Bug #86/#92 generalized: every item-output recipe derives its duplicate
    // guard from RecipeCatalog.OutputOf. Adding a new item recipe therefore
    // needs no second hand-maintained list here or in PlanningSystem.
    internal static bool HasReachableCompletedOutput(
        WorldState world, NPCState npc, GoalType goal) =>
        TryFindReachableCompletedOutput(
            world, npc, goal, requireInventoryRoom: false, out _);

    /// <summary>
    /// A demand can be satisfied by taking a finished output, resuming a paid
    /// project, or (last) paying a new bill. A visible result which cannot fit
    /// deliberately returns false instead of authorizing a duplicate.
    /// </summary>
    internal static bool CanSatisfyItemCraftDemand(
        WorldState world, NPCState npc, GoalType goal, bool canManufacture)
    {
        if (TryFindReachableCompletedOutput(
                world, npc, goal, requireInventoryRoom: false, out _))
        {
            return TryFindReachableCompletedOutput(
                world, npc, goal, requireInventoryRoom: true, out _);
        }

        return HasReachableProject(world, npc, goal) || canManufacture;
    }

    internal static bool TryFindTakeableCompletedOutput(
        WorldState world, NPCState npc, GoalType goal, out WorldObjectState source) =>
        TryFindReachableCompletedOutput(
            world, npc, goal, requireInventoryRoom: true, out source);

    // A consumable may be used straight from its world object/container. This
    // deliberately skips only the inventory-capacity gate; perception,
    // reachability, reservations and the "finished project" invariant remain
    // exactly the same as for the ordinary pickup path.
    internal static bool TryFindUsableCompletedOutput(
        WorldState world, NPCState npc, GoalType goal, out WorldObjectState source) =>
        TryFindReachableCompletedOutput(
            world, npc, goal, requireInventoryRoom: false, out source);

    private static bool TryFindReachableCompletedOutput(
        WorldState world, NPCState npc, GoalType goal, bool requireInventoryRoom,
        out WorldObjectState source)
    {
        source = null;
        var output = RecipeCatalog.OutputOf(goal);
        if (string.IsNullOrEmpty(output)) return false;

        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable || perceived.DefinitionId != output ||
                !perceived.AvailableInteractions.Contains(InteractionType.PickUp) ||
                !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var candidate) ||
                !candidate.Fragment.Equals(npc.Fragment) || candidate.IsOccupied ||
                (requireInventoryRoom &&
                 !InventoryMath.CanMakeRoomForGoal(world, npc, goal, output)))
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
                source = candidate;
                return true;
            }
        }

        // §133 (продолжение той же мысли, что и #86/#92): готовый выход может
        // лежать не на земле, а В КАРМАНЕ брошенной одежды — снятая куртка
        // уносит с собой всё, что не влезло в рюкзак (§52). GatherTools такую
        // заначку видит (InventoryMath.StashHoldsWantedTool), а крафт не видел
        // — и колония делала второй нож, когда первый лежал в кармане куртки
        // в трёх шагах. Тот же вопрос, тот же ответ.
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable ||
                !perceived.AvailableInteractions.Contains(InteractionType.PickUp) ||
                !DecisionSystem.ObjectUsableBy(perceived, npc.Id) ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var container) ||
                container.IsCraftProject || container.IsOccupied ||
                container.Contents.Count == 0 ||
                !container.Fragment.Equals(npc.Fragment) ||
                // Contents is not synonymous with a pocket. Build sites,
                // stations and process objects also own paid/working contents;
                // treating those as stash made a rope delivered into a site
                // look like loose rope and produced an endless pickup plan.
                !world.Content.ObjectDefinitions.TryGetValue(
                    container.DefinitionId, out var containerDefinition) ||
                containerDefinition.Layer == null)
            {
                continue;
            }

            foreach (var stashed in container.Contents)
            {
                if (stashed.DefinitionId == output &&
                    (!requireInventoryRoom ||
                     InventoryMath.CanMakeRoomForGoal(world, npc, goal, output)))
                {
                    source = container;
                    return true;
                }
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
            if (PinnedProject(npc, goal) is { } exact && candidate.Id != exact) continue;
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

        // A late perception update can race the plan. Keep the invariant at
        // the mutation boundary too: never create/resume manufacturing while
        // a finished copy is already available to this worker.
        if (!IsPlayerOrderedCraft(npc, goal) &&
            HasReachableCompletedOutput(world, npc, goal))
        {
            return false;
        }

        if (!TryFindProject(world, npc, goal, station, requireNearby: true, out project))
        {
            if (PinnedProject(npc, goal).HasValue) return false;
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
        project.CraftLastWorkerId = npc.Id;
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
        // must run the completion adapter.

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
            if (obj.CraftStationObjectId is { } stationId &&
                (!world.Entities.Objects.TryGetValue(stationId, out var station) ||
                 !station.Fragment.Equals(obj.Fragment) || !station.Tile.Equals(obj.Tile) ||
                 station.Junctions.Count == 0 || obj.Junctions.Count == 0 ||
                 station.Junctions[0] != obj.Junctions[0]))
            {
                if (obj.IsCraftProject) (orphaned ??= new List<ObjectId>()).Add(obj.Id);
                else obj.CraftStationObjectId = null;
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
                !definition.HasTag(tag)) continue;
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
        if (goal == GoalType.CraftBandage)
        {
            project.ResourceAmount = 1f; // persisted herbal provenance marker
        }

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
                    WaterKind = source.WaterKind,
                    LastAddedWaterKind = source.LastAddedWaterKind,
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
            if (PinnedProject(npc, goal) is { } exact && candidate.Id != exact) continue;
            if (!candidate.IsCraftProject || candidate.DefinitionId != output ||
                !candidate.Fragment.Equals(npc.Fragment) ||
                (candidate.IsOccupied && candidate.CurrentUser != npc.Id)) continue;
            var match = station is not null
                ? candidate.CraftStationObjectId == station.Id &&
                  station.Fragment.Equals(candidate.Fragment) && station.Tile.Equals(candidate.Tile) &&
                  station.Junctions.Count > 0 && candidate.Junctions.Count > 0 &&
                  station.Junctions[0] == candidate.Junctions[0]
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

    private static ObjectId? PinnedProject(NPCState npc, GoalType goal) =>
        npc.Mind.ManualControl && npc.Plan.Goal == goal && npc.Plan.Status == PlanStatus.Active
            ? npc.Plan.CraftProjectTargetId : null;

    private static bool StationHasProject(WorldState world, WorldObjectState station)
    {
        if (station is null) return false;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (candidate.CraftWorkRequired > 0 && candidate.CraftStationObjectId == station.Id)
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
            definition.HasTag(stationTag);
    }

    private static void CopyItem(ItemInstance item, WorldObjectState target)
    {
        target.Wetness = item.Wetness;
        target.Durability = item.Durability;
        target.ResourceAmount = item.ResourceAmount;
        target.WaterKind = item.WaterKind;
        target.LastAddedWaterKind = item.LastAddedWaterKind;
        target.Dirtiness = item.Dirtiness;
        target.Bloodiness = item.Bloodiness;
    }
}

}
