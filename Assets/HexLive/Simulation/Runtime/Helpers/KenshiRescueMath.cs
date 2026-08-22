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

/// <summary>§116 shared rescue predicates, reservations and carry invariants.</summary>
internal static class KenshiRescueMath
{
    // Headless regression probe: a normal destination selection must perform
    // one weighted graph search, not one search per rim/interior junction.
    internal static int DestinationPathSearchesLastCall { get; private set; }

    // Бюджет островных weighted-поисков на ОДИН вызов TryFindDestination.
    // Успешный подбор укладывается в 1-3 поиска; исчерпывающий перебор
    // случается ровно тогда, когда маршрута переноски НЕТ ВООБЩЕ (например,
    // каждый вариант режется правилом уступов), — и тогда все кандидаты
    // лагеря × их джанкшены дают тысячи Дейкстр в одном тике: замерено
    // 23 с и 15 ГБ аллокаций на острове 1x при 10 NPC. После бюджета это
    // тот же честный провал "no safe bed or camp ground route" с тем же
    // кулдауном Rescue, но за миллисекунды.
    private const int DestinationPathSearchBudget = 24;

    private static bool SearchBudgetExhausted =>
        DestinationPathSearchesLastCall >= DestinationPathSearchBudget;

    internal static bool NeedsRescue(WorldState world, NPCState patient) =>
        patient.Health > 0f &&
        !patient.IsBeingCarried &&
        (patient.IsDying || patient.Mind.ComaCause != ComaCause.None ||
         IsStranded(world, patient)) &&
        !IsRecoveryResting(world, patient);

    /// <summary>
    /// §118.4 r2 (баг #166): ЗАСТРЯЛА. В сознании, но ноги её больше не несут, и
    /// своим ходом до дома ей не добраться — с её способностями маршрута туда
    /// просто нет. До этой ветки спасение знало только умирающих и коматозных,
    /// то есть переломанная, но живая колонистка оставалась там, где упала, и
    /// колония смотрела на это молча.
    /// <para>
    /// Мера — связность, а не расстояние: «далеко» она доползёт (§50.9 ровно об
    /// этом), а «нет пути» не лечится временем. Спрашивается по её РЕАЛЬНОЙ
    /// проходимости (аварийная лестница §63), поэтому здоровая, у которой дом
    /// за обрывом, сюда не попадает: у неё маршрут есть.
    /// </para>
    /// </summary>
    internal static bool IsStranded(WorldState world, NPCState patient)
    {
        if (!Spec118.StrandedRescueEnabled ||
            patient.IsUnconscious(world.Tick) ||
            !patient.Body.IsCrawling ||
            patient.CurrentJunction is not { } from)
        {
            return false;
        }

        var home = ColonyQueries.Home(world, patient.Faction);
        if (home is not { } homeTile ||
            !world.Tiles.Items.TryGetValue(homeTile, out var homeState) ||
            homeState.Junctions.Count == 0)
        {
            return false;
        }

        var canJump = PlanningSystem.CanUseCriticalTraversal(patient);
        foreach (var junction in homeState.Junctions)
        {
            if (Connectivity.Reachable(world, from, junction, canJump))
            {
                return false; // дойдёт сама, пусть и ползком
            }
        }

        return true;
    }

    internal static bool TryGetPerson(
        WorldState world, EntityId id, out NPCState person, out bool dead)
    {
        if (world.Entities.Npcs.TryGetValue(id, out person))
        {
            dead = false;
            return true;
        }

        dead = world.Entities.Corpses.TryGetValue(id, out person);
        return dead;
    }

    // §118.4: putting a critical patient down is the completion of one
    // evacuation, not a fresh rescue request on the next Medium tick. The
    // patient's own plan is already over, so this sleep is deliberately owned
    // by the recovery state until ExitDying/WakeFromComa clears it.
    internal static bool IsRecoveryResting(WorldState world, NPCState patient)
    {
        if (patient.Health <= 0f || patient.IsBeingCarried ||
            (!patient.IsDying && patient.Mind.ComaCause == ComaCause.None) ||
            patient.Execution.Status != ExecutionStatus.InProgress ||
            patient.Execution.CurrentInteraction != InteractionType.Sleep)
        {
            return false;
        }

        if (patient.Execution.TargetObject is { } bedId)
        {
            return world.Entities.Objects.TryGetValue(bedId, out var bed) &&
                IsBed(bed) && bed.IsOccupied && bed.CurrentUser == patient.Id;
        }

        // Ground was validated immediately before the carrier committed to the
        // route. Keep that result stable for this recovery episode: a looter
        // walking across the threat-radius edge must become a combat problem,
        // not restart the pick-up/put-down transport carousel.
        return true;
    }

