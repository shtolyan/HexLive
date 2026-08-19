using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§138: stable reason codes shared by the backpack UI and the
/// authoritative command re-check.</summary>
public enum CraftBlockReason
{
    None,
    InvalidRecipe,
    NotManual,
    Incapacitated,
    MissingHands,
    NoStation,
    StationBusy,
    MissingResources,
    Unreachable,
    Active
}

public sealed class CraftIngredientOption
{
    public string DefinitionId { get; set; } = string.Empty;
    public int Available { get; set; }
    public int Required { get; set; }
}

/// <summary>§138 read model. Target ids are advisory: CraftItemCommand resolves
/// the whole option again at the beginning of the simulation tick.</summary>
public sealed class CraftRecipeOption
{
    public GoalType Goal { get; set; }
    public string OutputDefinitionId { get; set; } = string.Empty;
    public string StationTag { get; set; } = string.Empty;
    public ObjectId? StationObjectId { get; set; }
    public ObjectId? ProjectObjectId { get; set; }
    public TileCoord WorkTile { get; set; } = TileCoord.Zero;
    public JunctionId? WorkJunction { get; set; }
    public int WorkDone { get; set; }
    public int WorkRequired { get; set; }
    public bool CanCraft { get; set; }
    public bool IsActive { get; set; }
    public bool IsResume { get; set; }
    public CraftBlockReason BlockReason { get; set; }
    public List<CraftIngredientOption> Ingredients { get; } = new();
}

/// <summary>
/// §138: the ONE answer to "what can this manual NPC craft, where, and from
/// which physical inputs?" Presentation reads it; ManualCommandExecutor
/// re-runs it before mutating a plan.
/// </summary>
public static class CraftingOptions
{
    private sealed class StationCandidate
    {
        public WorldObjectState Station;
        public int Distance;
        public bool GateOk;
        public bool Busy;
        public bool BillCovered;
        public List<CraftIngredientOption> Ingredients;
    }

    public static bool TryFill(
        WorldState world, EntityId npcId, List<CraftRecipeOption> into)
    {
        into.Clear();
        if (world == null || !world.Entities.Npcs.TryGetValue(npcId, out var npc))
        {
            return false;
        }

        var recipes = new List<KeyValuePair<string, Recipe>>(RecipeCatalog.ItemRecipes());
        recipes.Sort((a, b) =>
        {
            var byGoal = ((int)a.Value.Goal).CompareTo((int)b.Value.Goal);
            return byGoal != 0
                ? byGoal
                : string.CompareOrdinal(a.Key, b.Key);
        });

        foreach (var pair in recipes)
        {
            into.Add(Resolve(world, npc, pair.Value.Goal));
        }
        return true;
    }

    internal static CraftRecipeOption Resolve(
        WorldState world, NPCState npc, GoalType goal)
    {
        var option = new CraftRecipeOption
        {
            Goal = goal,
            OutputDefinitionId = RecipeCatalog.OutputOf(goal),
            WorkTile = npc.Tile
        };

        if (!RecipeCatalog.IsItemOutputGoal(goal) ||
            !RecipeCatalog.ByGoal.TryGetValue(goal, out var recipe) ||
            string.IsNullOrEmpty(option.OutputDefinitionId))
        {
            option.BlockReason = CraftBlockReason.InvalidRecipe;
            return option;
        }

        option.StationTag = recipe.Station;
        option.IsActive = npc.Mind.ManualControl &&
            (npc.Mind.CurrentGoal == goal || npc.Plan.Goal == goal) &&
            (npc.Plan.Status == PlanStatus.Active ||
             npc.Execution.Status == ExecutionStatus.InProgress);

        var project = CraftProjectMath.FindReachableProject(world, npc, goal);
        if (project != null)
        {
            ResolveProject(world, npc, recipe, project, option);
        }
        else if (string.IsNullOrEmpty(recipe.Station))
        {
            ResolveInPlace(world, npc, recipe, option);
        }
        else
        {
            ResolveAtStation(world, npc, recipe, option);
        }

        var actorBlock = ActorBlock(world, npc, goal);
        if (actorBlock != CraftBlockReason.None)
        {
            option.CanCraft = false;
            option.BlockReason = actorBlock;
        }
        else if (option.IsActive)
        {
            option.CanCraft = false;
            option.BlockReason = CraftBlockReason.Active;
        }

        return option;
    }

