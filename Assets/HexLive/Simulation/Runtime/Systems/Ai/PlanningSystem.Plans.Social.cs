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
    // Spec 29C.5: wander to a seeded-random unblocked junction 3-8 tiles away.
    // Discoveries along the way land in spatial memory.
    private readonly System.Collections.Generic.List<Junction> _exploreCandidates = new();

    private enum ExploreRejection
    {
        None,
        NoStartOrBlocked,
        DeepWater,
        Distance,
        LiveMob,
        VisibleHostile,
        DangerMemory,
        Unreachable
    }

    // PERF (Aug-2026, BigIsland): кандидат Explore по определению лежит в
    // полосе дистанций ≤12 тайлов от неё (см. ExploreRejectionFor), а обе
    // точки входа обходили ВСЕ ~194k джанкшенов острова — 57% всей цены
    // ScoreGoals. Кольцо собирается через Tile.Junctions; junction.Tiles[0]
    // отстоит от любого своего тайла не больше чем на 1, поэтому радиус
    // сбора = 12+1. Сортировка по id воспроизводит прежний порядок обхода
    // словаря (worldgen кладёт джанкшены по возрастанию id и не удаляет их),
    // так что сидированный выбор в BuildExplorePlan остаётся бит-в-бит тем же.
    private static readonly System.Collections.Generic.HashSet<JunctionId> _exploreSeenScratch = new();

    private static readonly System.Collections.Generic.List<Junction> _exploreRingScratch = new();

    private const int ExploreRingCollectRadius = 13;

    private static void CollectExploreRing(
        WorldState world, NPCState npc,
        System.Collections.Generic.List<Junction> into)
    {
        into.Clear();
        _exploreSeenScratch.Clear();
        for (var dq = -ExploreRingCollectRadius; dq <= ExploreRingCollectRadius; dq++)
        {
            var lo = System.Math.Max(-ExploreRingCollectRadius, -dq - ExploreRingCollectRadius);
            var hi = System.Math.Min(ExploreRingCollectRadius, -dq + ExploreRingCollectRadius);
            for (var dr = lo; dr <= hi; dr++)
            {
                var coord = new TileCoord(npc.Tile.Q + dq, npc.Tile.R + dr);
                if (!world.Tiles.Items.TryGetValue(coord, out var tile))
                {
                    continue;
                }

                foreach (var junctionId in tile.Junctions)
                {
                    if (_exploreSeenScratch.Add(junctionId) &&
                        world.Junctions.Items.TryGetValue(junctionId, out var junction))
                    {
                        into.Add(junction);
                    }
                }
            }
        }

        into.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
    }

    /// <summary>One source of truth for the Explore auction and planner.
    /// An exploration bid is invalid when there is no junction the same
    /// planner could actually use; bidding first and discovering that only
    /// after selection produced a permanent Explore/PlanFailed loop.</summary>
    internal static bool HasExploreCandidate(WorldState world, NPCState npc)
    {
        // Булевому вопросу не нужны ни дедуп, ни сортировка (суб-сетка — это
        // ~48 узлов НА ТАЙЛ, кольцо радиуса 13 — ~26k узлов; собирать и
        // сортировать их ради «есть ли хоть один» стоило ~5 мс на вызов).
        // Обходим тайлы кольца и отвечаем на первом же годном узле; узел,
        // повторившийся у соседнего тайла, просто отвергнется ещё раз.
        for (var dq = -ExploreRingCollectRadius; dq <= ExploreRingCollectRadius; dq++)
        {
            var lo = System.Math.Max(-ExploreRingCollectRadius, -dq - ExploreRingCollectRadius);
            var hi = System.Math.Min(ExploreRingCollectRadius, -dq + ExploreRingCollectRadius);
            for (var dr = lo; dr <= hi; dr++)
            {
                var coord = new TileCoord(npc.Tile.Q + dq, npc.Tile.R + dr);
                if (!world.Tiles.Items.TryGetValue(coord, out var tile))
                {
                    continue;
                }

                foreach (var junctionId in tile.Junctions)
                {
                    if (world.Junctions.Items.TryGetValue(junctionId, out var junction) &&
                        ExploreRejectionFor(world, npc, junction) == ExploreRejection.None)
                    {
                        return true;
                    }
                }
            }
        }

        // Low-rate headless diagnosis for the exact "FleeUnavailable -> Idle"
        // class. This is trace-only and allocates only once per 200 ticks while
        // a critical NPC has literally no legal discovery destination.
        if (SimTrace.Enabled && ExploreMustAvoidDeepWater(npc) && world.Tick % 200 == 0)
        {
            var counts = new int[System.Enum.GetValues(typeof(ExploreRejection)).Length];
            foreach (var junction in world.Junctions.Items.Values)
            {
                counts[(int)ExploreRejectionFor(world, npc, junction)]++;
            }

            Trace.Debug(world, npc.Id, "CriticalExploreUnavailable",
                $"Tile={npc.Tile.Q},{npc.Tile.R} " +
                $"Junction={(npc.CurrentJunction is { } at ? at.Value.ToString() : "none")} " +
                $"CanJump={npc.Body.CanJump} " +
                $"LegL={npc.Body.LimbFunction(BodyPart.LegL):F2} " +
                $"LegR={npc.Body.LimbFunction(BodyPart.LegR):F2} " +
                $"BlockedOrNoStart={counts[(int)ExploreRejection.NoStartOrBlocked]} " +
                $"DeepWater={counts[(int)ExploreRejection.DeepWater]} " +
                $"Distance={counts[(int)ExploreRejection.Distance]} " +
                $"LiveMob={counts[(int)ExploreRejection.LiveMob]} " +
                $"VisibleHostile={counts[(int)ExploreRejection.VisibleHostile]} " +
                $"Danger={counts[(int)ExploreRejection.DangerMemory]} " +
                $"Unreachable={counts[(int)ExploreRejection.Unreachable]}");
        }

        return false;
    }

    internal static bool ExploreMustAvoidDeepWater(NPCState npc) =>
        npc.Mind.IsDehydrated || npc.Mind.IsStarving ||
        npc.Needs.Energy <= Spec49.DeadTiredEnergy;

    /// <summary>Last-resort ledge traversal for an explicit or survival route.
    /// The ordinary jump threshold is deliberately strict (both legs at 0.75),
    /// but treating 0.74/0.62 as absolute immobility let a survivor step DOWN
    /// into a one-hex depression and made every upward exit unreachable
    /// (bug #191). A conscious body with both legs still supporting a crawl may
    /// make the risky scramble for ReachSafeGround, Flee/Homeward or a player
    /// order; routine travel and a lost/fully collapsed leg remain no-jump.</summary>
    internal static bool CanUseCriticalTraversal(NPCState npc) =>
        npc.Body.CanJump ||
        (npc.Body.LimbFunction(BodyPart.LegL) >= BodyState.CrawlLegFunctionThreshold &&
         npc.Body.LimbFunction(BodyPart.LegR) >= BodyState.CrawlLegFunctionThreshold);

    /// <summary>A critical swimmer needs an actual dry graph destination
    /// before ReachSafeGround may bid. The target is deliberately stricter
    /// than a mixed shore junction: every represented tile must be walkable
    /// dry land, so arriving cannot still leave the body in deep water.</summary>
    internal static JunctionId? FindCriticalSwimExit(
        WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return null;
        }

        // §158.4: ближайший сухой выход ищется кольцами от неё, не по всему
        // графу; дальше NearestSearchMaxRadiusTiles выхода не считается.
        var exit = LocalSearch.FindNearest(
            world, npc.Tile, npc.Position, LocalSearch.NearestSearchMaxRadiusTiles,
            junction =>
            {
                if (junction.Id.Equals(from) || junction.Blocked ||
                    junction.Tiles.Count == 0 ||
                    !SpatialQueries.IsJunctionFree(world, junction.Id))
                {
                    return false;
                }

                foreach (var coord in junction.Tiles)
                {
                    if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                        !tile.Flags.HasFlag(TileFlags.Walkable) ||
                        SpatialQueries.IsSwimTile(tile))
                    {
                        return false;
                    }
                }

                return Connectivity.Reachable(
                    world, from, junction.Id, CanUseCriticalTraversal(npc));
            });
        JunctionId? best = exit?.Id;

        return best;
    }

    internal static bool IsExploreCandidate(
        WorldState world, NPCState npc, Junction junction) =>
        ExploreRejectionFor(world, npc, junction) == ExploreRejection.None;

    private static ExploreRejection ExploreRejectionFor(
        WorldState world, NPCState npc, Junction junction)
    {
        if (junction.Blocked || npc.CurrentJunction is not { } exploreFrom ||
            junction.Id.Equals(exploreFrom))
        {
            return ExploreRejection.NoStartOrBlocked;
        }

        var tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : npc.Tile;
        if (ExploreMustAvoidDeepWater(npc) &&
            SpatialQueries.IsSwimTile(world, tile))
        {
            return ExploreRejection.DeepWater;
        }
        var criticalDiscovery = ExploreMustAvoidDeepWater(npc);
        var distance = HexSpatialMath.HexDistance(npc.Tile, tile);
        if ((!criticalDiscovery && distance is < 3 or > 8) ||
            (criticalDiscovery && distance is < 2 or > 12))
        {
            return ExploreRejection.Distance;
        }

        // Ordinary wandering respects the whole remembered danger map. A
        // starving/dehydrated last-resort search cannot: an expelled NPC in
        // seed 8675309 had no route to her faction camp, every nearby route was
        // covered by old dog memories, and she idled at Thirst=1.00 for 2,000
        // ticks. During a crisis, avoid the PRESENT threat instead — live mobs,
        // visible hostiles and only the fresh edge of a remembered attack — but
        // permit a route through yesterday's fear when the alternative is a
        // deterministic death in place.
        if (criticalDiscovery && MobSystem.MobNear(world, tile, 3))
        {
            return ExploreRejection.LiveMob;
        }

        foreach (var hostile in npc.Perception.Hostiles)
        {
            if (criticalDiscovery &&
                HexSpatialMath.HexDistance(tile, hostile.Tile) <= 3)
            {
                return ExploreRejection.VisibleHostile;
            }
        }

        foreach (var danger in npc.Memory.Dangers)
        {
            if ((!criticalDiscovery ||
                 world.Tick - danger.Tick <= SimBalance.BuildDangerFreshTicks) &&
                HexSpatialMath.HexDistance(tile, danger.Tile) <= 3)
            {
                return ExploreRejection.DangerMemory;
            }
        }

        // §50.9: the candidate must be reachable with this NPC's present
        // body capabilities, including the directed no-jump graph.
        return Connectivity.Reachable(
                world, exploreFrom, junction.Id,
                criticalDiscovery
                    ? CanUseCriticalTraversal(npc)
                    : CanUseRoutineTraversal(npc))
            ? ExploreRejection.None
            : ExploreRejection.Unreachable;
    }

    private readonly System.Collections.Generic.Dictionary<TileCoord, (int NewTiles, int LastSurvey)>
        _exploreSurveyScores = new();

    // §27.18A r3: rank only already-admissible endpoints, using personal
    // survey history and static topology. No world object lookup, no shared fog.
    private void PreferUnsurveyedExploreTargets(WorldState world, NPCState npc)
    {
        _exploreSurveyScores.Clear();
        var bestNew = -1;
        var bestLast = int.MaxValue;
        var retained = 0;
        for (var i = 0; i < _exploreCandidates.Count; i++)
        {
            var candidate = _exploreCandidates[i];
            var tile = HexSpatialMath.WorldToTile(candidate.WorldPosition);
            if (!_exploreSurveyScores.TryGetValue(tile, out var score))
            {
                var fresh = 0;
                var radius = AiBalance.PerceptionRadiusTiles;
                for (var dq = -radius; dq <= radius; dq++)
                {
                    var lo = System.Math.Max(-radius, -dq - radius);
                    var hi = System.Math.Min(radius, -dq + radius);
                    for (var dr = lo; dr <= hi; dr++)
                    {
                        var seen = new TileCoord(tile.Q + dq, tile.R + dr);
                        if (world.Tiles.Items.ContainsKey(seen) && !npc.Memory.SurveyedTiles.ContainsKey(seen))
                            fresh++;
                    }
                }
                score = (fresh, npc.Memory.SurveyedTiles.TryGetValue(tile, out var tick) ? tick : -1);
                _exploreSurveyScores.Add(tile, score);
            }
            if (score.NewTiles > bestNew || score.NewTiles == bestNew && score.LastSurvey < bestLast)
            {
                bestNew = score.NewTiles;
                bestLast = score.LastSurvey;
                retained = 0;
            }
            if (score.NewTiles == bestNew && score.LastSurvey == bestLast)
                _exploreCandidates[retained++] = candidate;
        }
        if (retained < _exploreCandidates.Count)
            _exploreCandidates.RemoveRange(retained, _exploreCandidates.Count - retained);
    }

    internal void BuildExplorePlan(WorldState world, NPCState npc, bool preferPersonalSurvey = false)
    {
        // PERF: то же кольцо, что в HasExploreCandidate; порядок (по id)
        // совпадает со старым словарным, так что сидированный pick ниже
        // выбирает ту же точку.
        CollectExploreRing(world, npc, _exploreRingScratch);
        _exploreCandidates.Clear();
        foreach (var junction in _exploreRingScratch)
        {
            if (IsExploreCandidate(world, npc, junction))
            {
                _exploreCandidates.Add(junction);
            }
        }

        if (_exploreCandidates.Count == 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Explore);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Explore NoCandidateJunctions");

            }
            return;
        }

        if (preferPersonalSurvey) PreferUnsurveyedExploreTargets(world, npc);

        Junction destination = null;
        Faction visitFaction = default;
        TileCoord visitHome = default;
        var visiting = !preferPersonalSurvey && CampDiplomacyMath.TryFindVisitCamp(
            world, npc, out visitFaction, out visitHome);
        if (visiting)
        {
            var currentDistance = HexSpatialMath.HexDistance(npc.Tile, visitHome);
            var bestDistance = currentDistance;
            foreach (var candidate in _exploreCandidates)
            {
                var tile = candidate.Tiles.Count > 0 ? candidate.Tiles[0] : npc.Tile;
                var distance = HexSpatialMath.HexDistance(tile, visitHome);
                if (distance < bestDistance ||
                    (distance == bestDistance && destination is not null &&
                     candidate.Id.Value < destination.Id.Value))
                {
                    bestDistance = distance;
                    destination = candidate;
                }
            }

            // A local obstacle can make every legal 3..8-tile endpoint point
            // sideways or back. Fall through to ordinary wandering instead of
            // installing a visit leg that provably makes no progress.
            visiting = destination is not null;
        }

        if (destination is null)
        {
            var pick = (int)(MathUtil.Hash01(
                world.Seed, world.Tick, npc.Id.Value, 991) * _exploreCandidates.Count);
            pick = System.Math.Min(pick, _exploreCandidates.Count - 1);
            destination = _exploreCandidates[pick];
        }

        // Reachability check: destination must connect to where we stand.
        if (npc.CurrentJunction is not { } startJunction ||
            !Connectivity.Reachable(
                world, startJunction, destination.Id,
                ExploreMustAvoidDeepWater(npc)
                    ? CanUseCriticalTraversal(npc)
                    : CanUseRoutineTraversal(npc)))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Explore);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal=Explore Destination={destination.Id.Value} unreachable");
            }
            return;
        }

        npc.Plan.TargetJunctionId = destination.Id;
        npc.Plan.TargetTile = destination.Tiles.Count > 0 ? destination.Tiles[0] : null;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, visiting ? "CampVisitPlanned" : "ExplorePlanned",
                $"To Junction={destination.Id.Value} " +
                $"Tile={Trace.FormatTile(npc.Plan.TargetTile)} " +
                (visiting
                    ? $"Camp={visitFaction} Home={visitHome.Q},{visitHome.R} "
                    : string.Empty) +
                (preferPersonalSurvey
                    ? $"PersonalSurvey={npc.Memory.SurveyedTiles.Count} NewTiles={_exploreSurveyScores[HexSpatialMath.WorldToTile(destination.WorldPosition)].NewTiles} "
                    : string.Empty) +
                "Steps=[MoveToJunction]");
        }
    }

    // §50.9: «спуститься, пока ноги держат» — дойти до ближайшего узла САМОЙ
    // БОЛЬШОЙ плоской компоненты (большой земли), пока прыжок ещё возможен.
    // Дальше обычная жизнь: еда/вода/лечение планируются уже с материка.
    private void BuildReachSafeGroundPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from ||
            world.LargestFlatComponentId <= 0)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.ReachSafeGround);
            return;
        }

        // Ближайший узел материка по миру-расстоянию; достижимость — с её
        // РЕАЛЬНОЙ способностью. §57.11: без прыжка Reachable считает СПУСКИ
        // (направленно), так что и полностью обезноженная планирует сход вниз.
        var criticalSwimmer = SpatialQueries.IsSwimTile(world, npc.Tile) &&
            ExploreMustAvoidDeepWater(npc);
        JunctionId? best = criticalSwimmer
            ? FindCriticalSwimExit(world, npc)
            : null;
        var bestDistance = float.MaxValue;
        if (best is { } swimExit &&
            world.Junctions.Items.TryGetValue(swimExit, out var exitJunction))
        {
            bestDistance = HexSpatialMath.Distance(
                exitJunction.WorldPosition, npc.Position);
        }

        if (!criticalSwimmer)
        {
            // §158.4: ближайший свободный узел материка — кольцами от неё, а
            // не обходом карты плоских компонент (2.5 млн записей на «Островах»).
            Connectivity.EnsureFlat(world);
            var mainland = LocalSearch.FindNearest(
                world, npc.Tile, npc.Position, LocalSearch.NearestSearchMaxRadiusTiles,
                junction =>
                    !junction.Blocked &&
                    world.JunctionComponentsFlat.TryGetValue(junction.Id, out var component) &&
                    component == world.LargestFlatComponentId &&
                    SpatialQueries.IsJunctionFree(world, junction.Id));
            if (mainland is not null)
            {
                var d = HexSpatialMath.Distance(mainland.WorldPosition, npc.Position);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = mainland.Id;
                }
            }
        }

        if (best is not { } destination ||
            !Connectivity.Reachable(
                world, from, destination,
                CanUseCriticalTraversal(npc)))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.ReachSafeGround);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    "Goal=ReachSafeGround NoRouteToMainland");
            }
            return;
        }

        npc.Plan.TargetJunctionId = destination;
        npc.Plan.TargetTile = world.Junctions.Items[destination].Tiles.Count > 0
            ? world.Junctions.Items[destination].Tiles[0]
            : null;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=ReachSafeGround To Junction={destination.Value} " +
                $"Dist={bestDistance:F1} Steps=[MoveToJunction]");
        }
    }

    /// <summary>
    /// §140.2: маршрут к своему очагу. Цель — ближайший СВОБОДНЫЙ узел на
    /// домашнем тайле фракции; если весь тайл занят или недостижим, берём
    /// ближайший достижимый узел внутри лагеря. Отдельный план, а не вызов
    /// Explore с другой точкой: Explore выбирает случайный дальний узел и
    /// оценивает достижимость по «бытовому» проходу, а домой идёт та, у кого
    /// ног почти нет, — ей нужен тот же аварийный проход, что и §50.9
    /// (спуски по направленному графу, §57.11).
    /// </summary>
    internal void BuildHomewardPlan(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not { } from ||
            ColonyQueries.Home(world, npc.Faction) is not { } home)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Homeward);
            return;
        }

        JunctionId? best = null;
        var bestDistance = float.MaxValue;
        var homeCentre = HexSpatialMath.TileToWorld(home);
        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            if (junction.Blocked ||
                junction.Tiles.Count == 0 ||
                !ColonyQueries.InCamp(world, junction.Tiles[0], npc.Faction) ||
                !SpatialQueries.IsJunctionFree(world, pair.Key))
            {
                continue;
            }

            var d = HexSpatialMath.Distance(junction.WorldPosition, homeCentre);
            if (d >= bestDistance)
            {
                continue;
            }

            // Достижимость считается её РЕАЛЬНЫМ телом и последней — она дороже
            // расстояния, а кандидатов в лагере десятки.
            if (!Connectivity.Reachable(world, from, pair.Key, CanUseCriticalTraversal(npc)))
            {
                continue;
            }

            bestDistance = d;
            best = pair.Key;
        }

        if (best is not { } destination)
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Homeward);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Homeward NoRouteToCamp");
            }

            return;
        }

        npc.Plan.TargetJunctionId = destination;
        npc.Plan.TargetTile = world.Junctions.Items[destination].Tiles.Count > 0
            ? world.Junctions.Items[destination].Tiles[0]
            : null;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=Homeward To Junction={destination.Value} " +
                $"Dist={bestDistance:F1} Steps=[MoveToJunction]");
        }
    }

    private static bool HasTalkPartnerClaimedByOther(
        WorldState world, NPCState npc)
    {
        foreach (var perceived in npc.Perception.Agents)
        {
            if (world.Entities.Npcs.TryGetValue(perceived.Id, out var partner) &&
                partner.Mind.PendingTalkFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                return true;
            }
        }

        return false;
    }

    // Spec 28.15A: walk to a free neighbor junction of the target agent, then Talk.
    private void BuildTalkPlan(WorldState world, NPCState npc)
    {
        // Handshake (spec 28.8): if someone is already coming to talk to us,
        // wait for them instead of initiating our own approach.
        if (npc.Mind.PendingTalkFrom is { } incoming)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanNoInteraction",
                    $"Goal=Socialize WaitingForTalkFrom=NPC{incoming.Value}");
            }
            return;
        }

        // Spec 28.6 (iteration 8): prefer the most-liked available partner;
        // distance only breaks ties. Friendship self-selects.
        PerceivedAgent? target = null;
        foreach (var agent in npc.Perception.Agents)
        {
            if (!agent.IsReachable || agent.IsBusy || agent.IsMoving ||
                agent.IsUnconscious) // §60: never plan a chat with a body
            {
                continue;
            }

            if (agent.Junction is not { } candidateJunction ||
                !world.Entities.Npcs.TryGetValue(agent.Id, out var candidatePartner) ||
                !HasAvailableArmsLengthApproach(
                    world, npc, candidatePartner, candidateJunction))
            {
                continue;
            }

            // Skip targets already claimed by another initiator.
            if (world.Entities.Npcs.TryGetValue(agent.Id, out var agentState) &&
                agentState.Mind.PendingTalkFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (target is null ||
                agent.Relationship.Affinity > target.Relationship.Affinity + 0.01f ||
                (System.Math.Abs(agent.Relationship.Affinity - target.Relationship.Affinity) <= 0.01f &&
                 agent.Distance < target.Distance))
            {
                target = agent;
            }
        }

        if (target?.Junction is not { } targetJunction)
        {
            if (HasTalkPartnerClaimedByOther(world, npc))
            {
                npc.Plan.Status = PlanStatus.Completed;
                npc.Mind.CurrentGoal = GoalType.None;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanNoInteraction",
                        "Goal=Socialize PartnerAlreadyClaimed");
                }
                return;
            }

            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Socialize);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    "Goal=Socialize NoApproachableAgent");
            }
            return;
        }

        // Spec 28.8: talk at arm's length — a free junction ~0.9 hex radius
        // from the partner, on the initiator's side, not the adjacent
        // sub-grid point (that reads as standing inside each other).
        world.Entities.Npcs.TryGetValue(target.Id, out var partnerState);
        if (!TryInstallTalkPlan(
                world, npc, partnerState, target.Id, targetJunction, target.Tile,
                out var approachJunction))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Socialize);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal=Socialize Target={target.Id.Value} NoFreeApproachJunction");
            }
            return;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=Socialize Target=NPC{target.Id.Value} " +
                $"ApproachJunction={approachJunction.Value} Steps=[MoveToJunction,Talk]");
        }
    }

    /// <summary>§121.9: хвост установки плана разговора на УЖЕ выбранную цель —
    /// общий для автономного выбора (<see cref="BuildTalkPlan"/>) и ручного
    /// приказа (<c>TalkToCommand</c>): одна геометрия подхода, один claim,
    /// одни шаги, одна трасса <c>TalkRequested</c>. <paramref name="partner"/>
    /// может быть null (цели нет в мире живьём) — тогда подход строится без
    /// станций лежащей и claim не ставится, ровно как раньше.</summary>
    internal static bool TryInstallTalkPlan(
        WorldState world, NPCState npc, NPCState partner, EntityId targetId,
        JunctionId partnerJunction, TileCoord? partnerTile,
        out JunctionId approachJunction)
    {
        approachJunction = default;
        if (TryReserveArmsLengthApproach(world, npc, partner, partnerJunction)
            is not { } approach)
        {
            return false;
        }

        approachJunction = approach;
        npc.Plan.RequestedTalkTopic = null;
        npc.Plan.TargetAgentId = targetId;
        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = partnerTile;
        if (world.Entities.Npcs.TryGetValue(targetId, out var claimedTarget))
        {
            claimedTarget.Mind.PendingTalkFrom = npc.Id;
            claimedTarget.Mind.PendingTalkSinceTick = world.Tick;
            SocialCueSignals.Stamp(world, npc, "TalkRequest", targetId);
            SocialCueSignals.Stamp(world, claimedTarget, "TalkIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "TalkRequested",
                $"Asked NPC{targetId.Value} to talk " +
                $"Affinity={npc.Social.GetOrCreate(targetId).Affinity:F2}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approach,
            Interaction = InteractionType.Talk
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        return true;
    }

    // Spec §53: which aid interaction serves this kind of suffering.
    // Spec 28.8 / §53: reserve a free junction at arm's length (~0.9*R) from
    // the partner, on the initiator's side. The nearest-junction snap is
    // capped at InteractionReach.Aid — uncapped, a blocked/claimed grid around
    // the partner (a sufferer lying on a bed footprint, crowded camp) hands
    // back a junction a whole hex out and the talk/aid visibly runs at range.
    // Falls back to the partner junction's own passable neighbours (one
    // sub-grid step); null when nothing close is free.
    /// <summary>⭐ Дойдёт ли ОНА до этой точки. Подход к подопечной выбирался
    /// по одной геометрии — «рядом, свободно, забронировано», — и ни один из
    /// трёх выборщиков подхода (помощь, спасение, Defend) не спрашивал
    /// достижимость. Замер (seed 476005489, 16 000 тиков): 19 срывов
    /// «Aid approach blocked (no route)» и 6 «patient approach blocked» —
    /// помощница уходила к точке, куда пути нет, движение отвечало Blocked,
    /// поход отменялся, назначался снова. Мир без прыжка — половина острова
    /// (§50.7), так что раненой это стоило любой помощи вообще.</summary>
    private const int AidApproachExpansionBudget = 1500;

    private static bool CanWalkTo(
        WorldState world, NPCState npc, JunctionId candidate,
        System.Collections.Generic.HashSet<JunctionId> occupiedByActor)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return false;
        }

        var route = HexPathfinder.FindPath(
            world, from, candidate, occupiedByActor,
            weightClimb: true, canJump: CanUseRoutineTraversal(npc),
            danger: null, dangerCost: 0L,
            hardAvoid: DoorTopology.ForbiddenFor(world, npc.Faction),
            maxExpansions: AidApproachExpansionBudget);
        return route.Count > 0;
    }

    private static int BedsideRouteLength(
        WorldState world, NPCState npc, JunctionId candidate,
        System.Collections.Generic.HashSet<JunctionId> occupiedByActor)
    {
        if (npc.CurrentJunction is not { } from)
        {
            return int.MaxValue;
        }

        if (from.Equals(candidate))
        {
            return 0;
        }

        var route = HexPathfinder.FindPath(
            world, from, candidate, occupiedByActor,
            weightClimb: true, canJump: CanUseRoutineTraversal(npc),
            danger: null, dangerCost: 0L,
            hardAvoid: DoorTopology.ForbiddenFor(world, npc.Faction),
            maxExpansions: AidApproachExpansionBudget);
        return route.Count > 0 ? route.Count : int.MaxValue;
    }

    /// <summary>
    /// §53 r4: dry availability and the reserving planner use the same station,
    /// reach, occupancy and physical-route predicate. This prevents a visible
    /// bed-bound ward with no usable side from winning Aid every cooldown and
    /// failing immediately with NoFreeApproachJunction.
    /// </summary>
    internal static bool HasAvailableArmsLengthApproach(
        WorldState world, NPCState npc, NPCState partner, JunctionId partnerJunction) =>
        FindArmsLengthApproach(world, npc, partner, partnerJunction, reserve: false)
            is not null;

    private static JunctionId? TryReserveArmsLengthApproach(
        WorldState world, NPCState npc, NPCState partner, JunctionId partnerJunction) =>
        FindArmsLengthApproach(world, npc, partner, partnerJunction, reserve: true);

    private static JunctionId? FindArmsLengthApproach(
        WorldState world, NPCState npc, NPCState partner, JunctionId partnerJunction,
        bool reserve)
    {
        // IsJunctionFree covers explicit interaction occupancy, not the live
        // CurrentJunction of a walking/standing actor. Aid needs both: otherwise
        // it reserves a point already held by a third person and MovementSystem
        // politely re-paths to that exact same point forever.
        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, npc);
        if (partner is not null &&
            BedSleep.TryGetOccupiedBed(world, partner, out var supportBed))
        {
            return FindBedsideApproach(
                world, npc, partner, supportBed, occupiedByActor, reserve);
        }

        if (partner is not null && partner.IsLyingDown(world.Tick))
        {
            // Do not claim the first geometrically usable slot and only then
            // discover that its snapped node is blocked. Prove each station in
            // priority order; a chair at the feet must not hide a reachable
            // side station.
            for (var slot = 0; slot < LyingStations.Count; slot++)
            {
                if (!LyingStations.IsFree(world, partner, slot, npc) ||
                    !LyingStations.IsUsable(world, partner, slot))
                {
                    continue;
                }

                var stationSpot = LyingStations.Point(partner, slot);
                if (SpatialQueries.FindNearestJunction(world, stationSpot) is not { } station ||
                    !IsArmsLengthCandidate(
                        world, npc, partner, partnerJunction, station,
                        occupiedByActor, hasStation: true, stationSpot))
                {
                    continue;
                }

                if (!reserve)
                {
                    return station;
                }

                if (LyingStations.TryClaimSlot(world, npc, partner, slot) &&
                    SpatialMutations.TryReserveJunction(
                        world, station, npc.Id, world.Tick, 48))
                {
                    return station;
                }

                LyingStations.ReleaseStation(npc);
            }

            return null;
        }

        if (partner is not null)
        {
            var spot = partner.Position + HexSpatialMath.Normalize(new Float2(
                npc.Position.X - partner.Position.X,
                npc.Position.Y - partner.Position.Y)) * HexSpatialMath.HexRadius * 0.9f;
            if (SpatialQueries.FindNearestJunction(world, spot) is { } armsLength &&
                IsArmsLengthCandidate(
                    world, npc, partner, partnerJunction, armsLength,
                    occupiedByActor, hasStation: false, default) &&
                (!reserve || SpatialMutations.TryReserveJunction(
                    world, armsLength, npc.Id, world.Tick, 48)))
            {
                return armsLength;
            }
        }

        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, partnerJunction))
        {
            if (IsArmsLengthCandidate(
                    world, npc, partner, partnerJunction, neighbor,
                    occupiedByActor, hasStation: false, default) &&
                (!reserve || SpatialMutations.TryReserveJunction(
                    world, neighbor, npc.Id, world.Tick, 48)))
            {
                return neighbor;
            }
        }

        return null;
    }

    /// <summary>
    /// §53.4 r6: the five body-local stations sit over a mattress and therefore
    /// are not standing places. For an authoritative BedSleep occupant, walk to
    /// the furniture's ordinary free rim, then keep only points from which the
    /// patient's live body is within aid reach. Dry scoring and reservation use
    /// this same selection so an unavailable bedside never wins the auction.
    /// </summary>
    private static JunctionId? FindBedsideApproach(
        WorldState world, NPCState npc, NPCState patient, WorldObjectState bed,
        System.Collections.Generic.HashSet<JunctionId> occupiedByActor,
        bool reserve)
    {
        var candidates = world.Caches.ObjectApproachJunctionsScratch;
        SpatialQueries.CollectStandableAround(
            world, bed.Junctions[0], candidates, 96,
            SpatialQueries.BesideReach(LyingSpot.SolidRadius(world, bed)),
            bed, SpatialQueries.RimPurpose.Route);

        JunctionId? best = null;
        var bestRoute = int.MaxValue;
        var bestPatientDistance = float.MaxValue;
        foreach (var candidate in candidates)
        {
            if (!world.Junctions.Items.TryGetValue(candidate, out var junction) ||
                occupiedByActor.Contains(candidate) ||
                !JunctionAvailableFor(world, candidate, npc.Id) ||
                HexSpatialMath.Distance(junction.WorldPosition, patient.Position) >
                    InteractionReach.Aid ||
                !InteractionReach.CanTouchBedOccupantAcross(world, candidate, bed) ||
                world.Reservations.Junctions.TryGetValue(candidate, out var held) &&
                    held.Owner != npc.Id && held.EndTick >= world.Tick)
            {
                continue;
            }

            var routeLength = BedsideRouteLength(
                world, npc, candidate, occupiedByActor);
            if (routeLength == int.MaxValue)
            {
                continue;
            }

            var patientDistance = HexSpatialMath.Distance(
                junction.WorldPosition, patient.Position);
            if (routeLength < bestRoute ||
                routeLength == bestRoute && patientDistance < bestPatientDistance - 0.001f ||
                routeLength == bestRoute &&
                System.Math.Abs(patientDistance - bestPatientDistance) <= 0.001f &&
                (best is null || candidate.Value < best.Value.Value))
            {
                best = candidate;
                bestRoute = routeLength;
                bestPatientDistance = patientDistance;
            }
        }

        if (best is not { } bedside)
        {
            return null;
        }

        if (!reserve)
        {
            return bedside;
        }

        return SpatialMutations.TryReserveJunction(
            world, bedside, npc.Id, world.Tick, 48)
            ? bedside
            : null;
    }

    private static bool IsArmsLengthCandidate(
        WorldState world, NPCState npc, NPCState partner, JunctionId partnerJunction,
        JunctionId candidate,
        System.Collections.Generic.HashSet<JunctionId> occupiedByActor,
        bool hasStation, Float2 stationSpot)
    {
        if (candidate.Equals(partnerJunction) ||
            !world.Junctions.Items.TryGetValue(candidate, out var candidateJunction))
        {
            return false;
        }

        if (partner is not null &&
            (HexSpatialMath.Distance(candidateJunction.WorldPosition, partner.Position) >
                 InteractionReach.Aid ||
             !InteractionReach.CanTouchPersonAcross(
                 world, candidate, partnerJunction, InteractionReach.Aid)))
        {
            return false;
        }

        return !BlockedByActor(
                   occupiedByActor, candidate, candidateJunction,
                   hasStation, stationSpot) &&
               SpatialQueries.IsJunctionFree(world, candidate) &&
               CanWalkTo(world, npc, candidate, occupiedByActor);
    }

    /// <summary>
    /// ⭐ §111.13: СВОЯ БРОНЬ ЛЕЖАЩЕЙ НЕ МЕШАЕТ ТОМУ, КТО ПРИШЁЛ К НЕЙ.
    ///
    /// Узел под выбранной станцией почти всегда занят самим телом:
    /// <c>ClaimLyingFootprint</c> раздувает прямоугольник на полшага «на обход»,
    /// и все четыре боковые станции вместе с точкой у ног попадают в этот
    /// список, а список целиком лежит в avoid-множестве пути. Без исключения ни
    /// одна станция никогда не была бы забронирована — фича молча не работала бы.
    ///
    /// Исключение НАРОЧНО узкое: прощается ровно тот узел, на котором стоит моя
    /// станция, а не весь футпринт. Иначе подход разрешили бы в центр тела.
    /// Для всех прочих прохожих бронь остаётся стеной — лежащую не задевают.
    /// Фильтруем на месте вызова: множество из OtherActorJunctions общее на весь
    /// тик и мутировать его нельзя.
    /// </summary>
    private static bool BlockedByActor(
        System.Collections.Generic.HashSet<JunctionId> occupiedByActor,
        JunctionId candidate, Junction candidateJunction,
        bool hasStation, Float2 stationSpot)
    {
        if (!occupiedByActor.Contains(candidate))
        {
            return false;
        }

        if (!hasStation || candidateJunction is null)
        {
            return true;
        }

        // Полшага суб-сетки: ближе этого к станции лежит только её собственный узел.
        var tolerance = HexSpatialMath.HexRadius / HexPointLayout.BoundaryRadius * 0.5f;
        return HexSpatialMath.Distance(candidateJunction.WorldPosition, stationSpot) > tolerance;
    }

    // ⭐ §53/§111.9: ПОХОД ЗА ПОМОЩЬЮ ДОВОДИТСЯ, А НЕ ВЫБРАСЫВАЕТСЯ.
    //
    // План по ПАМЯТИ строится без живой подопечной (`partner = null`) — и это
    // намеренно: «у памяти нет честного ответа про достижимость, проверит
    // живая переоценка по прибытии». Но вместе с partner отключаются ОБЕ
    // проверки досягаемости и станция у ног лежащей: бронируется просто первый
    // свободный сосед запомненного узла. Для лежащей подопечной он почти
    // никогда не проходит боевой `InteractionReach.Aid`, и переоценка по
    // прибытии — единственная, кто это замечает, — поход просто отменяла.
    //
    // Получался вечный холостой круг: дошла, не дотянулась, отменила, кулдаун,
    // спланировала тот же поход снова. Замерено на seed 63287937: 28 из 59
    // походов на помощь (47%) не начинались вовсе, «Target out of aid range» —
    // самая частая причина; к npc3 так сходили девять раз за 6000 тиков, пока
    // она умирала в двух шагах (баг #117, нашёлся при разборе #115).
    //
    // Поэтому по прибытии сначала ПЕРЕПРИЦЕЛИВАНИЕ: подопечная теперь перед
    // глазами, значит геометрию можно пересчитать честно — той же общей
    // формулой, но уже с живым телом, то есть со станцией у ног, если она
    // лежит. Не видно её отсюда — забыть узел (а не страдание): «пришла, где
    // помнила, там пусто, где она теперь — не знаю». Память без узла походов
    // больше не притягивает и починится сама при следующей встрече.
    internal static bool TryRetargetAidOnArrival(
        WorldState world, NPCState npc, NPCState target)
    {
        var seen = false;
        foreach (var agent in npc.Perception.Agents)
        {
            if (agent.Id.Equals(target.Id) && agent.CanSee)
            {
                seen = true;
                break;
            }
        }

        if (!seen || target.CurrentJunction is not { } targetJunction)
        {
            if (npc.Memory.KnownAgents.TryGetValue(target.Id, out var stale))
            {
                stale.Junction = null;
            }

            return false;
        }

        // Старую бронь отпустить до новой: иначе узел, на котором она стоит,
        // остаётся за ней же и блокирует собственный пересчёт.
        if (npc.Plan.TargetJunctionId is { } held)
        {
            SpatialMutations.ReleaseJunctionReservation(world, held, npc.Id);
        }

        if (TryReserveArmsLengthApproach(world, npc, target, targetJunction)
            is not { } approach)
        {
            return false;
        }

        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = target.Tile;
        npc.Plan.Steps.Clear();
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            Interaction = AidInteraction(AidAssessment.Assess(target, world.Tick, out _))
        });

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "AidRetargeted",
                $"NPC{target.Id.Value} was not at the remembered spot — " +
                $"walking to her actual station (Junction={approach.Value})");
        }
        return true;
    }

    private static InteractionType AidInteraction(AidKind kind) => kind switch
    {
        AidKind.Feed => InteractionType.FeedOther,
        AidKind.Hydrate => InteractionType.HydrateOther,
        AidKind.Treat => InteractionType.TreatOther,
        AidKind.Medicate => InteractionType.MedicateOther,
        _ => InteractionType.ConsoleOther
    };

    // Spec §53: walk to a suffering housemate and help. Mirrors BuildTalkPlan
    // but selects the WORST-OFF reachable neighbour (highest Suffering) rather
    // than the most-liked, and claims her with PendingAidFrom so she holds still.
    /// <summary>§125.7: лучшая подопечная ПО ПАМЯТИ — та, кого она не видит, но
    /// помнит раненой. Фильтры те же, что в ставке решения, иначе цель выиграла
    /// бы аукцион и развалилась в планировщике (грабля HasUsableCoconut).
    /// Достижимость и занятость не спрашиваются: у памяти нет на них честного
    /// ответа, а проверит их живая переоценка по прибытии.</summary>
    private static bool TryRememberedWard(
        WorldState world, NPCState npc, out RememberedAgent best)
    {
        best = null;
        foreach (var remembered in npc.Perception.Remembered)
        {
            if (remembered.AidKind == AidKind.None ||
                remembered.Age > Spec53.AidMemoryMaxAgeTicks ||
                remembered.Suffering < Spec53.SufferingThreshold ||
                remembered.Junction is null ||
                !AidSupply.Has(world, npc, remembered.AidKind))
            {
                continue;
            }

            if (world.Entities.Npcs.TryGetValue(remembered.Id, out var liveWard) &&
                remembered.Junction is { } rememberedJunction &&
                !HasAvailableArmsLengthApproach(
                    world, npc, liveWard, rememberedJunction))
            {
                continue;
            }

            // Уже идёт другая — не ходить вдвоём по одной вере.
            if (world.Entities.Npcs.TryGetValue(remembered.Id, out var wardState) &&
                wardState.Mind.PendingAidFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (best is null || remembered.Suffering > best.Suffering + 0.001f ||
                (System.Math.Abs(remembered.Suffering - best.Suffering) <= 0.001f &&
                 remembered.Id.Value < best.Id.Value))
            {
                best = remembered;
            }
        }

        return best is not null;
    }

    private static bool HasWardClaimedByOther(WorldState world, NPCState npc)
    {
        foreach (var perceived in npc.Perception.Agents)
        {
            if (perceived.AidKind != AidKind.None &&
                world.Entities.Npcs.TryGetValue(perceived.Id, out var ward) &&
                ward.Mind.PendingAidFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                return true;
            }
        }

        foreach (var remembered in npc.Perception.Remembered)
        {
            if (remembered.AidKind != AidKind.None &&
                world.Entities.Npcs.TryGetValue(remembered.Id, out var ward) &&
                ward.Mind.PendingAidFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                return true;
            }
        }

        return false;
    }

    private void BuildAidPlan(WorldState world, NPCState npc)
    {
        // If someone is already coming to help US, don't set off ourselves.
        if (npc.Mind.PendingAidFrom is { } incoming)
        {
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.CurrentGoal = GoalType.None;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanNoInteraction",
                    $"Goal=Aid WaitingForAidFrom=NPC{incoming.Value}");
            }
            return;
        }

        PerceivedAgent? target = null;
        foreach (var agent in npc.Perception.Agents)
        {
            // §53.8: тот же смягчённый фильтр, что в ставке решения (иначе
            // цель выиграла бы аукцион и развалилась здесь): критическая
            // подопечная выбирается и в движении — ползущую к воде умирающую
            // догоняет живой ретаргет по прибытии.
            if (agent.AidKind == AidKind.None || agent.Suffering < Spec53.SufferingThreshold ||
                !agent.IsReachable || (agent.IsBusy && !agent.IsDying) ||
                (agent.IsMoving && !agent.IsDying &&
                 agent.Suffering < Spec53.HeavyAidSuffering))
            {
                continue;
            }

            if (agent.Junction is not { } candidateJunction ||
                !world.Entities.Npcs.TryGetValue(agent.Id, out var candidateWard) ||
                !HasAvailableArmsLengthApproach(
                    world, npc, candidateWard, candidateJunction))
            {
                continue;
            }

            // §53.7: help costs supplies — never set out to a ward whose need
            // we cannot pay for. The decision layer turns that case into a
            // fetch errand instead; walking over empty-handed would only abort
            // on arrival and freeze her in the wait.
            if (!AidSupply.Has(world, npc, agent.AidKind))
            {
                continue;
            }

            // Skip a sufferer another helper is already on the way to.
            if (world.Entities.Npcs.TryGetValue(agent.Id, out var agentState) &&
                agentState.Mind.PendingAidFrom is { } claimedBy &&
                !claimedBy.Equals(npc.Id))
            {
                continue;
            }

            if (target is null || agent.Suffering > target.Suffering + 0.001f ||
                (System.Math.Abs(agent.Suffering - target.Suffering) <= 0.001f &&
                 agent.Distance < target.Distance))
            {
                target = agent;
            }
        }

        // §125.7: ПО ПАМЯТИ. Никого не видно — но она может помнить, что дома
        // осталась раненая подруга, и имеет право пойти проверить. Живая цель
        // всегда в приоритете: сюда попадаем, только когда видимой нет.
        var fromMemory = false;
        EntityId targetId;
        AidKind targetKind;
        TileCoord targetTile;
        float targetSuffering;
        JunctionId targetJunction;

        if (target?.Junction is { } liveJunction)
        {
            targetId = target.Id;
            targetKind = target.AidKind;
            targetTile = target.Tile;
            targetSuffering = target.Suffering;
            targetJunction = liveJunction;
        }
        else if (TryRememberedWard(world, npc, out var remembered) &&
                 remembered.Junction is { } rememberedJunction)
        {
            fromMemory = true;
            targetId = remembered.Id;
            targetKind = remembered.AidKind;
            targetTile = remembered.Tile;
            targetSuffering = remembered.Suffering;
            targetJunction = rememberedJunction;
        }
        else
        {
            // Decision evaluates all NPCs before Planning reserves targets.
            // Two helpers can therefore honestly choose the same ward in one
            // auction pass; the first planner claims her and the second sees
            // no candidate. That is a resolved race, not a path/planning
            // failure and must not become a 40-tick Aid retry rhythm.
            if (HasWardClaimedByOther(world, npc))
            {
                npc.Plan.Status = PlanStatus.Completed;
                npc.Mind.CurrentGoal = GoalType.None;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "PlanNoInteraction",
                        "Goal=Aid WardAlreadyClaimed");
                }
                return;
            }

            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Aid);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=Aid NoReachableSufferer");

            }
            return;
        }

        // Help at arm's length — a free junction ~0.9 hex radius from her, on
        // our side (same geometry as a talk approach). У цели по памяти тела на
        // месте может и не быть — подход строится от запомненного узла.
        world.Entities.Npcs.TryGetValue(targetId, out var partnerState);
        if (fromMemory)
        {
            partnerState = null;
        }

        if (!TryInstallAidPlan(
                world, npc, partnerState, targetId, targetKind, targetTile,
                targetSuffering, targetJunction, fromMemory, out var approachJunction))
        {
            npc.Plan.Status = PlanStatus.Failed;
            SetGoalCooldown(world, npc, GoalType.Aid);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal=Aid Target={targetId.Value} NoFreeApproachJunction");
            }
            return;
        }

        // NB (замерено, не чинится намеренно): обычный замок цели держится
        // GoalLockTicks = 24 тика, дальше помощь перебивается SwitchDelta =
        // 0.15. Выглядит как дыра — «бросила помощь ради болтовни», 16 случаев
        // на seed 63287937, — но по данным она бьёт ТОЛЬКО лёгкие походы:
        // Console 0.47…0.60, Feed/Hydrate 0.56…0.74. Ни один поход Treat, в том
        // числе все шесть с Suffering=1.00, не был брошен ни до правки #117, ни
        // после. Усиливать замок для тяжёлой помощи означало бы охранять то,
        // чего в данных нет; проверка стоит здесь, чтобы это не пришлось
        // выяснять заново.
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=Aid Kind={targetKind} Target=NPC{targetId.Value} " +
                $"Suffering={targetSuffering:F2} ApproachJunction={approachJunction.Value} " +
                $"FromMemory={(fromMemory ? 1 : 0)} Steps=[MoveToJunction,{AidInteraction(targetKind)}]");
        }
    }

    /// <summary>§121.9: хвост установки плана помощи на УЖЕ выбранную подопечную —
    /// общий для автономного выбора (<see cref="BuildAidPlan"/>) и ручного
    /// приказа (<c>AidPersonCommand</c>). <paramref name="partner"/> может быть
    /// null (цель по памяти): подход строится от запомненного узла, значки
    /// видимой пары не ставятся — ровно прежнее поведение.</summary>
    internal static bool TryInstallAidPlan(
        WorldState world, NPCState npc, NPCState partner, EntityId targetId,
        AidKind targetKind, TileCoord? targetTile, float targetSuffering,
        JunctionId targetJunction, bool fromMemory, out JunctionId approachJunction)
    {
        approachJunction = default;
        if (TryReserveArmsLengthApproach(world, npc, partner, targetJunction)
            is not { } approach)
        {
            return false;
        }

        approachJunction = approach;
        var interaction = AidInteraction(targetKind);
        npc.Plan.TargetAgentId = targetId;
        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = targetTile;
        if (world.Entities.Npcs.TryGetValue(targetId, out var claimedTarget))
        {
            claimedTarget.Mind.PendingAidFrom = npc.Id;
            claimedTarget.Mind.PendingAidSinceTick = world.Tick;
            // Значки «просит помощи» / «к тебе идут» — про ВИДИМУЮ пару: по
            // памяти обе стороны друг друга не видят, и рисовать им реплику
            // было бы враньём вида.
            if (!fromMemory)
            {
                SocialCueSignals.Stamp(world, npc, "AidRequest", targetId);
                SocialCueSignals.Stamp(world, claimedTarget, "AidIncoming", npc.Id);
            }

            Trace.Emit(world, npc.Id, "AidRequested",
                $"Going to help NPC{targetId.Value} Kind={targetKind} " +
                $"Suffering={targetSuffering:F2} FromMemory={(fromMemory ? 1 : 0)}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approach,
            Interaction = interaction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        return true;
    }

    private void BuildDefendPlan(WorldState world, NPCState npc)
    {
        JunctionId? attackerJunction = null;
        TileCoord attackerTile = npc.Tile;
        var label = string.Empty;
        var dogEngaged = false;
        NPCState humanAttacker = null;

        if (npc.Mind.CombatAssistDogId is { } dogId)
        {
            foreach (var dog in world.Mobs)
            {
                if (dog.Id == dogId && dog.Health > 0f)
                {
                    attackerJunction = dog.Junction;
                    attackerTile = dog.Tile;
                    label = $"Dog={dog.Id}";
                    dogEngaged = dog.Status == Wildlife.MobStatus.Fighting;
                    break;
                }
            }
        }
        else if (npc.Mind.CombatAssistAttackerNpcId is { } attackerId &&
                 world.Entities.Npcs.TryGetValue(attackerId, out var attacker) &&
                 attacker.Health > 0f && !attacker.IsUnconscious(world.Tick) &&
                 !attacker.Body.IsProne)
        {
            humanAttacker = attacker;
            attackerJunction = attacker.CurrentJunction;
            attackerTile = attacker.Tile;
            label = $"Attacker=NPC{attacker.Id.Value}";
        }

        if (attackerJunction is not { } target)
        {
            npc.Plan.Status = PlanStatus.Failed;
            HumanCombatPairing.ClearFor(world, npc);
            CombatHelpSystem.ClearAssist(npc);
            Trace.Emit(world, npc.Id, "HelpCryAssistLost", "Attacker vanished before defender arrived");
            return;
        }

        if (humanAttacker != null && !CombatMedium.NpcMelee(world, npc, humanAttacker))
        {
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Defend);
            HumanCombatPairing.ClearFor(world, npc);
            CombatHelpSystem.ClearAssist(npc);
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "HelpCryAssistLost",
                $"{label} combat medium changed — standing down");
            return;
        }

        // Dogs still use their own exchange. Human assists use the common
        // Approach/Act contract below: adjacency alone ignored combat medium.
        var dogOnStation = humanAttacker == null && npc.CurrentJunction is { } current &&
            (current.Equals(target) ||
             (world.Junctions.Items.TryGetValue(target, out var targetJ) &&
              targetJ.Neighbors.Contains(current)));

        // §29C.4B assist give-up: the GoalLock stamped when the assist was
        // taken (help cry / friend guard / §62 first strike) is the whole
        // budget. Before, NOTHING ended an assist while the mob lived — a
        // defender parked beside an unreachable standoff wolf, or trailing a
        // roaming one, stayed locked in Defend forever (DecisionSystem skips
        // the auction while CombatAssist* is set, so needs never broke in
        // either). A LIVE exchange (mob actually Fighting with her on
        // station) extends past the lock; the moment it isn't, she stands
        // down and Defend goes on cooldown so the auction doesn't re-enter.
        var lockExpired = npc.Mind.GoalLock is not { } assistLock ||
            assistLock.Goal != GoalType.Defend ||
            world.Tick >= assistLock.EndTick;
        var humanCanAct = humanAttacker != null &&
            InteractionReach.AssessMelee(
                world, npc, humanAttacker, sceneStarted: false,
                $"Defend NPC{humanAttacker.Id.Value}") == MeleeApproach.Act;
        var engaged = humanCanAct || (dogOnStation && dogEngaged);
        if (lockExpired && !engaged)
        {
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Defend);
            HumanCombatPairing.ClearFor(world, npc);
            CombatHelpSystem.ClearAssist(npc);
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "HelpCryAssistExpired",
                $"{label} unresolved after the assist window — standing down");
            return;
        }

        if (humanCanAct)
        {
            npc.Mind.AssistHoldSinceTick = 0;
            HumanCombatPairing.EngageAssist(world, npc, humanAttacker);
            // EngageAssist installs the combat pairing; it is not a completed
            // movement job that needs rebuilding next medium tick. Keep one
            // bounded active hold while HumanCombatSystem owns the exchange.
            // Seed 632 otherwise emitted Engaged + a new empty Defend plan on
            // five consecutive passes before the first scene resolved.
            var holdUntil = npc.Mind.GoalLock is { } humanLock &&
                humanLock.Goal == GoalType.Defend
                    ? System.Math.Max(world.Tick + 4, humanLock.EndTick)
                    : world.Tick + 4;
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Wait,
                TimeoutEndTick = holdUntil
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "HelpCryAssistEngaged",
                    $"{label} Reach=Act Reply=NPC{humanAttacker.Mind.CombatOpponentNpcId?.Value ?? -1} " +
                    $"HoldUntil={holdUntil}");
            }
            return;
        }

        // §29C.4B dog on-station hold: she is where the fight needs her; the old
        // code still built a 1-step move plan TO HER OWN JUNCTION, which
        // completed instantly and re-planned every pass (Started→Arrived 17
        // times in 68 ticks, seed 521091321 day 30). Strikes never came from
        // the plan — RunDogDefenders/PredationSystem read only the goal and
        // adjacency — so the right plan here is NO plan: stand and wait.
        if (dogOnStation)
        {
            var holdUntil = npc.Mind.GoalLock is { } dogLock &&
                dogLock.Goal == GoalType.Defend
                    ? System.Math.Max(world.Tick + 4, dogLock.EndTick)
                    : world.Tick + 4;
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Wait,
                TimeoutEndTick = holdUntil
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            if (npc.Mind.AssistHoldSinceTick == 0)
            {
                npc.Mind.AssistHoldSinceTick = world.Tick;
                Trace.Emit(world, npc.Id, "HelpCryAssistHolding",
                    $"{label} on station — waiting for the exchange");
            }
            return;
        }

        npc.Mind.AssistHoldSinceTick = 0;

        var approach = FindDefendApproach(world, npc, target, reserve: true);

        if (approach is not { } approachJunction)
        {
            // The attacker can move after the help event. Do not keep the
            // assist latch alive when the exact route has disappeared: while
            // CombatAssist* is present DecisionSystem deliberately skips its
            // auction, and RallyFriends runs again every medium pass.
            PlanningSystem.SetGoalCooldown(world, npc, GoalType.Defend);
            HumanCombatPairing.ClearFor(world, npc);
            CombatHelpSystem.ClearAssist(npc);
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "HelpCryAssistLost",
                $"{label} has no reachable free approach — standing down");
            return;
        }

        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = attackerTile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        SocialCueSignals.Stamp(world, npc,
            npc.Mind.CombatAssistDogId.HasValue ? "HelpCryAssistStarted:dog" : "HelpCryAssistStarted:npc",
            null);
        Trace.Emit(world, npc.Id, "HelpCryAssistStarted",
            $"{label} ApproachJunction={approachJunction.Value} Tile={attackerTile.Q},{attackerTile.R}");
    }

    /// <summary>Exact non-mutating availability shared by combat event
    /// assignment and the reserving planner. A coarse hex radius is not a
    /// route: cliffs and occupied sub-grid junctions can separate actors that
    /// are visually close.</summary>
    internal static bool HasReachableDefendApproach(
        WorldState world, NPCState npc, JunctionId attackerJunction)
    {
        if (npc.CurrentJunction is not { } current)
        {
            return false;
        }

        if (current.Equals(attackerJunction) ||
            (world.Junctions.Items.TryGetValue(attackerJunction, out var target) &&
             target.Neighbors.Contains(current)))
        {
            return true;
        }

        return FindDefendApproach(world, npc, attackerJunction, reserve: false) is not null;
    }

    private static JunctionId? FindDefendApproach(
        WorldState world, NPCState npc, JunctionId attackerJunction, bool reserve)
    {
        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, npc);
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, attackerJunction))
        {
            if (occupiedByActor.Contains(neighbor) ||
                !SpatialQueries.IsJunctionFree(world, neighbor) ||
                !CanWalkTo(world, npc, neighbor, occupiedByActor))
            {
                continue;
            }

            if (!reserve || SpatialMutations.TryReserveJunction(
                    world, neighbor, npc.Id, world.Tick, 48))
            {
                return neighbor;
            }
        }

        return null;
    }
}

}