    internal static bool HasOpenBleeding(NPCState patient)
    {
        foreach (var wound in patient.Wounds)
        {
            if (!wound.Stabilized && wound.Clot01 < 1f &&
                wound.Severity * (1f - wound.Heal01) > 0.001f)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsThreatened(WorldState world, NPCState patient)
    {
        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f &&
                HexSpatialMath.HexDistance(mob.Tile, patient.Tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return true;
            }
        }

        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id != patient.Id && other.Health > 0f &&
                !other.IsUnconscious(world.Tick) &&
                FactionRelations.AreHostile(world, other, patient) &&
                HexSpatialMath.HexDistance(other.Tile, patient.Tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool NeedsImmediateEvacuation(WorldState world, NPCState patient)
    {
        if (IsThreatened(world, patient) || System.MathF.Abs(patient.Needs.ThermalComfort) >= 0.85f)
        {
            return true;
        }

        return world.Tiles.Items.TryGetValue(patient.Tile, out var tile) &&
            SpatialQueries.IsSwimTile(tile);
    }

    internal static bool TryFindApproach(
        WorldState world, NPCState helper, NPCState patient, out JunctionId approach)
    {
        approach = default;
        var patientJunction = patient.CurrentJunction ??
            SpatialQueries.FindNearestJunction(world, patient.Position);
        if (patientJunction is not { } target)
        {
            return false;
        }

        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, helper);
        var from = helper.CurrentJunction;
        var best = float.MaxValue;
        foreach (var candidate in SpatialQueries.GetPassableNeighbors(world, target))
        {
            if (occupiedByActor.Contains(candidate) ||
                !SpatialQueries.IsJunctionFree(world, candidate) ||
                !world.Junctions.Items.TryGetValue(candidate, out var junction))
            {
                continue;
            }

            // ⭐ Подход обязан быть достижим ИМЕННО ЭТОЙ спасательнице. Здесь
            // проверки не было — в отличие от TryFindDestination и разбора
            // маршрута переноски ниже, которые обе спрашивают Reachable с её
            // CanJump. Выбор шёл по прямой дистанции, поэтому раненая без
            // прыжка получала подход на приподнятой полке, куда можно только
            // запрыгнуть: следующим тиком путь не строился, спасение
            // отменялось, назначалось снова — и так по кругу.
            // Замер (seed 476005489, тик 12440): NPC3 CanJump=false стоит на
            // материке (плоская компонента 1, 3367 узлов), лежащая — на полке
            // (компонента 995, 37 узлов), спуска между ними нет и быть не
            // может (наверх без прыжка не залезть). 18 назначений, 0 доносов.
            if (from is { } origin &&
                !Connectivity.Reachable(world, origin, candidate, helper.Body.CanJump))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(helper.Position, junction.WorldPosition);
            if (distance < best)
            {
                best = distance;
                approach = candidate;
            }
        }

        return best < float.MaxValue &&
            SpatialMutations.TryReserveJunction(world, approach, helper.Id, world.Tick, 96);
    }

    internal static bool TryFindDestination(
        WorldState world, NPCState helper, NPCState patient,
        out WorldObjectState destination, out JunctionId approach,
        out TileCoord destinationTile, out List<JunctionId> route,
        bool allowGround = true, string requiredBedDefinitionId = null)
    {
        destination = null;
        approach = default;
        destinationTile = default;
        route = new List<JunctionId>();
        DestinationPathSearchesLastCall = 0;
        ReconcileBedOccupancy(world);
        if (helper.CurrentJunction is not { } from)
        {
            return false;
        }

        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, helper);
        var beds = new List<WorldObjectState>();
        foreach (var candidate in world.Entities.Objects.Values)
        {
            var priority = BedPriority(world, helper, patient, candidate);
            if (priority == int.MaxValue ||
                (requiredBedDefinitionId is not null &&
                 candidate.DefinitionId != requiredBedDefinitionId) ||
                (candidate.IsOccupied && candidate.CurrentUser != patient.Id) ||
                !DestinationSafe(world, patient, candidate.Tile))
            {
                continue;
            }

            beds.Add(candidate);
        }

        beds.Sort((a, b) =>
        {
            var byPriority = BedPriority(world, helper, patient, a)
                .CompareTo(BedPriority(world, helper, patient, b));
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byDistance = HexSpatialMath.HexDistance(helper.Tile, a.Tile)
                .CompareTo(HexSpatialMath.HexDistance(helper.Tile, b.Tile));
            return byDistance != 0 ? byDistance : a.Id.Value.CompareTo(b.Id.Value);
        });

        foreach (var candidate in beds)
        {
            if (SearchBudgetExhausted)
            {
                break;
            }

            if (!TryObjectApproach(
                    world, helper, patient, candidate, from, occupiedByActor,
                    out var stand, out var candidateRoute))
            {
                continue;
            }

            destination = candidate;
            approach = stand;
            destinationTile = candidate.Tile;
            route = candidateRoute;
            break;
        }

        if (destination is null)
        {
            if (!allowGround)
            {
                return false;
            }

            if (!TryGroundDestination(
                    world, helper, patient, from, occupiedByActor,
                    out destination, out approach, out destinationTile, out route))
            {
                return false;
            }
        }

        if (!SpatialMutations.TryReserveJunction(world, approach, helper.Id, world.Tick, 240))
        {
            destination = null;
            route.Clear();
            return false;
        }

        if (destination is not null && IsBed(destination))
        {
            destination.IsOccupied = true;
            destination.CurrentUser = patient.Id;
        }

        return true;
    }

    private static bool TryObjectApproach(
        WorldState world, NPCState helper, NPCState patient,
        WorldObjectState target, JunctionId from,
        HashSet<JunctionId> occupiedByActor, out JunctionId approach,
        out List<JunctionId> route)
    {
        approach = default;
        route = new List<JunctionId>();
        if (target.Junctions.Count == 0)
        {
            return false;
        }

        var obstacleRadius = world.Content.ObjectDefinitions.TryGetValue(
            target.DefinitionId, out var definition)
                ? definition.ObstacleRadius
                : 0f;
        var candidates = new List<JunctionId>();
        SpatialQueries.CollectStandableAround(
            world, target.Junctions[0], candidates, 96,
            SpatialQueries.BesideReach(obstacleRadius), target,
            SpatialQueries.RimPurpose.Route);

        candidates.Sort((a, b) =>
        {
            var aDistance = world.Junctions.Items.TryGetValue(a, out var aJunction)
                ? HexSpatialMath.Distance(helper.Position, aJunction.WorldPosition)
                : float.MaxValue;
            var bDistance = world.Junctions.Items.TryGetValue(b, out var bJunction)
                ? HexSpatialMath.Distance(helper.Position, bJunction.WorldPosition)
                : float.MaxValue;
            var byDistance = aDistance.CompareTo(bDistance);
            return byDistance != 0 ? byDistance : a.Value.CompareTo(b.Value);
        });

        foreach (var candidate in candidates)
        {
            if (SearchBudgetExhausted)
            {
                return false;
            }

            if (!SpatialQueries.IsJunctionFree(world, candidate) ||
                !world.Junctions.Items.ContainsKey(candidate) ||
                !Connectivity.Reachable(world, from, candidate, helper.Body.CanJump))
            {
                continue;
            }

            if (TryBuildCarryRoute(
                    world, helper, patient, from, candidate, occupiedByActor,
                    out var candidateRoute))
            {
                approach = candidate;
                route = candidateRoute;
                return true;
            }
        }

        return false;
    }

    private static int BedPriority(
        WorldState world, NPCState helper, NPCState patient, WorldObjectState candidate)
    {
        if (!IsBed(candidate))
        {
            return int.MaxValue;
        }

        if (candidate.Owner == patient.Id)
        {
            return 0;
        }

        if (candidate.Owner == helper.Id)
        {
            return 1;
        }

        return ColonyQueries.InCamp(world, candidate.Tile, patient.Faction)
            ? 2
            : int.MaxValue;
    }

    private static bool TryGroundDestination(
        WorldState world, NPCState helper, NPCState patient, JunctionId from,
        HashSet<JunctionId> occupiedByActor, out WorldObjectState destination,
        out JunctionId approach, out TileCoord destinationTile,
        out List<JunctionId> route)
    {
        destination = null;
        approach = default;
        destinationTile = default;
        route = new List<JunctionId>();

        WorldObjectState hearth = null;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (candidate.DefinitionId != ContentIds.Campfire ||
                !ColonyQueries.InCamp(world, candidate.Tile, patient.Faction))
            {
                continue;
            }

            // A cold hearth still marks the camp. Stable id wins when a camp
            // has several fires, matching the ordinary ground-sleep anchor.
            if (hearth is null || candidate.Id.Value < hearth.Id.Value)
            {
                hearth = candidate;
            }
        }

        var anchor = hearth?.Tile ??
            ColonyQueries.Home(world, patient.Faction) ??
            ColonyQueries.Home(world, helper.Faction);
        if (anchor is not { } campAnchor)
        {
            return false;
        }

        var candidateTiles = new List<Tile>();
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (!tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Blocked) ||
                tile.Flags.HasFlag(TileFlags.Water) ||
                !ColonyQueries.InCamp(world, tile.Coord, patient.Faction) ||
                HexSpatialMath.HexDistance(tile.Coord, campAnchor) >
                    Spec72.MaxCampRadiusTiles)
            {
                continue;
            }