    private static void ResolveProject(
        WorldState world,
        NPCState npc,
        Recipe recipe,
        WorldObjectState project,
        CraftRecipeOption option)
    {
        option.ProjectObjectId = project.Id;
        option.IsResume = true;
        option.WorkTile = project.Tile;
        option.WorkDone = project.CraftWorkDone;
        option.WorkRequired = project.CraftWorkRequired;

        WorldObjectState station = null;
        if (project.CraftStationObjectId is { } stationId)
        {
            world.Entities.Objects.TryGetValue(stationId, out station);
            option.StationObjectId = station?.Id;
        }
        option.WorkJunction = station?.CraftJunction ??
            (project.Junctions.Count > 0 ? project.Junctions[0] : (JunctionId?)null);

        FillIngredientCounts(world, npc, recipe, project.Tile,
            includeGround: false, paidProject: project, option.Ingredients);

        if (!string.IsNullOrEmpty(recipe.Station) && station == null)
        {
            option.BlockReason = CraftBlockReason.NoStation;
            return;
        }

        if (project.IsOccupied && project.CurrentUser != npc.Id)
        {
            option.BlockReason = CraftBlockReason.StationBusy;
            return;
        }

        if (option.WorkJunction is null ||
            !Reachable(world, npc, station ?? project, option.WorkJunction.Value))
        {
            option.BlockReason = CraftBlockReason.Unreachable;
            return;
        }

        option.CanCraft = true;
        option.BlockReason = CraftBlockReason.None;
    }

    private static void ResolveInPlace(
        WorldState world,
        NPCState npc,
        Recipe recipe,
        CraftRecipeOption option)
    {
        var anchorTile = npc.Tile;
        JunctionId? anchorJunction = null;
        FillIngredientCounts(world, npc, recipe, anchorTile,
            includeGround: true, paidProject: null, option.Ingredients);

        if (!BillCovered(option.Ingredients))
        {
            foreach (var ingredient in recipe.Inputs)
            {
                var missing = ingredient.Count - DecisionSystem.CountInventory(
                    npc, ingredient.Id);
                if (missing <= 0) continue;

                var pileObject = FindGroundInputPile(
                    world, npc, ingredient.Id, missing);
                if (pileObject != null)
                {
                    anchorTile = pileObject.Tile;
                    anchorJunction = pileObject.Junctions[0];
                    FillIngredientCounts(world, npc, recipe, anchorTile,
                        includeGround: true, paidProject: null, option.Ingredients);
                }
                break;
            }
        }

        option.WorkTile = anchorTile;
        option.WorkJunction = anchorJunction;
        option.CanCraft = BillCovered(option.Ingredients);
        option.BlockReason = option.CanCraft
            ? CraftBlockReason.None
            : CraftBlockReason.MissingResources;
    }

    private static WorldObjectState FindGroundInputPile(
        WorldState world, NPCState npc, string definitionId, int need)
    {
        if (need <= 0 || npc.CurrentJunction is not { } from) return null;
        WorldObjectState best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (candidate.DefinitionId != definitionId || candidate.IsOccupied ||
                candidate.IsCraftProject || candidate.Contents.Count > 0 ||
                !string.IsNullOrEmpty(candidate.BuildProduct) ||
                !candidate.Fragment.Equals(npc.Fragment) ||
                candidate.Junctions.Count == 0 ||
                CountGroundInputs(world, npc, candidate.Tile, definitionId) < need ||
                !Connectivity.Reachable(
                    world, from, candidate.Junctions[0], npc.Body.CanJump))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(npc.Tile, candidate.Tile);
            if (distance < bestDistance ||
                (distance == bestDistance &&
                 (best == null || candidate.Id.Value < best.Id.Value)))
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static void ResolveAtStation(
        WorldState world,
        NPCState npc,
        Recipe recipe,
        CraftRecipeOption option)
    {
        StationCandidate bestReady = null;
        StationCandidate bestFallback = null;
        foreach (var station in world.Entities.Objects.Values)
        {
            if (!station.Fragment.Equals(npc.Fragment) ||
                !world.Content.ObjectDefinitions.TryGetValue(
                    station.DefinitionId, out var definition) ||
                !definition.HasTag(recipe.Station) ||
                station.Junctions.Count == 0 ||
                !StationReachable(world, npc, station))
            {
                continue;
            }

            var ingredients = new List<CraftIngredientOption>();
            FillIngredientCounts(world, npc, recipe, station.Tile,
                includeGround: option.Goal != GoalType.CookMeat,
                paidProject: null, ingredients);
            var candidate = new StationCandidate
            {
                Station = station,
                Distance = HexSpatialMath.HexDistance(npc.Tile, station.Tile),
                GateOk = StationGateOk(world, recipe, option.Goal, station),
                Busy = StationBusy(world, npc, station),
                BillCovered = BillCovered(ingredients),
                Ingredients = ingredients
            };

            if (Better(candidate, bestFallback)) bestFallback = candidate;
            if (candidate.GateOk && !candidate.Busy && candidate.BillCovered &&
                Better(candidate, bestReady))
            {
                bestReady = candidate;
            }
        }

        var selected = bestReady ?? bestFallback;
        if (selected == null)
        {
            FillIngredientCounts(world, npc, recipe, npc.Tile,
                includeGround: false, paidProject: null, option.Ingredients);
            option.BlockReason = CraftBlockReason.NoStation;
            return;
        }

        option.StationObjectId = selected.Station.Id;
        option.WorkTile = selected.Station.Tile;
        option.WorkJunction = selected.Station.CraftJunction ??
            (selected.Station.Junctions.Count > 0
                ? selected.Station.Junctions[0]
                : (JunctionId?)null);
        option.Ingredients.Clear();
        option.Ingredients.AddRange(selected.Ingredients);

        if (selected.Busy || !selected.GateOk)
        {
            option.BlockReason = CraftBlockReason.StationBusy;
            return;
        }
        if (!selected.BillCovered)
        {
            option.BlockReason = CraftBlockReason.MissingResources;
            return;
        }

        option.CanCraft = true;
        option.BlockReason = CraftBlockReason.None;
    }

    private static CraftBlockReason ActorBlock(
        WorldState world, NPCState npc, GoalType goal)
    {
        if (!npc.Mind.ManualControl || npc.Faction != Faction.Colony)
        {
            return CraftBlockReason.NotManual;
        }
        if (npc.Health <= 0f || npc.IsUnconscious(world.Tick) ||
            world.Tick < npc.Mind.CryingUntilTick ||
            world.Tick < npc.Mind.PlayDeadUntilTick)
        {
            return CraftBlockReason.Incapacitated;
        }
        if (GoalCatalog.CraftNeedsHands(goal) && !npc.Body.CanUseToolsOrWeapons)
        {
            return CraftBlockReason.MissingHands;
        }
        if (npc.CurrentJunction is null)
        {
            return CraftBlockReason.Unreachable;
        }
        return CraftBlockReason.None;
    }

    private static void FillIngredientCounts(
        WorldState world,
        NPCState npc,
        Recipe recipe,
        TileCoord tile,
        bool includeGround,
        WorldObjectState paidProject,
        List<CraftIngredientOption> into)
    {
        into.Clear();
        foreach (var ingredient in recipe.Inputs)
        {
            var available = DecisionSystem.CountInventory(npc, ingredient.Id);
            if (includeGround)
            {
                available += CountGroundInputs(world, npc, tile, ingredient.Id);
            }
            if (paidProject != null)
            {
                foreach (var paid in paidProject.Contents)
                {
                    if (paid.DefinitionId == ingredient.Id) available++;
                }
            }
            into.Add(new CraftIngredientOption
            {
                DefinitionId = ingredient.Id,
                Available = available,
                Required = ingredient.Count
            });
        }
    }

    internal static int CountGroundInputs(
        WorldState world, NPCState npc, TileCoord tile, string definitionId)
    {
        var count = 0;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == definitionId && !obj.IsOccupied &&
                !obj.IsCraftProject && obj.Contents.Count == 0 &&
                string.IsNullOrEmpty(obj.BuildProduct) &&
                obj.Fragment.Equals(npc.Fragment) &&
                HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
            {
                count++;
            }
        }
        return count;
    }

