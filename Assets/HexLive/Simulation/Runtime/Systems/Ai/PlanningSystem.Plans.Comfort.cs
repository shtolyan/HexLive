using System.Linq;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public sealed partial class PlanningSystem
{
    // Spec 35.4: is standing on this tile genuinely cooling? Either the cast-
    // shadow map shades it, or it's a walkable water tile (the shallows). This is
    // the tile TemperatureSystem reads (npc.Tile == junction.Tiles[0] on arrival),
    // so a plan that lands her here actually sheds heat — unlike the old plan that
    // parked her on an approach *neighbour* of the shade object and never cooled.
    private static bool IsCoolingTile(WorldState world, Common.TileCoord tile)
    {
        if (TemperatureSystem.IsShaded(world, tile))
        {
            return true;
        }

        return world.Tiles.Items.TryGetValue(tile, out var t) &&
            t.Flags.HasFlag(TileFlags.Water);
    }

    /// <summary>Exact shared answer for the CoolOff bid and its plan. The old
    /// bid merely saw an object tagged Shade/Water, while the plan needs a free
    /// cooling junction with a physical route; injured seed 1104 therefore
    /// selected and failed CoolOff every 64 ticks.</summary>
    internal static Junction FindCoolingJunction(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return null;
        }

        const int candidateBudget = 12;
        var candidates = world.Caches.CoolingCandidatesScratch;
        candidates.Clear();
        var siteJunctions = CollectBuildSiteJunctions(world);
        var maxDistance = HexSpatialMath.HexRadius * 12f;
        foreach (var junction in world.Junctions.Items.Values)
        {
            var isCurrent = junction.Id.Equals(from);
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                (!isCurrent && !SpatialQueries.IsJunctionFree(world, junction.Id)) ||
                siteJunctions.Contains(junction.Id) ||
                !IsCoolingTile(world, junction.Tiles[0]))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance >= maxDistance)
            {
                continue;
            }

            var insert = 0;
            while (insert < candidates.Count && candidates[insert].Distance <= distance)
            {
                insert++;
            }
            if (insert >= candidateBudget)
            {
                continue;
            }
            candidates.Insert(insert, (junction.Id, distance));
            if (candidates.Count > candidateBudget)
            {
                candidates.RemoveAt(candidates.Count - 1);
            }
        }

        var occupied = PathfindingSystem.OtherActorJunctions(world, npc);
        foreach (var candidate in candidates)
        {
            var route = HexPathfinder.FindPath(
                world, from, candidate.Junction, occupied,
                weightClimb: true,
                canJump: CanUseRoutineTraversal(npc),
                danger: null, dangerCost: 0L,
                hardAvoid: DoorTopology.ForbiddenFor(world, npc.Faction),
                maxExpansions: 2000);
            if (route.Count > 0)
            {
                return world.Junctions.Items[candidate.Junction];
            }
        }

        return null;
    }

    // Spec 35.4: walk to the nearest genuinely cool tile (real shade or the
    // shallows) and DWELL there until cooled. Mirrors BuildGroundSitPlan — a
    // move + an in-place GroundCool step — rather than the old move-only trip
    // that completed on arrival and re-won None→CoolOff every tick.
    private void BuildCoolOffPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.CoolOff);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=CoolOff NoJunction");

            }
            return;
        }

        // Nearest free cooling tile with the same exact route Decision used.
        // §54.12: FindCoolingJunction also excludes build-site junctions.
        var best = FindCoolingJunction(world, npc);

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96))
        {
            // Decision and Planning are separate passes. Two overheated actors
            // may both see the last free shade, then the earlier planner owns
            // it. That is resource contention, not a broken route: reporting
            // PlanFailed every generic 40-tick cooldown produced twenty false
            // failures in seed 632. Defer for the longest possible dwell so
            // the waiter checks again when the occupant can actually leave.
            var retryTicks = System.Math.Max(
                SimBalance.CoolOffSettleTicks,
                Spec49.CoolOffDwellTicks * (Spec49.CoolOffMaxRearms + 1));
            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Mind.CurrentGoal = GoalType.None;
            SetGoalCooldown(world, npc, GoalType.CoolOff, retryTicks);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "CoolOffDeferred",
                    $"No free cooling tile; retry after tick {world.Tick + retryTicks}");

            }
            return;
        }

        npc.Plan.TargetJunctionId = best.Id;
        npc.Plan.TargetTile = best.Tiles[0];
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = best.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundCool, TargetJunction = best.Id });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CoolRearmCount = 0;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CoolOffPlanned",
                $"Junction={best.Id.Value} Tile={Trace.FormatTile(npc.Plan.TargetTile)} " +
                $"Shade={TemperatureSystem.IsShaded(world, best.Tiles[0])}");
        }
    }

    // §121.9: internal — ручной приказ ставит тот же план тем же билдером.
    internal void BuildBathePlan(WorldState world, NPCState npc)
    {
        npc.Mind.RedressGarments.RemoveAll(id => !world.Entities.Objects.ContainsKey(id));
        if (npc.Mind.PersonalCarePhase == PersonalCarePhase.None &&
            npc.Mind.RedressGarments.Count > 0)
        {
            // Compatibility with v15-v47 and hand-built fixtures: a remembered
            // pile used to mean only one thing — final redress.
            npc.Mind.PersonalCarePhase = PersonalCarePhase.Redress;
        }

        // #147: resume the exact persistent phase. Laundry returns to the pile
        // and finishes ONE batch beat; Bathing returns to the selected shore;
        // Redress uses the old same-clothes return path.
        if (npc.Mind.PersonalCarePhase == PersonalCarePhase.Redress &&
            npc.Mind.RedressShore is { } redressShore)
        {
            if (npc.Mind.RedressGarments.Count > 0)
            {
                npc.Plan.TargetObjectId = null;
                npc.Plan.TargetJunctionId = redressShore;
                npc.Plan.TargetTile = null;
                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = redressShore });
                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.RedressAfterBathe, TargetJunction = redressShore });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PostBatheRedress",
                        $"Resume: returning to {redressShore.Value} for {npc.Mind.RedressGarments.Count} garments");
                }
                return;
            }

            npc.Mind.RedressShore = null;
            npc.Mind.PersonalCareBathShore = null;
            npc.Mind.PersonalCarePhase = PersonalCarePhase.None;
        }
        else if (npc.Mind.PersonalCarePhase is PersonalCarePhase.LaundryBatch or
                 PersonalCarePhase.Bathing)
        {
            var stillDoffing = npc.WornItems.Any(item =>
                !HolsterCatalog.IsHolster(item.DefinitionId));
            var resume = npc.Mind.PersonalCarePhase == PersonalCarePhase.LaundryBatch || stillDoffing
                ? npc.Mind.RedressShore
                : npc.Mind.PersonalCareBathShore;
            if (resume is { } resumeJunction &&
                world.Junctions.Items.ContainsKey(resumeJunction) &&
                SpatialMutations.TryReserveJunction(world, resumeJunction, npc.Id, world.Tick, 96))
            {
                npc.Plan.TargetObjectId = null;
                npc.Plan.TargetJunctionId = resumeJunction;
                npc.Plan.TargetTile = world.Junctions.Items[resumeJunction].Tiles.Count > 0
                    ? world.Junctions.Items[resumeJunction].Tiles[0]
                    : null;
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = resumeJunction
                });
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.PrepareBathe,
                    TargetJunction = resumeJunction,
                    TimeoutEndTick = npc.Mind.PersonalCareBathShore?.Value
                });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                return;
            }

            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            return;
        }

        // §40.6: a fresh bathe starts a fresh doffed-clothes pile — drop any
        // stale ids from an earlier interrupted bathe so the redress that
        // follows only reclaims the clothes she takes off THIS time.
        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressShore = null;
        npc.Mind.PersonalCareBathShore = null;
        npc.Mind.PersonalCarePhase = PersonalCarePhase.None;

        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            return;
        }

        var best = HygieneMath.FindReachableBathShore(world, npc);

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Bathe NoWaterTile");

            }
            return;
        }

        // §133: раздеваться она идёт ДОМОЙ — к гардеробу, к сушилке или хотя бы
        // на домашний тайл, — и только оттуда в воду. Прежнее поведение (раздеться
        // у самой воды) остаётся запасным: без дома или без пути от дома к воде.
        var undressStand = best.Id;
        ObjectId? stowObject = null;
        var laundryRequired = npc.WornItems.Any(item =>
            !HolsterCatalog.IsHolster(item.DefinitionId) &&
            MathUtil.Clamp01(item.Dirtiness + item.Bloodiness) >=
                SimBalance.WashClothesNeedThreshold);
        if (!laundryRequired && StowMath.FindUndressSpot(world, npc) is { } spot &&
            HygieneMath.HasBathApproachRoute(world, npc, from, spot.Stand) &&
            HygieneMath.HasBathApproachRoute(world, npc, spot.Stand, best.Id) &&
            // ⭐ ...НО ТОЛЬКО ЕСЛИ ДОМ РЯДОМ С ВОДОЙ. Здесь стояла одна лишь
            // достижимость, и «дом» подходил любой, хоть через весь остров.
            // Получалось: разделась догола у гардероба, пошла к воде за 181
            // узел, на четвёртом шаге её перебила жажда, вернулась, ОДЕЛАСЬ
            // обратно — и всё сначала. Замер (seed 476005489, тик 5617): четыре
            // круга «разделась, плыву» подряд, в воду не вошла ни разу.
            // Порог тот же, которым выше отбирался сам берег (12 радиусов от
            // неё): дальше этого голый переход через остров — не купание, а
            // петля, и раздеваться тогда надо у воды (ветка ниже, она же
            // запасная по §133).
            HexSpatialMath.Distance(
                world.Junctions.Items[spot.Stand].WorldPosition,
                best.WorldPosition) < HexSpatialMath.HexRadius * 12f)
        {
            undressStand = spot.Stand;
            stowObject = spot.StowObject;
        }

        if (!undressStand.Equals(best.Id))
        {
            // Резервируем ту точку, где она реально будет стоять и раздеваться;
            // берег доедет своим ходом, когда она разденется.
            SpatialMutations.ReleaseJunctionReservation(world, best.Id, npc.Id);
            if (!SpatialMutations.TryReserveJunction(world, undressStand, npc.Id, world.Tick, 96))
            {
                undressStand = best.Id;
                stowObject = null;
                SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96);
            }
        }

        npc.Plan.TargetObjectId = stowObject;
        npc.Mind.RedressShore = undressStand;
        npc.Mind.PersonalCareBathShore = best.Id;
        npc.Mind.PersonalCarePhase = laundryRequired
            ? PersonalCarePhase.LaundryBatch
            : PersonalCarePhase.Bathing;
        // Pathfinding consumes Plan.TargetJunctionId, so while the first leg
        // goes home it must remain the home work point. Preserve the ACTUAL
        // shore on PrepareBathe.TimeoutEndTick (an integer payload, as used by
        // the player-inventory steps) until undressing is complete. Previously
        // the shore was forgotten and execution searched for water beside the
        // wardrobe; storing it directly in Plan.TargetJunctionId instead made
        // the inverse bug — pathfinding skipped the home leg and stood forever.
        npc.Plan.TargetJunctionId = undressStand;
        npc.Plan.TargetTile = world.Junctions.Items[undressStand].Tiles.Count > 0
            ? world.Junctions.Items[undressStand].Tiles[0]
            : best.Tiles[0];
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = undressStand });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PrepareBathe,
            TargetJunction = undressStand,
            TargetObject = stowObject,
            TimeoutEndTick = best.Id.Value
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CoolRearmCount = 0;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "BathePlanned",
                $"Junction={undressStand.Value} Water={best.Id.Value} " +
                $"Stow={(stowObject is { } s ? s.Value.ToString() : "ground")} " +
                $"BodyHygiene={npc.Needs.Hygiene:F2} " +
                $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
        }
    }

    // §121.9: internal — ручной приказ ставит тот же план тем же билдером.
    internal void BuildWashClothesPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.WashClothes);
            return;
        }

        WorldObjectState best = null;
        JunctionId bestTarget = default;
        TileCoord bestStandTile = default;
        var bestDistance = float.MaxValue;
        foreach (var obj in world.Entities.Objects.Values)
        {
            var objContamination = MathUtil.Clamp01(obj.Dirtiness + obj.Bloodiness);
            if (objContamination < SimBalance.WashClothesNeedThreshold ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Layer is null || !HygieneMath.IsBathingTile(world, obj.Tile) ||
                obj.Junctions.Count == 0)
            {
                continue;
            }

            var objectPosition = world.Junctions.Items.TryGetValue(obj.Junctions[0], out var objectJunction)
                ? objectJunction.WorldPosition
                : HexSpatialMath.TileToWorld(obj.Tile);
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (!TryGetEdgeSeatGeometry(world, junction, waterOnly: true,
                        out var standTile, out _) ||
                    !JunctionAvailableFor(world, junction.Id, npc.Id) ||
                    !Connectivity.Reachable(world, from, junction.Id, CanUseRoutineTraversal(npc)))
                {
                    continue;
                }

                var garmentDistance = HexSpatialMath.Distance(junction.WorldPosition, objectPosition);
                if (garmentDistance > HexSpatialMath.HexRadius * 2.2f)
                {
                    continue;
                }

                // §40.6 r4: the true walk — to the pile first, then carrying
                // it to the edge.
                var distance = HexSpatialMath.Distance(npc.Position, objectPosition) +
                               garmentDistance;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = obj;
                    bestTarget = junction.Id;
                    bestStandTile = standTile;
                }
            }
        }

        if (best is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.WashClothes);
            return;
        }

        // §40.6 r4: the pile is fetched for REAL — leg 1 stands beside the
        // garment like any ground pick-up (gather-beside rim, one sub-grid
        // step), leg 2 carries it to the reserved edge. Without the leg the
        // wash beat teleported the pile into the hand from up to 2.2R away
        // (the "acts a hex away" family, spec §26.6A).
        JunctionId? beside = null;
        var besideReach = SpatialQueries.BesideReach(
            world.Content.ObjectDefinitions.TryGetValue(best.DefinitionId, out var besideDef)
                ? besideDef.ObstacleRadius : 0f);
        var rimScratch = world.Caches.ObjectApproachJunctionsScratch;
        SpatialQueries.CollectStandableAround(world, best.Junctions[0], rimScratch, 96, besideReach, best,
            InteractionReach.RimMode);
        rimScratch.Sort((a, b) =>
        {
            var da = world.Junctions.Items.TryGetValue(a, out var ja)
                ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
            var db = world.Junctions.Items.TryGetValue(b, out var jb)
                ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
            return da.CompareTo(db);
        });
        foreach (var rim in rimScratch)
        {
            if (Connectivity.Reachable(world, from, rim, CanUseRoutineTraversal(npc)) &&
                SpatialQueries.IsJunctionFree(world, rim) &&
                SpatialMutations.TryReserveJunction(world, rim, npc.Id, world.Tick, 48))
            {
                beside = rim;
                break;
            }
        }

        if (beside is not { } fetchStand ||
            !SpatialMutations.TryReserveJunction(world, bestTarget, npc.Id, world.Tick,
                SimBalance.WashClothesDurationTicks + 96))
        {
            if (beside is { } reserved)
            {
                SpatialMutations.ReleaseJunctionReservation(world, reserved, npc.Id);
            }

            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.WashClothes);
            return;
        }

        npc.Plan.TargetObjectId = best.Id;
        npc.Plan.TargetJunctionId = fetchStand; // leg 1: to the pile first
        npc.Plan.TargetTile = bestStandTile;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = fetchStand });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.WashClothes,
            TargetJunction = bestTarget,
            TargetObject = best.Id,
            Interaction = InteractionType.WashClothes
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "WashClothesPlanned",
                $"Object={best.Id.Value} Def={best.DefinitionId} Dirt={best.Dirtiness:F2} " +
                $"FetchVia={fetchStand.Value} Edge={bestTarget.Value} " +
                $"StandTile={bestStandTile.Q},{bestStandTile.R}");
        }
    }

    // Spec 29G: does perception offer real furniture for this interaction?
    private static bool HasFurnitureCandidate(WorldState world, NPCState npc, InteractionType interaction)
    {
        var goal = interaction == InteractionType.Sleep
            ? GoalType.Sleep
            : GoalType.Sit;
        return HasObjectCandidateForGoal(world, npc, goal);
    }

    /// <summary>Exact shared sleep availability: either a usable bed or the
    /// same full-body ground placement BuildGroundSleepPlan will reserve.</summary>
    internal static bool HasSleepSurface(WorldState world, NPCState npc) =>
        HasFurnitureCandidate(world, npc, InteractionType.Sleep) ||
        FindGroundSleepSpot(world, npc, out _) is not null;

    // §54.12: junctions occupied by a build-site (the half-built bed by the
    // fire). Never chosen as a ground-sit / cool-off spot — she'd plop down
    // ON the growing mat she just stocked.
    private static readonly System.Collections.Generic.HashSet<JunctionId> _siteJunctionsScratch = new();

    private static System.Collections.Generic.HashSet<JunctionId> CollectBuildSiteJunctions(WorldState world)
    {
        _siteJunctionsScratch.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!BuildSiteMath.IsSite(obj))
            {
                continue;
            }

            foreach (var junction in obj.Junctions)
            {
                _siteJunctionsScratch.Add(junction);
            }
        }

        return _siteJunctionsScratch;
    }

    /// <summary>Shared decision/planner gate: an unfinished furniture footprint
    /// is occupied space, not a scenic ledge seat.</summary>
    internal static bool IsBuildSiteJunction(WorldState world, JunctionId junctionId)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (BuildSiteMath.IsSite(obj) && obj.Junctions.Contains(junctionId))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 29G: sit on the land — a ledge with the legs over the edge when
    // one is close, any free junction otherwise.
    // §121.9: internal — ручной приказ ставит тот же план тем же билдером.
    internal void BuildGroundSitPlan(WorldState world, NPCState npc)
    {
        JunctionId? spot = null;
        var siteJunctions = CollectBuildSiteJunctions(world);

        // Prefer a scenic ledge within ~4 tiles.
        if (npc.CurrentJunction is { } from)
        {
            var bestDist = float.MaxValue;
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (junction.Blocked || junction.Tiles.Count < 2 ||
                    !TryGetEdgeSeatGeometry(world, junction, waterOnly: false, out _, out _) ||
                    !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                    siteJunctions.Contains(junction.Id))
                {
                    continue;
                }

                var d = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
                // Only a ledge she's basically beside — walk up to it, don't trek
                // across the island to a scenic edge (user: sit close, not afar).
                if (d < bestDist && d < HexSpatialMath.HexRadius * 2f &&
                    Connectivity.Reachable(world, from, junction.Id, CanUseRoutineTraversal(npc)))
                {
                    bestDist = d;
                    spot = junction.Id;
                }
            }
        }

        // NO flat-ground fallback: sitting happens ONLY on a real seat — a
        // ledge (the hex edge working as a step, legs over the drop), a stump
        // or Sit-furniture (chair/bed, handled by the furniture path before
        // this). The old §29G "sit right where she stands" plopped her onto
        // flat land — and onto the half-built bed she'd just stocked (§54.12).
        if (spot is not { } sitSpot ||
            !SpatialMutations.TryReserveJunction(world, sitSpot, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Sit);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Sit NoLedgeOrSeat");

            }
            return;
        }

        npc.Plan.TargetJunctionId = sitSpot;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = sitSpot });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundSit, TargetJunction = sitSpot });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GroundSitPlanned",
                $"Junction={sitSpot.Value} Ledge={IsLedgeId(world, sitSpot)}");
        }
    }

    // §137: аукцион не нашёл дела. План на «ничего» — сесть на землю ТАМ, ГДЕ
    // СТОИШЬ: ни шага, ни цели, ни объекта. Тем он и отличается от посиделок
    // §29G (GoalType.Sit), которые ищут настоящее сиденье — уступ или мебель —
    // и ради него идут; сюда же попадает та, кому идти некуда и незачем.
    //
    // Не сложилось — план просто Completed, ровно как раньше у Idle
    // (PlanNoInteraction). Кулдаун на Idle не вешается: SetGoalCooldown его
    // намеренно не берёт, а обнулять ставку запасной цели нельзя — без неё
    // аукцион остаётся вовсе без победителя.
    private void BuildIdleRestPlan(WorldState world, NPCState npc)
    {
        if (!IdleRestMath.CanStart(world, npc))
        {
            npc.Plan.Status = PlanStatus.Completed;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanNoInteraction",
                    $"Goal=Idle rest unavailable (cooldown until {npc.Mind.RestCooldownUntilTick})");
            }
            return;
        }

        npc.Mind.RestRearmCount = 0;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.IdleRest,
            Interaction = InteractionType.Rest,
            TargetJunction = npc.CurrentJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "IdleRestPlanned",
                $"Tile={npc.Tile.Q},{npc.Tile.R} Block={Spec137.RestBlockTicks}ticks");
        }
    }

    // Spec 29G: lie at the center of a free hexagon — walkable, dry, no
    // objects, nobody else lying there. The spot is anchored to HOME (the
    // campfire), not to wherever the night caught the NPC: the first soak
    // with self-anchored sleep had the colony bedding down in dog country
    // and getting eaten (fights=215, 5 deaths).
    private static JunctionId? FindGroundSleepSpot(
        WorldState world, NPCState npc, out TileCoord anchor)
    {
        anchor = npc.Tile;
        WorldObjectState hearth = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId != ContentIds.Campfire ||
                !ColonyQueries.InCamp(world, obj.Tile, npc.Faction))
            {
                continue;
            }

            if (hearth is null || obj.Id.Value < hearth.Id.Value)
            {
                hearth = obj;
            }
        }

        // Old/custom worlds may have no faction-home metadata. Preserve their
        // existing fallback to any ordinary campfire rather than sleeping at
        // the work site.
        if (hearth is null)
        {
            foreach (var obj in world.Entities.Objects.Values)
            {
                if (obj.DefinitionId == ContentIds.Campfire &&
                    (hearth is null || obj.Id.Value < hearth.Id.Value))
                {
                    hearth = obj;
                }
            }
        }

        if (hearth is not null)
        {
            anchor = hearth.Tile;
        }

        JunctionId? spot = null;
        var bestDist = float.MaxValue;
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (!tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Water) ||
                tile.Junctions.Count == 0)
            {
                continue;
            }

            var d = (float)HexSpatialMath.HexDistance(tile.Coord, anchor);
            if (d > 6f)
            {
                continue;
            }

            // A roof beats proximity: indoor sleepers are sanctuary-safe
            // (spec 29C.4A) — outdoor night camps got mauled by dogs.
            if (tile.Flags.HasFlag(TileFlags.Indoor))
            {
                d -= 100f;
            }

            // Spec §49 (Tier C): comfort nudge — a hot day pulls toward shade, a
            // cold night toward the fireside. SMALL vs the distance range (0..6)
            // and dwarfed by the indoor -100, so it only re-orders nearby spots,
            // never sends her into danger. (Fireside already correlates with
            // "near the anchor", so this mostly adds daytime shade-seeking.)
            if (Spec49.SmartSleepSpot)
            {
                var cold = world.Environment.GlobalTemperature < 16f ||
                    world.Environment.Phase is DayPhase.Night or DayPhase.Evening;
                var hot = world.Environment.GlobalTemperature > 24f &&
                    world.Environment.Phase is DayPhase.Day or DayPhase.Morning;
                if (cold && TemperatureSystem.NearbyFireWarmth(world, tile.Coord, out _) > 0f)
                {
                    d -= Spec49.SleepSpotFireWeight;
                }
                if (hot && TemperatureSystem.IsShaded(world, tile.Coord))
                {
                    d -= Spec49.SleepSpotShadeWeight;
                }
            }

            if (d >= bestDist)
            {
                continue;
            }

            // Ask the SAME 37-node full-body solver that will place the body
            // after arrival. The former centre-only + "tile has no objects"
            // approximation rejected furnished rooms and harmless loose items,
            // even when a legal rectangle existed beside them.
            if (LyingSpot.TrySolveOnTile(world, npc, tile.Coord, out var placement) &&
                SpatialQueries.IsJunctionFree(world, placement.Node) &&
                npc.CurrentJunction is { } from2 &&
                Connectivity.Reachable(
                    world, from2, placement.Node, CanUseRoutineTraversal(npc)))
            {
                bestDist = d;
                spot = placement.Node;
            }
        }

        return spot;
    }

    // §121.9: internal — ручной приказ ставит тот же план тем же билдером.
    internal void BuildGroundSleepPlan(WorldState world, NPCState npc)
    {
        var spot = FindGroundSleepSpot(world, npc, out var anchor);
        if (spot is not { } lieSpot ||
            !SpatialMutations.TryReserveJunction(world, lieSpot, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Sleep);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Sleep NoGroundSpot");

            }
            return;
        }

        npc.Plan.TargetJunctionId = lieSpot;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = lieSpot });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundSleep, TargetJunction = lieSpot });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "GroundSleepPlanned",
                $"Junction={lieSpot.Value} Hearth={anchor}");
        }
    }

    // Spec 35.5: the rack half is the exact generic-planner predicate, not a
    // loose tag lookup. In seed 31337 the old helper saw a non-full rack and
    // entered the generic planner, which then rejected the same rack because
    // its Hang offer/claim/approach was unavailable (Candidates=0, repeatedly).
    private static bool HasFreeRackCandidate(WorldState world, NPCState npc)
    {
        return HasObjectCandidateForGoal(world, npc, GoalType.DryClothes);
    }

    internal static bool HasDryingDestination(WorldState world, NPCState npc)
    {
        return HasFreeRackCandidate(world, npc) ||
            TryFindFireDryApproach(world, npc, out _, out _);
    }

    // Drying is peacetime work; a route which needs to expand more than this
    // is not worth blocking the simulation for. Typical camp trips are in the
    // low hundreds, while a missing/forbidden door used to fan across the
    // whole island on every re-selection.
    private const int DryingRouteExpansionBudget = 1500;

    private static bool HasExactFireDryRoute(
        WorldState world,
        NPCState npc,
        JunctionId destination,
        System.Collections.Generic.HashSet<JunctionId> occupiedByActor)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return false;
        }

        if (from.Equals(destination))
        {
            return true;
        }

        var route = HexPathfinder.FindPath(
            world, from, destination, occupiedByActor,
            weightClimb: PathfindingSystem.ShouldWeightClimbs(npc),
            canJump: CanUseRoutineTraversal(npc),
            danger: PathfindingSystem.RouteAvoidRing(world, npc),
            dangerCost: TraitMath.DangerStepCost(npc),
            hardAvoid: DoorTopology.ForbiddenFor(world, npc.Faction),
            maxExpansions: DryingRouteExpansionBudget);
        return route.Count > 0;
    }

    private static bool TryFindFireDryApproach(
        WorldState world,
        NPCState npc,
        out PerceivedObject fire,
        out JunctionId approach)
    {
        fire = null;
        approach = default;
        var bestDistance = float.MaxValue;
        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, npc);
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable || perceived.Distance >= bestDistance ||
                perceived.DefinitionId != ContentIds.Campfire ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var campfire) ||
                campfire.ResourceAmount <= 0f || campfire.Junctions.Count == 0)
            {
                continue;
            }

            var anchor = campfire.Junctions[0];
            foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, anchor))
            {
                var isCurrent = npc.CurrentJunction is { } current && current.Equals(neighbor);
                if (occupiedByActor.Contains(neighbor) ||
                    (!isCurrent && !CanUseApproachJunction(world, npc, neighbor)) ||
                    (world.Reservations.Junctions.TryGetValue(neighbor, out var reservation) &&
                     reservation.Owner != npc.Id && reservation.EndTick >= world.Tick) ||
                    !HasExactFireDryRoute(
                        world, npc, neighbor, occupiedByActor))
                {
                    continue;
                }

                fire = perceived;
                approach = neighbor;
                bestDistance = perceived.Distance;
                break;
            }
        }

        return fire is not null;
    }

    // Spec 35.5: a move-only trip to the lit campfire — standing within a
    // tile dries the whole outfit at x4 (MoistureSystem does the rest).
    private void BuildFireDryPlan(WorldState world, NPCState npc)
    {
        if (!TryFindFireDryApproach(world, npc, out var fire, out var approach) ||
            !SpatialMutations.TryReserveJunction(
                world, approach, npc.Id, world.Tick, 48))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.DryClothes);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    "Goal=DryClothes NoReachableDryingDestination");

            }
            return;
        }

        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = fire.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "FireDryPlanned",
                $"To campfire Tile={fire.Tile.Q},{fire.Tile.R}");
        }
    }
}

}