            candidateTiles.Add(tile);
        }

        candidateTiles.Sort((a, b) =>
        {
            var byAnchor = HexSpatialMath.HexDistance(a.Coord, campAnchor)
                .CompareTo(HexSpatialMath.HexDistance(b.Coord, campAnchor));
            if (byAnchor != 0)
            {
                return byAnchor;
            }

            var byHelper = HexSpatialMath.HexDistance(helper.Tile, a.Coord)
                .CompareTo(HexSpatialMath.HexDistance(helper.Tile, b.Coord));
            if (byHelper != 0)
            {
                return byHelper;
            }

            var byQ = a.Coord.Q.CompareTo(b.Coord.Q);
            return byQ != 0 ? byQ : a.Coord.R.CompareTo(b.Coord.R);
        });

        foreach (var tile in candidateTiles)
        {
            if (SearchBudgetExhausted)
            {
                return false;
            }

            if (!DestinationSafe(world, patient, tile.Coord) ||
                !LyingSpot.CanSolveOnTile(world, patient, tile.Coord))
            {
                continue;
            }

            var center = HexSpatialMath.TileToWorld(tile.Coord);
            var junctions = new List<JunctionId>(tile.Junctions);
            junctions.Sort((a, b) =>
            {
                var aDistance = world.Junctions.Items.TryGetValue(a, out var aJunction)
                    ? HexSpatialMath.Distance(aJunction.WorldPosition, center)
                    : float.MaxValue;
                var bDistance = world.Junctions.Items.TryGetValue(b, out var bJunction)
                    ? HexSpatialMath.Distance(bJunction.WorldPosition, center)
                    : float.MaxValue;
                var byDistance = aDistance.CompareTo(bDistance);
                return byDistance != 0 ? byDistance : a.Value.CompareTo(b.Value);
            });

            foreach (var candidate in junctions)
            {
                if (SearchBudgetExhausted)
                {
                    return false;
                }

                if (SpatialQueries.IsJunctionFree(world, candidate) &&
                    world.Junctions.Items.TryGetValue(candidate, out var junction) &&
                    !junction.Blocked &&
                    !SpatialQueries.IsAllWaterJunction(world, candidate) &&
                    Connectivity.Reachable(world, from, candidate, helper.Body.CanJump) &&
                    TryBuildCarryRoute(
                        world, helper, patient, from, candidate, occupiedByActor,
                        out var candidateRoute))
                {
                    destination = hearth;
                    approach = candidate;
                    destinationTile = tile.Coord;
                    route = candidateRoute;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryBuildCarryRoute(
        WorldState world, NPCState helper, NPCState patient, JunctionId from,
        JunctionId approach, HashSet<JunctionId> occupiedByActor,
        out List<JunctionId> route)
    {
        // The current save often puts a valid ground-rest point on the same
        // hex, one lattice edge away. Sending that two-node move through the
        // island-wide weighted Dijkstra caused the visible 200-350 ms freeze
        // before every pickup. A direct graph edge has no alternate route to
        // compare: validate its carry semantics locally and execute it.
        route = new List<JunctionId> { from };
        if (from == approach)
        {
            return RouteSupportsCarryTransfers(world, helper, patient, route);
        }

        // The pathfinder deliberately permits its GOAL even when the broad
        // lying-body avoid set touches it; TryFindApproach/ground selection
        // already proved the concrete junction free. Match that rule here.
        if (world.Junctions.Items.TryGetValue(from, out var start) &&
            start.Neighbors.Contains(approach))
        {
            route.Add(approach);
            if (RouteSupportsCarryTransfers(world, helper, patient, route))
            {
                return true;
            }
        }

        if (SearchBudgetExhausted)
        {
            route.Clear();
            return false;
        }

        DestinationPathSearchesLastCall++;
        route = HexPathfinder.FindPath(
            world, from, approach, occupiedByActor,
            weightClimb: true, canJump: helper.Body.CanJump,
            danger: PathfindingSystem.RouteAvoidRing(world, helper),
            dangerCost: Spec62.DangerStepCost);
        if (route.Count == 0 || !RouteSupportsCarryTransfers(world, helper, patient, route))
        {
            route.Clear();
            return false;
        }

        return true;
    }

    // The path itself is cheap to execute only if every directed step agrees
    // with the carry transaction: never swim, and every elevation crossing has
    // a full-body landing spot on the far side before the carrier takes off.
    private static bool RouteSupportsCarryTransfers(
        WorldState world, NPCState helper, NPCState patient, List<JunctionId> route)
    {
        if (!world.Tiles.Items.TryGetValue(helper.Tile, out var standTile))
        {
            return false;
        }

        for (var i = 1; i < route.Count; i++)
        {
            if (!world.Junctions.Items.TryGetValue(route[i], out var junction))
            {
                return false;
            }

            // §21.21B v24: the last boundary node is an arrival on the current
            // side when that side owns the node; it is not a crossing.
            if (i == route.Count - 1 && junction.Tiles.Contains(standTile.Coord))
            {
                continue;
            }

            if (!HexPathfinder.TryGetDirectedStepTile(world, route[i - 1], route[i], out var nextTile) ||
                SpatialQueries.IsSwimTile(nextTile))
            {
                return false;
            }

            if (nextTile.Elevation != standTile.Elevation &&
                !LyingSpot.CanSolveOnTile(world, patient, nextTile.Coord))
            {
                return false;
            }

            standTile = nextTile;
        }

        return true;
    }

    private static bool DestinationSafe(WorldState world, NPCState patient, TileCoord tile)
    {
        if (!world.Tiles.Items.TryGetValue(tile, out var tileState) ||
            SpatialQueries.IsSwimTile(tileState))
        {
            return false;
        }

        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f &&
                HexSpatialMath.HexDistance(mob.Tile, tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return false;
            }
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f && FactionRelations.AreHostile(world, npc, patient) &&
                HexSpatialMath.HexDistance(npc.Tile, tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsBed(WorldObjectState worldObject) =>
        ContentIds.IsBed(worldObject.DefinitionId);

    internal static void BeginCarry(
        WorldState world, NPCState carrier, NPCState patient,
        WorldObjectState destination, JunctionId destinationJunction,
        TileCoord destinationTile, List<JunctionId> route)
    {
        carrier.Mind.InterruptedRescuePatientId = null;
        ReleasePatientBedOnWake(world, patient);
        ExecutionSystem.ReleaseClaims(world, patient);
        if (patient.CurrentJunction is { } lying)
        {
            SpatialMutations.FreeJunction(world, lying, patient.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lying, patient.Id);
        }

        patient.CurrentJunction = null;
        patient.CarriedByNpcId = carrier.Id;
        carrier.CarriedNpcId = patient.Id;
        carrier.RescueDestinationObjectId = destination?.Id;
        carrier.IsFighting = false;
        MeleeSwing.Cancel(carrier);
        carrier.StrikeReadyAtTick = 0;

        carrier.Plan.TargetObjectId = null;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.Plan.TargetJunctionId = destinationJunction;
        carrier.Plan.TargetTile = destinationTile;
        carrier.Plan.Steps.Clear();
        carrier.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PutPersonInBed,
            TargetJunction = destinationJunction,
            TargetObject = destination?.Id,
            Interaction = InteractionType.PutInBed
        });
        carrier.Plan.CurrentStepIndex = 0;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Movement.JunctionPath.Clear();
        foreach (var junction in route)
        {
            carrier.Movement.JunctionPath.Add(junction);
        }

        carrier.Movement.PathIndex = route.Count > 1 ? 1 : route.Count;
        carrier.Movement.BlockedWaitTicks = 0;
        carrier.Movement.HopArmed = false;
        carrier.Movement.HopPathIndex = -1;
        carrier.Movement.IsMoving = route.Count > 1;
        carrier.Movement.SetStatus(
            carrier.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived);

        SyncPatient(world, carrier, patient);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "PersonPickedUp",
                destination is null
                    ? $"NPC{patient.Id.Value} -> ground@{destinationTile.Q},{destinationTile.R} " +
                      $"Route={route.Count} Searches={DestinationPathSearchesLastCall}"
                    : $"NPC{patient.Id.Value} -> {destination.DefinitionId}#{destination.Id.Value} " +
                      $"at {destinationTile.Q},{destinationTile.R} Route={route.Count} " +
                      $"Searches={DestinationPathSearchesLastCall}");
        }
    }

    /// <summary>§124.1: игрок выбрал КОНКРЕТНУЮ кровать для человека, который
    /// уже на руках. Бронирует подход и кровать и ставит носильщице ровно тот
    /// же план из одного шага PutPersonInBed, каким §105.17 ходит ИИ, — дальше
    /// прибытие и укладку ведёт штатный RunRescue → PutDownAtDestination.
    /// false = подхода нет (занято/недостижимо); занятость кровати проверяет
    /// вызывающий ДО (чтобы отказать Occupied, а не Unreachable).</summary>
    internal static bool TryBeginManualBedPlacement(
        WorldState world, NPCState carrier, NPCState patient, WorldObjectState bed)
    {
        if (carrier.CurrentJunction is not { } from)
        {
            return false;
        }

        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, carrier);
        if (!TryObjectApproach(
                world, carrier, patient, bed, from, occupiedByActor,
                out var approach, out var route) ||
            !SpatialMutations.TryReserveJunction(world, approach, carrier.Id, world.Tick, 240))
        {
            return false;
        }

        bed.IsOccupied = true;
        bed.CurrentUser = patient.Id;
        carrier.RescueDestinationObjectId = bed.Id;

        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.TargetObjectId = null;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.Plan.TargetJunctionId = approach;
        carrier.Plan.TargetTile = bed.Tile;
        carrier.Plan.Steps.Clear();
        carrier.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PutPersonInBed,
            TargetJunction = approach,
            TargetObject = bed.Id,
            Interaction = InteractionType.PutInBed
        });
        carrier.Plan.CurrentStepIndex = 0;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Movement.JunctionPath.Clear();
        foreach (var junction in route)
        {
            carrier.Movement.JunctionPath.Add(junction);
        }

        carrier.Movement.PathIndex = route.Count > 1 ? 1 : route.Count;
        carrier.Movement.BlockedWaitTicks = 0;
        carrier.Movement.HopArmed = false;
        carrier.Movement.HopPathIndex = -1;
        carrier.Movement.IsMoving = route.Count > 1;
        carrier.Movement.SetStatus(
            carrier.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived);
        return true;
    }

    /// <summary>§124: ручной перенос заканчивается в руках, а не автоматически
    /// выбранной кроватью. Следующий MoveTo держит связь до явного PutDown.</summary>
    internal static void BeginManualCarry(
        WorldState world, NPCState carrier, NPCState person, bool dead)
    {
        carrier.Mind.InterruptedRescuePatientId = null;
        carrier.RescueDestinationObjectId = null;
        if (dead)
        {
            CorpseMath.SuspendAnchor(world, person);
        }
        else
        {
            ReleasePatientBedOnWake(world, person);
            ExecutionSystem.ReleaseClaims(world, person);
            if (person.CurrentJunction is { } lying)
            {
                SpatialMutations.FreeJunction(world, lying, person.Id);
                SpatialMutations.ReleaseJunctionReservation(world, lying, person.Id);
            }
        }

        person.CurrentJunction = null;
        person.CarriedByNpcId = carrier.Id;
        carrier.CarriedNpcId = person.Id;
        carrier.IsFighting = false;
        MeleeSwing.Cancel(carrier);
        carrier.StrikeReadyAtTick = 0;
        // §118.4 r2 (#166): на руки берут и БОДРСТВУЮЩУЮ свою. У лежащей плана
        // нет по построению, а у идущей он есть — и без этой остановки она
        // продолжала бы «идти» по своему маршруту с рук носильщика: узла у неё
        // уже нет, так что это была бы не ходьба, а тихо ломающееся состояние.
        if (!dead && !person.IsLyingDown(world.Tick))
        {
            PlanInterruption.TryAbort(
                world, person, InterruptionCause.PlayerCommand, "её взяли на руки");
            person.Mind.CurrentGoal = GoalType.None;
            person.Movement.JunctionPath.Clear();
            person.Movement.IsMoving = false;
            person.Movement.SetStatus(MovementStatus.Idle);
        }

        SyncPatient(world, carrier, person, dead);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "PersonPickedUp",
                $"NPC{person.Id.Value} Manual=1 Dead={(dead ? 1 : 0)}");
        }
    }

    internal static void SyncAll(WorldState world)
    {
        // A normal rescue claim is already coherent. Only a loaded/interrupting
        // carry whose plan ownership vanished needs the object-wide repair on
        // a Fast tick; ordinary selection also repairs immediately above.
        foreach (var carrier in world.Entities.Npcs.Values)
        {
            if (carrier.CarriedNpcId is not null &&
                (carrier.Plan.Status != PlanStatus.Active ||
                 carrier.Plan.Goal != GoalType.Rescue ||
                 carrier.Mind.CurrentGoal != GoalType.Rescue))
            {
                ReconcileBedOccupancy(world);
                break;
            }
        }

        foreach (var carrier in world.Entities.Npcs.Values)
        {
            if (carrier.CarriedNpcId is not { } patientId)
            {
                continue;
            }

            if (!TryGetPerson(world, patientId, out var patient, out var dead) ||
                patient.CarriedByNpcId != carrier.Id || carrier.Health <= 0f ||
                carrier.IsLyingDown(world.Tick) ||
                // Ноги отказали ПОД ношей — положить, а не ползти с телом на
                // руках. Это же чинит уже сохранённые миры, где переноска
                // началась до появления гейта.
                carrier.Body.IsCrawling ||
                (!dead && patient.Health <= 0f) ||
                carrier.Movement.Status == MovementStatus.Invalid)
            {
                DropSafely(world, carrier, "carry link/path/carrier invalid");
                continue;
            }

            SyncPatient(world, carrier, patient, dead);
        }
    }

    /// <summary>MovementSystem изменил позицию носильщика после основной
    /// проверки ссылок — одним дешёвым проходом закрепить модель тела на новой
    /// позиции до экспорта снапшота/сейва этого же тика.</summary>
    internal static void SyncCarriedPositionsAfterMovement(WorldState world)
    {
        foreach (var carrier in world.Entities.Npcs.Values)
        {
            if (carrier.CarriedNpcId is { } personId &&
                TryGetPerson(world, personId, out var person, out var dead) &&
                person.CarriedByNpcId == carrier.Id)
            {
                SyncPatient(world, carrier, person, dead);
            }
        }
    }

    private static void SyncPatient(
        WorldState world, NPCState carrier, NPCState patient, bool dead = false)
    {
        if (patient.Tile != carrier.Tile)
        {
            if (!dead)
            {
                SpatialMutations.MoveEntityToTile(world, patient.Id, patient.Tile, carrier.Tile);
            }
            patient.Tile = carrier.Tile;
        }

        patient.Fragment = carrier.Fragment;
        patient.Position = carrier.Position;
        patient.RotationDegrees = carrier.RotationDegrees + 90f;
        patient.Movement.IsMoving = false;
        patient.Movement.JunctionPath.Clear();
    }

    internal static void PutDownAtDestination(WorldState world, NPCState carrier, NPCState patient)
    {
        WorldObjectState destination = null;
        if (carrier.RescueDestinationObjectId is { } destinationId)
        {
            world.Entities.Objects.TryGetValue(destinationId, out destination);
        }

        var wakeJunction = carrier.CurrentJunction;
        ClearLinks(carrier, patient);
        carrier.Mind.InterruptedRescuePatientId = null;
        // Уложенная не владеет прошлым планом: застрявший Active-план поверх
        // её Sleep-интеракции — ровно «split-brain» bug-128, и
        // SleepPlanConsistencySystem вытащил бы её из кровати следующим тиком.
        // Органический обморок план уже разобрал (Abort) — ветка мертва для
        // golden trace и стреляет только на протухшем Active.
        if (patient.Plan.Status == PlanStatus.Active)
        {
            patient.Plan.Status = PlanStatus.None;
            patient.Plan.Goal = GoalType.None;
            patient.Plan.Steps.Clear();
            patient.Plan.TargetObjectId = null;
            patient.Plan.TargetJunctionId = null;
            patient.Plan.TargetAgentId = null;
            patient.Plan.TargetTile = null;
            patient.Mind.CurrentGoal = GoalType.None;
        }

        var enteredBed = destination is not null && IsBed(destination) &&
            BedSleep.TryEnter(
                world, patient, destination, int.MaxValue, wakeJunction);
        if (!enteredBed)
        {
            // A bed selected earlier can become invalid before arrival (for
            // example after a legacy topology repair). Release that stale
            // patient claim and use the ordinary ground placement instead.
            if (destination is not null && IsBed(destination) &&
                destination.CurrentUser == patient.Id)
            {
                destination.IsOccupied = false;
                destination.CurrentUser = null;
            }

            MortalityHelpers.AnchorLyingBody(world, patient);
            patient.Execution.Status = ExecutionStatus.InProgress;
            patient.Execution.CurrentInteraction = InteractionType.Sleep;
            patient.Execution.TargetObject = null;
            patient.Execution.StartTick = world.Tick;
            patient.Execution.EndTick = int.MaxValue;
        }

        CompleteCarrier(world, carrier);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "PersonPutDown",
                $"NPC{patient.Id.Value} at " +
                $"{(enteredBed ? destination.DefinitionId : "ground")}");
        }
    }

    internal static void DropSafely(WorldState world, NPCState carrier, string reason)
    {
        var hadCarry = carrier.CarriedNpcId is not null;
        PutDownForPlanInterruption(world, carrier, reason);

        if (carrier.Plan.Status == PlanStatus.Active &&
            carrier.Plan.Goal == GoalType.Rescue)
        {
            PlanningSystem.SetGoalCooldown(world, carrier, GoalType.Rescue);
            PlanInterruption.TryAbort(world, carrier, InterruptionCause.RescueDrop, $"Rescue drop: {reason}");
        }
        carrier.Mind.CurrentGoal = GoalType.None;
        if (!hadCarry)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, carrier.Id, "PersonDropped", reason);

            }
        }
    }

    // §118.4: plan teardown and the carry link are one transaction. Before
    // this hook existed, combat could invalidate Rescue while leaving the
    // patient attached; the carrier then could neither swing nor replan.
    // This primitive deliberately does not touch the plan itself, so
    // PlanInterruption can call it without recursing through DropSafely.
    internal static EntityId? PutDownForPlanInterruption(
        WorldState world, NPCState carrier, string reason)
    {
        if (carrier.CarriedNpcId is not { } patientId)
        {
            // A rescue can still own its destination while the patient is not
            // in hand (during approach or while a combat-resume promise is
            // active). An interruption must release that reservation too.
            var stagedPatientId = carrier.Mind.InterruptedRescuePatientId ??
                (carrier.Plan.Goal == GoalType.Rescue ? carrier.Plan.TargetAgentId : null);
            if (stagedPatientId is { } stagedId)
            {
                world.Entities.Npcs.TryGetValue(stagedId, out var stagedPatient);
                ReleaseDestination(world, carrier, stagedPatient);
                if (stagedPatient?.Mind.PendingAidFrom == carrier.Id)
                {
                    stagedPatient.Mind.PendingAidFrom = null;
                }
                carrier.RescueDestinationObjectId = null;
            }

            return null;
        }

        TryGetPerson(world, patientId, out var patient, out var dead);
        ReleaseDestination(world, carrier, patient);
        if (patient is not null)
        {
            SyncPatient(world, carrier, patient, dead);
            ClearLinks(carrier, patient);
            if (dead)
            {
                CorpseMath.AnchorBody(world, patient);
            }
            else
            {
                MortalityHelpers.AnchorLyingBody(world, patient);
            }
        }
        else
        {
            carrier.CarriedNpcId = null;
            carrier.RescueDestinationObjectId = null;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "PersonDropped", reason);

        }
        return patient?.Id;
    }

    /// <summary>Освобождает всё, что принадлежало rescue-плану, но не роняет
    /// уже переносимого человека. Используется только новым ручным Move/Stop.</summary>
    internal static EntityId? DetachRescueDestinationForManualCarry(
        WorldState world, NPCState carrier)
    {
        NPCState person = null;
        if (carrier.CarriedNpcId is { } personId)
        {
            TryGetPerson(world, personId, out person, out _);
        }

        ReleaseDestination(world, carrier, person);
        if (person is not null && world.Entities.Npcs.ContainsKey(person.Id) &&
            person.Mind.PendingAidFrom == carrier.Id)
        {
            person.Mind.PendingAidFrom = null;
        }

        carrier.RescueDestinationObjectId = null;
        carrier.Mind.InterruptedRescuePatientId = null;
        return null;
    }

    private static void CompleteCarrier(WorldState world, NPCState carrier)
    {
        if (carrier.Plan.TargetJunctionId is { } destinationJunction)
        {
            SpatialMutations.ReleaseJunctionReservation(world, destinationJunction, carrier.Id);
        }

        carrier.Execution.Status = ExecutionStatus.None;
        carrier.Execution.CurrentInteraction = null;
        carrier.Execution.StartTick = 0;
        carrier.Execution.EndTick = 0;
        carrier.Plan.Status = PlanStatus.Completed;
        carrier.Plan.Steps.Clear();
        carrier.Plan.TargetAgentId = null;
        carrier.Plan.TargetObjectId = null;
        carrier.Plan.TargetJunctionId = null;
        carrier.Plan.TargetTile = null;
        carrier.Mind.CurrentGoal = GoalType.None;
        // §121.7: доигранная укладка — завершение ручного приказа §124.1,
        // окно внимания перезапускается. Для ИИ — безобидная запись None-ветки
        // не происходит: гейт по IsManual.
        if (ManualControlMath.IsManual(carrier))
        {
            carrier.Mind.LastManualInputTick = world.Tick;
        }

        carrier.Movement.JunctionPath.Clear();
        carrier.Movement.IsMoving = false;
        carrier.Movement.SetStatus(MovementStatus.Idle);
    }

    private static void ClearLinks(NPCState carrier, NPCState patient)
    {
        patient.CarriedByNpcId = null;
        patient.Mind.PendingAidFrom = null;
        carrier.CarriedNpcId = null;
        carrier.RescueDestinationObjectId = null;
    }

    private static void ReleaseDestination(WorldState world, NPCState carrier, NPCState patient)
    {
        if (carrier.RescueDestinationObjectId is not { } destinationId ||
            !world.Entities.Objects.TryGetValue(destinationId, out var destination))
        {
            return;
        }

        if (IsBed(destination) &&
            (patient is null || destination.CurrentUser == patient.Id))
        {
            destination.IsOccupied = false;
            destination.CurrentUser = null;
        }
    }

    internal static void ReleasePatientBedOnWake(WorldState world, NPCState patient)
    {
        if (patient.Execution.CurrentInteraction != InteractionType.Sleep)
        {
            return;
        }

        if (patient.Execution.TargetObject is { } bedId &&
            world.Entities.Objects.TryGetValue(bedId, out var bed) && IsBed(bed) &&
            bed.CurrentUser == patient.Id)
        {
            bed.IsOccupied = false;
            bed.CurrentUser = null;
        }

        patient.Execution.Status = ExecutionStatus.None;
        patient.Execution.CurrentInteraction = null;
        patient.Execution.TargetObject = null;
        patient.Execution.StartTick = 0;
        patient.Execution.EndTick = 0;
        patient.CurrentJunction = SpatialQueries.FindNearestJunction(world, patient.Position);
    }

    // Saves can contain an object claim after its sleeper was picked up or its
    // plan was interrupted. Such a claim must not force every later rescue to
    // the ground. The only live bed users are an actual sleeper, or the exact
    // patient currently being carried to that bed.
    private static void ReconcileBedOccupancy(WorldState world)
    {
        foreach (var bed in world.Entities.Objects.Values)
        {
            if (!IsBed(bed) || (!bed.IsOccupied && bed.CurrentUser is null))
            {
                continue;
            }

            var live = false;
            if (bed.CurrentUser is { } userId &&
                world.Entities.Npcs.TryGetValue(userId, out var user))
            {
                live = !user.IsBeingCarried &&
                    user.Execution.Status == ExecutionStatus.InProgress &&
                    user.Execution.CurrentInteraction == InteractionType.Sleep &&
                    user.Execution.TargetObject == bed.Id;

                if (!live && user.CarriedByNpcId is { } carrierId &&
                    world.Entities.Npcs.TryGetValue(carrierId, out var carrier))
                {
                    live = carrier.CarriedNpcId == user.Id &&
                        carrier.RescueDestinationObjectId == bed.Id;
                }

                if (!live)
                {
                    foreach (var candidateCarrier in world.Entities.Npcs.Values)
                    {
                        if (candidateCarrier.Plan.Status == PlanStatus.Active &&
                            candidateCarrier.Plan.Goal == GoalType.Rescue &&
                            candidateCarrier.Plan.TargetAgentId == user.Id &&
                            candidateCarrier.RescueDestinationObjectId == bed.Id &&
                            (candidateCarrier.CarriedNpcId == user.Id ||
                             candidateCarrier.Mind.InterruptedRescuePatientId == user.Id))
                        {
                            live = true;
                            break;
                        }
                    }
                }
            }

            if (live)
            {
                bed.IsOccupied = true;
                continue;
            }

            bed.IsOccupied = false;
            bed.CurrentUser = null;
        }
    }
}

}