    private static bool BillCovered(List<CraftIngredientOption> ingredients)
    {
        foreach (var ingredient in ingredients)
        {
            if (ingredient.Available < ingredient.Required) return false;
        }
        return true;
    }

    private static bool StationGateOk(
        WorldState world, Recipe recipe, GoalType goal, WorldObjectState station)
    {
        if (recipe.NeedsLitFire && station.ResourceAmount <= 0f) return false;
        if (recipe.RequiresNoRack && DecisionSystem.RackExists(world)) return false;
        if (goal != GoalType.CookMeat) return true;
        return station.ResourceAmount > 0f &&
            BuildSiteMath.CampfireSpitComplete(station) &&
            BuildSiteMath.HangingMeat(station, ContentIds.MeatRaw) +
            BuildSiteMath.HangingMeat(station, ContentIds.MeatCooked) <
            SimBalance.CampfireSpitCapacity;
    }

    private static bool StationBusy(
        WorldState world, NPCState npc, WorldObjectState station)
    {
        if (station.IsOccupied && station.CurrentUser != npc.Id) return true;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.IsCraftProject && obj.CraftStationObjectId == station.Id)
            {
                return true;
            }
        }
        return false;
    }

    private static bool StationReachable(
        WorldState world, NPCState npc, WorldObjectState station)
    {
        if (npc.CurrentJunction is not { } from) return false;
        if (station.CraftJunction is { } workPoint)
        {
            return Connectivity.Reachable(world, from, workPoint, npc.Body.CanJump);
        }
        return station.Junctions.Count > 0 &&
            Connectivity.ReachableBeside(
                world, from, station.Junctions[0], npc.Body.CanJump, station);
    }

    private static bool Reachable(
        WorldState world,
        NPCState npc,
        WorldObjectState target,
        JunctionId junction)
    {
        if (npc.CurrentJunction is not { } from) return false;
        return target.CraftJunction.HasValue
            ? Connectivity.Reachable(world, from, junction, npc.Body.CanJump)
            : Connectivity.ReachableBeside(
                world, from, junction, npc.Body.CanJump, target);
    }

    private static bool Better(StationCandidate candidate, StationCandidate current) =>
        current == null || candidate.Distance < current.Distance ||
        (candidate.Distance == current.Distance &&
         candidate.Station.Id.Value < current.Station.Id.Value);
}

}
