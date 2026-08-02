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
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=CoolOff NoJunction");
            return;
        }

        // Nearest reachable, free junction whose standing tile actually cools.
        // §54.12: never dwell ON a build-site (the half-built fireside bed).
        var siteJunctions = CollectBuildSiteJunctions(world);
        Junction best = null;
        var bestDist = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                siteJunctions.Contains(junction.Id) ||
                !IsCoolingTile(world, junction.Tiles[0]))
            {
                continue;
            }

            var d = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (d < bestDist && d < HexSpatialMath.HexRadius * 12f &&
                Connectivity.Reachable(world, from, junction.Id))
            {
                bestDist = d;
                best = junction;
            }
        }

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.CoolOff);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=CoolOff NoCoolTile");
            return;
        }

        npc.Plan.TargetJunctionId = best.Id;
        npc.Plan.TargetTile = best.Tiles[0];
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = best.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundCool, TargetJunction = best.Id });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CoolRearmCount = 0;
        Trace.Emit(world, npc.Id, "CoolOffPlanned",
            $"Junction={best.Id.Value} Tile={Trace.FormatTile(npc.Plan.TargetTile)} " +
            $"Shade={TemperatureSystem.IsShaded(world, best.Tiles[0])}");
    }

    private void BuildBathePlan(WorldState world, NPCState npc)
    {
        // §40.6: an interrupted post-bathe redress RESUMES here — if she still
        // owes clothes to the shore pile, walk back and put them on rather than
        // starting a brand-new bathe (which would clear the pile memory and
        // leave her naked). Stale pieces (taken/gone) are dropped first.
        if (npc.Mind.RedressGarments.Count > 0 && npc.Mind.RedressShore is { } redressShore)
        {
            npc.Mind.RedressGarments.RemoveAll(id => !world.Entities.Objects.ContainsKey(id));
            if (npc.Mind.RedressGarments.Count > 0)
            {
                npc.Plan.TargetObjectId = null;
                npc.Plan.TargetJunctionId = redressShore;
                npc.Plan.TargetTile = null;
                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = redressShore });
                npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.RedressAfterBathe, TargetJunction = redressShore });
                npc.Plan.CurrentStepIndex = 0;
                npc.Plan.Status = PlanStatus.Active;
                Trace.Emit(world, npc.Id, "PostBatheRedress",
                    $"Resume: returning to {redressShore.Value} for {npc.Mind.RedressGarments.Count} garments");
                return;
            }

            npc.Mind.RedressShore = null;
        }

        // §40.6: a fresh bathe starts a fresh doffed-clothes pile — drop any
        // stale ids from an earlier interrupted bathe so the redress that
        // follows only reclaims the clothes she takes off THIS time.
        npc.Mind.RedressGarments.Clear();
        npc.Mind.RedressShore = null;

        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            return;
        }

        Junction best = null;
        var bestDist = float.MaxValue;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                !SpatialQueries.IsJunctionFree(world, junction.Id) ||
                !HygieneMath.IsShoreTile(world, junction.Tiles[0]) ||
                !Connectivity.Reachable(world, from, junction.Id))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDist && distance < HexSpatialMath.HexRadius * 12f)
            {
                bestDist = distance;
                best = junction;
            }
        }

        if (best is null ||
            !SpatialMutations.TryReserveJunction(world, best.Id, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Bathe);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Bathe NoWaterTile");
            return;
        }

        npc.Plan.TargetJunctionId = best.Id;
        npc.Plan.TargetTile = best.Tiles[0];
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = best.Id });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.PrepareBathe, TargetJunction = best.Id });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CoolRearmCount = 0;
        Trace.Emit(world, npc.Id, "BathePlanned",
            $"Junction={best.Id.Value} BodyHygiene={npc.Needs.Hygiene:F2} " +
            $"ClothingDirt={EquipmentMath.AverageDirtiness(npc):F2}");
    }

    private void BuildWashClothesPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.WashClothes);
            return;
        }

        // §40.6 r2 (laundry-in-hand): the dirtiest WORN piece competes with the
        // beached pile — whichever is filthier gets washed. A worn winner means
        // she carries it on her body to the edge, doffs it into her hand there
        // and scrubs; a ground winner is picked up off the shore into the hand.
        string wornCandidate = null;
        var wornContamination = 0f;
        foreach (var item in npc.WornItems)
        {
            var contamination = MathUtil.Clamp01(item.Dirtiness + item.Bloodiness);
            if (contamination >= SimBalance.WashClothesNeedThreshold &&
                contamination > wornContamination)
            {
                wornContamination = contamination;
                wornCandidate = item.DefinitionId;
            }
        }

        WorldObjectState best = null;
        var bestContamination = 0f;
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
                    !Connectivity.Reachable(world, from, junction.Id))
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
                    bestContamination = objContamination;
                    bestTarget = junction.Id;
                    bestStandTile = standTile;
                }
            }
        }

        // The worn piece wins ties — off-the-body washing is the primary path.
        if (wornCandidate is not null && wornContamination >= bestContamination)
        {
            best = null;
            bestDistance = float.MaxValue;
            foreach (var junction in world.Junctions.Items.Values)
            {
                if (!TryGetEdgeSeatGeometry(world, junction, waterOnly: true,
                        out var standTile, out _) ||
                    !JunctionAvailableFor(world, junction.Id, npc.Id) ||
                    !Connectivity.Reachable(world, from, junction.Id))
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(npc.Position, junction.WorldPosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = junction.Id;
                    bestStandTile = standTile;
                }
            }

            if (bestDistance == float.MaxValue ||
                !SpatialMutations.TryReserveJunction(world, bestTarget, npc.Id, world.Tick,
                    SimBalance.WashClothesDurationTicks + 96))
            {
                npc.Plan.Status = PlanStatus.Failed;
                SetGoalCooldown(world, npc, GoalType.WashClothes);
                return;
            }

            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetItemDefinitionId = wornCandidate;
            npc.Plan.TargetJunctionId = bestTarget;
            npc.Plan.TargetTile = bestStandTile;
            npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = bestTarget });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.WashClothes,
                TargetJunction = bestTarget,
                Interaction = InteractionType.WashClothes
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            Trace.Emit(world, npc.Id, "WashClothesPlanned",
                $"Worn={wornCandidate} Dirt={wornContamination:F2} " +
                $"Edge={bestTarget.Value} StandTile={bestStandTile.Q},{bestStandTile.R}");
            return;
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
        SpatialQueries.CollectStandableAround(world, best.Junctions[0], _rimScratch, 96, besideReach);
        _rimScratch.Sort((a, b) =>
        {
            var da = world.Junctions.Items.TryGetValue(a, out var ja)
                ? HexSpatialMath.Distance(ja.WorldPosition, npc.Position) : float.MaxValue;
            var db = world.Junctions.Items.TryGetValue(b, out var jb)
                ? HexSpatialMath.Distance(jb.WorldPosition, npc.Position) : float.MaxValue;
            return da.CompareTo(db);
        });
        foreach (var rim in _rimScratch)
        {
            if (Connectivity.Reachable(world, from, rim) &&
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
        Trace.Emit(world, npc.Id, "WashClothesPlanned",
            $"Object={best.Id.Value} Def={best.DefinitionId} Dirt={best.Dirtiness:F2} " +
            $"FetchVia={fetchStand.Value} Edge={bestTarget.Value} " +
            $"StandTile={bestStandTile.Q},{bestStandTile.R}");
    }

    // Spec 29G: does perception offer real furniture for this interaction?
    private static bool HasFurnitureCandidate(WorldState world, NPCState npc, InteractionType interaction)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable && !perceived.IsOccupied &&
                perceived.AvailableInteractions.Contains(interaction))
            {
                return true;
            }
        }

        return false;
    }

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

    // Spec 29G: sit on the land — a ledge with the legs over the edge when
    // one is close, any free junction otherwise.
    private void BuildGroundSitPlan(WorldState world, NPCState npc)
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
                    Connectivity.Reachable(world, from, junction.Id))
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
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Sit NoLedgeOrSeat");
            return;
        }

        npc.Plan.TargetJunctionId = sitSpot;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = sitSpot });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundSit, TargetJunction = sitSpot });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "GroundSitPlanned",
            $"Junction={sitSpot.Value} Ledge={IsLedgeId(world, sitSpot)}");
    }

    // Spec 29G: lie at the center of a free hexagon — walkable, dry, no
    // objects, nobody else lying there. The spot is anchored to HOME (the
    // campfire), not to wherever the night caught the NPC: the first soak
    // with self-anchored sleep had the colony bedding down in dog country
    // and getting eaten (fights=215, 5 deaths).
    private void BuildGroundSleepPlan(WorldState world, NPCState npc)
    {
        var anchor = npc.Tile;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var objDef) &&
                objDef.Tags.Contains("Campfire"))
            {
                anchor = obj.Tile;
                break;
            }
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

            if (world.Caches.ObjectsByTile.TryGetValue(tile.Coord, out var objects) && objects.Count > 0)
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

            // center junction: nearest to the tile's world center
            var center = HexSpatialMath.TileToWorld(tile.Coord);
            JunctionId? centerJunction = null;
            var centerDist = float.MaxValue;
            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || junction.Blocked)
                {
                    continue;
                }

                var cd = HexSpatialMath.Distance(junction.WorldPosition, center);
                if (cd < centerDist)
                {
                    centerDist = cd;
                    centerJunction = junctionId;
                }
            }

            if (centerJunction is { } cj &&
                world.Junctions.Items.TryGetValue(cj, out var sleepJunction) &&
                SpatialQueries.IsJunctionFree(world, cj) &&
                SpatialQueries.LyingBodyClear(world, sleepJunction) &&
                npc.CurrentJunction is { } from2 && Connectivity.Reachable(world, from2, cj))
            {
                bestDist = d;
                spot = cj;
            }
        }

        if (spot is not { } lieSpot ||
            !SpatialMutations.TryReserveJunction(world, lieSpot, npc.Id, world.Tick, 96))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Sleep);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Sleep NoGroundSpot");
            return;
        }

        npc.Plan.TargetJunctionId = lieSpot;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.MoveToJunction, TargetJunction = lieSpot });
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.GroundSleep, TargetJunction = lieSpot });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "GroundSleepPlanned", $"Junction={lieSpot.Value}");
    }

    // Spec 35.5: is a free drying rack within reach?
    private static bool HasFreeRackCandidate(WorldState world, NPCState npc)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable &&
                world.Content.ObjectDefinitions.TryGetValue(perceived.DefinitionId, out var definition) &&
                definition.Tags.Contains("Rack") &&
                world.Entities.Objects.TryGetValue(perceived.Id, out var rack) &&
                !ExecutionSystem.RackIsFull(world, rack))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 35.5: a move-only trip to the lit campfire — standing within a
    // tile dries the whole outfit at x4 (MoistureSystem does the rest).
    private void BuildFireDryPlan(WorldState world, NPCState npc)
    {
        PerceivedObject? fire = null;
        foreach (var perceived in npc.Perception.Objects)
        {
            if (perceived.IsReachable &&
                perceived.DefinitionId == ContentIds.Campfire &&
                world.Entities.Objects.TryGetValue(perceived.Id, out var campfire) &&
                campfire.ResourceAmount > 0f &&
                (fire is null || perceived.Distance < fire.Distance))
            {
                fire = perceived;
            }
        }

        JunctionId? anchor = null;
        if (fire is not null && world.Entities.Objects.TryGetValue(fire.Id, out var fireObject))
        {
            anchor = fireObject.Junctions.Count > 0 ? fireObject.Junctions[0] : null;
        }

        if (anchor is not { } target)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.DryClothes);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=DryClothes NoLitFire");
            return;
        }

        JunctionId? approach = null;
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, target))
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
            {
                approach = neighbor;
                break;
            }
        }

        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.DryClothes);
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=DryClothes NoFreeApproach");
            return;
        }

        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = fire!.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "FireDryPlanned",
            $"To campfire Tile={fire.Tile.Q},{fire.Tile.R}");
    }

    private static readonly System.Collections.Generic.List<JunctionId> _rimScratch = new();
}

}
