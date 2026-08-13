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

    private void BuildExplorePlan(WorldState world, NPCState npc)
    {
        _exploreCandidates.Clear();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked)
            {
                continue;
            }

            var tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : npc.Tile;
            var distance = HexSpatialMath.HexDistance(npc.Tile, tile);
            if (distance is < 3 or > 8)
            {
                continue;
            }

            // Spec 29C.4A: avoid places where we were recently attacked.
            var dangerous = false;
            foreach (var danger in npc.Memory.Dangers)
            {
                if (HexSpatialMath.HexDistance(tile, danger.Tile) <= 3)
                {
                    dangerous = true;
                    break;
                }
            }

            if (!dangerous)
            {
                // §50.9: кандидат обязан быть достижим ЕЙ — иначе ползущая
                // (полкарты вне её мира) проваливала 9 из 10 выборов и часами
                // крутила PlanFailed вместо прогулки по своему берегу.
                if (npc.CurrentJunction is { } exploreFrom &&
                    !Connectivity.Reachable(world, exploreFrom, junction.Id, npc.Body.CanJump))
                {
                    continue;
                }

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

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 991) * _exploreCandidates.Count);
        pick = System.Math.Min(pick, _exploreCandidates.Count - 1);
        var destination = _exploreCandidates[pick];

        // Reachability check: destination must connect to where we stand.
        if (npc.CurrentJunction is not { } startJunction ||
            !Connectivity.Reachable(world, startJunction, destination.Id, npc.Body.CanJump))
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
            Trace.Debug(world, npc.Id, "ExplorePlanned",
                $"To Junction={destination.Id.Value} " +
                $"Tile={Trace.FormatTile(npc.Plan.TargetTile)} Steps=[MoveToJunction]");
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
        JunctionId? best = null;
        var bestDistance = float.MaxValue;
        foreach (var pair in world.JunctionComponentsFlat)
        {
            if (pair.Value != world.LargestFlatComponentId ||
                !world.Junctions.Items.TryGetValue(pair.Key, out var junction) ||
                junction.Blocked)
            {
                continue;
            }

            var d = HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (d < bestDistance && SpatialQueries.IsJunctionFree(world, pair.Key))
            {
                bestDistance = d;
                best = pair.Key;
            }
        }

        if (best is not { } destination ||
            !Connectivity.Reachable(world, from, destination, npc.Body.CanJump))
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
        var approach = TryReserveArmsLengthApproach(world, npc, partnerState, targetJunction);

        if (approach is not { } approachJunction)
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

        npc.Plan.TargetAgentId = target.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = target.Tile;
        if (world.Entities.Npcs.TryGetValue(target.Id, out var claimedTarget))
        {
            claimedTarget.Mind.PendingTalkFrom = npc.Id;
            claimedTarget.Mind.PendingTalkSinceTick = world.Tick;
            SocialCueSignals.Stamp(world, npc, "TalkRequest", target.Id);
            SocialCueSignals.Stamp(world, claimedTarget, "TalkIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "TalkRequested",
                $"Asked NPC{target.Id.Value} to talk " +
                $"Affinity={npc.Social.GetOrCreate(target.Id).Affinity:F2}");
        }

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = InteractionType.Talk
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=Socialize Target=NPC{target.Id.Value} " +
                $"ApproachJunction={approachJunction.Value} Steps=[MoveToJunction,Talk]");
        }
    }

    // Spec §53: which aid interaction serves this kind of suffering.
    // Spec 28.8 / §53: reserve a free junction at arm's length (~0.9*R) from
    // the partner, on the initiator's side. The nearest-junction snap is
    // capped at InteractionReach.Aid — uncapped, a blocked/claimed grid around
    // the partner (a sufferer lying on a bed footprint, crowded camp) hands
    // back a junction a whole hex out and the talk/aid visibly runs at range.
    // Falls back to the partner junction's own passable neighbours (one
    // sub-grid step); null when nothing close is free.
    private static JunctionId? TryReserveArmsLengthApproach(
        WorldState world, NPCState npc, NPCState partner, JunctionId partnerJunction)
    {
        // IsJunctionFree covers explicit interaction occupancy, not the live
        // CurrentJunction of a walking/standing actor. Aid needs both: otherwise
        // it reserves a point already held by a third person and MovementSystem
        // politely re-paths to that exact same point forever.
        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, npc);
        if (partner is not null)
        {
            // §111.9: a lying ward has an absolute care/search station at her
            // feet. Standing partners retain the old caller-side approach.
            var spot = partner.IsLyingDown(world.Tick)
                ? LyingSpot.InteractionFeet(partner)
                : partner.Position + HexSpatialMath.Normalize(new Float2(
                    npc.Position.X - partner.Position.X,
                    npc.Position.Y - partner.Position.Y)) * HexSpatialMath.HexRadius * 0.9f;
            if (SpatialQueries.FindNearestJunction(world, spot) is { } armsLength &&
                !armsLength.Equals(partnerJunction) &&
                world.Junctions.Items.TryGetValue(armsLength, out var armsJct) &&
                HexSpatialMath.Distance(armsJct.WorldPosition, partner.Position) <=
                    InteractionReach.Aid &&
                InteractionReach.CanTouchPersonAcross(
                    world, armsLength, partnerJunction, InteractionReach.Aid) &&
                !occupiedByActor.Contains(armsLength) &&
                SpatialQueries.IsJunctionFree(world, armsLength) &&
                SpatialMutations.TryReserveJunction(world, armsLength, npc.Id, world.Tick, 48))
            {
                return armsLength;
            }
        }

        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, partnerJunction))
        {
            // The partner's own resolved junction can itself sit far from her
            // body (she may lie inside a blocked footprint cluster), so its
            // neighbours must pass the same reach cap or the walk is doomed —
            // the execution gate would abort it on arrival anyway.
            if (partner is not null &&
                (!world.Junctions.Items.TryGetValue(neighbor, out var nJct) ||
                 HexSpatialMath.Distance(nJct.WorldPosition, partner.Position) >
                     InteractionReach.Aid ||
                 !InteractionReach.CanTouchPersonAcross(
                     world, neighbor, partnerJunction, InteractionReach.Aid)))
            {
                continue;
            }

            if (!occupiedByActor.Contains(neighbor) &&
                SpatialQueries.IsJunctionFree(world, neighbor) &&
                SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
            {
                return neighbor;
            }
        }

        return null;
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

        Trace.Emit(world, npc.Id, "AidRetargeted",
            $"NPC{target.Id.Value} was not at the remembered spot — " +
            $"walking to her actual station (Junction={approach.Value})");
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
                !agent.IsReachable || agent.IsBusy ||
                (agent.IsMoving && !agent.IsDying &&
                 agent.Suffering < Spec53.HeavyAidSuffering))
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

        var approach = TryReserveArmsLengthApproach(world, npc, partnerState, targetJunction);

        if (approach is not { } approachJunction)
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

        var interaction = AidInteraction(targetKind);
        npc.Plan.TargetAgentId = targetId;
        npc.Plan.TargetJunctionId = approachJunction;
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
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = interaction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;

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
                $"FromMemory={(fromMemory ? 1 : 0)} Steps=[MoveToJunction,{interaction}]");
        }
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
            npc.Plan.Status = PlanStatus.Completed;
            npc.Mind.AssistHoldSinceTick = 0;
            HumanCombatPairing.EngageAssist(world, npc, humanAttacker);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "HelpCryAssistEngaged",
                    $"{label} Reach=Act Reply=NPC{humanAttacker.Mind.CombatOpponentNpcId?.Value ?? -1}");
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
            npc.Plan.Status = PlanStatus.Completed;
            if (npc.Mind.AssistHoldSinceTick == 0)
            {
                npc.Mind.AssistHoldSinceTick = world.Tick;
                Trace.Emit(world, npc.Id, "HelpCryAssistHolding",
                    $"{label} on station — waiting for the exchange");
            }
            return;
        }

        npc.Mind.AssistHoldSinceTick = 0;

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
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal=Defend {label} Reach=Approach NoFreeApproachJunction — will replan");
            }
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
}

}
