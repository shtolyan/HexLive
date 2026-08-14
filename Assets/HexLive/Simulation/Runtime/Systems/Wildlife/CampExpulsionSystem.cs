using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §115: хозяин лагеря подходит к враждебному чужаку, требует уйти и
/// только после отказа открывает FightScene. Система идёт после MobSystem,
/// потому что тот каждый Medium-проход сбрасывает IsFighting.
/// </summary>
public sealed class CampExpulsionSystem : ISimulationSystem
{
    private const int ApproachPhase = 0;
    private const int DemandPhase = 1;
    private const int FightPhase = 2;

    public string Name => nameof(CampExpulsionSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        if (!Spec82.TerritorialEnabled || !Spec72.Enabled)
        {
            return;
        }

        ClearOrphanClaims(world);

        // Сначала доиграть живущие сцены, затем заводить новые. Порядок id
        // делает выбор стабильным и не протаскивает порядок Dictionary в реплей.
        foreach (var owner in world.Entities.Npcs.Values
                     .Where(n => n.Mind.CurrentGoal == GoalType.Expel &&
                                 n.Mind.ExpulsionTargetNpcId is not null)
                     .OrderBy(n => n.Id.Value).ToList())
        {
            Advance(world, owner);
        }

        foreach (var owner in world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList())
        {
            if (!CanOwnChallenge(world, owner))
            {
                continue;
            }

            var intruder = BestIntruder(world, owner);
            if (intruder is not null)
            {
                BeginChallenge(world, owner, intruder);
            }
        }
    }

    internal static void BeginChallenge(WorldState world, NPCState owner, NPCState intruder)
    {
        // §121: сцена выгона — разговор с ролями и таймингом; ручному участнику
        // она бы отобрала управление на обеих сторонах (хозяин идёт сам,
        // чужака сцена держит на месте). Есть ручной — сцены нет; выгонять
        // чужака игрок волен приказом атаки.
        if (ManualControlMath.IsManual(owner) || ManualControlMath.IsManual(intruder))
        {
            return;
        }

        if (owner.Plan.Status == PlanStatus.Active ||
            owner.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.TryAbort(world, owner, InterruptionCause.SceneInitiator, $"Expelling NPC{intruder.Id.Value}");
        }

        owner.Mind.CurrentGoal = GoalType.Expel;
        owner.Mind.ExpulsionTargetNpcId = intruder.Id;
        owner.Mind.ExpulsionPhase = ApproachPhase;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick;
        owner.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Expel,
            StartTick = world.Tick,
            EndTick = world.Tick + Spec82.TerritoryFightTimeoutTicks + 240
        };
        intruder.Mind.PendingExpulsionFrom = owner.Id;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, owner.Id, "CampExpelStarted",
                $"Intruder=NPC{intruder.Id.Value} Camp={owner.Faction} Health={intruder.Health:F2}");
        }
    }

    internal static void Advance(WorldState world, NPCState owner)
    {
        if (owner.Mind.ExpulsionTargetNpcId is not { } intruderId ||
            !world.Entities.Npcs.TryGetValue(intruderId, out var intruder))
        {
            Finish(world, owner, intruder: null, "ParticipantGone", protectIntruder: false);
            return;
        }

        if (owner.Health <= 0f || owner.IsUnconscious(world.Tick) || owner.Body.IsProne)
        {
            Finish(world, owner, intruder, "OwnerDown", protectIntruder: true);
            return;
        }

        if (!FactionRelations.AreHostile(owner.Faction, intruder.Faction))
        {
            Finish(world, owner, intruder, "NoLongerHostile", protectIntruder: false);
            return;
        }

        if (owner.Mind.ExpulsionPhase < FightPhase &&
            (!world.FactionHomes.ContainsKey(owner.Faction) ||
             !ColonyQueries.InCamp(world, intruder.Tile, owner.Faction)))
        {
            Finish(world, owner, intruder, "IntruderLeftCamp", protectIntruder: false);
            return;
        }

        switch (owner.Mind.ExpulsionPhase)
        {
            case ApproachPhase:
                AdvanceApproach(world, owner, intruder);
                break;
            case DemandPhase:
                AdvanceDemand(world, owner, intruder);
                break;
            case FightPhase:
                AdvanceFight(world, owner, intruder);
                break;
            default:
                Finish(world, owner, intruder, "InvalidPhase", protectIntruder: false);
                break;
        }
    }

    private static void AdvanceApproach(WorldState world, NPCState owner, NPCState intruder)
    {
        if (!InteractionReach.CanStrike(world, owner, intruder))
        {
            return;
        }

        StopForScene(world, intruder, owner.Id);
        owner.Mind.ExpulsionPhase = DemandPhase;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick;
        intruder.Mind.CurrentGoal = GoalType.Expel;
        intruder.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Expel,
            StartTick = world.Tick,
            EndTick = world.Tick + Spec82.TerritoryFightTimeoutTicks + 120
        };
        SocialCueSignals.Stamp(world, owner, "CampExpelDemand", intruder.Id);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, owner.Id, "CampExpelDemanded",
                $"Intruder=NPC{intruder.Id.Value} Health={intruder.Health:F2}");
        }
    }

    internal static void AdvanceDemand(WorldState world, NPCState owner, NPCState intruder)
    {
        if (world.Tick - owner.Mind.ExpulsionPhaseStartedTick <
            Spec82.TerritoryResponseDelayTicks)
        {
            return;
        }

        // Не только «хочет уступить», но и реально может уйти: нет пути домой —
        // нет ложного «окей», остаётся драться.
        if (intruder.Health < Spec82.TerritorySubmitHealth &&
            MobSystem.TryFleeToCamp(world, intruder,
                $"Agreed to leave NPC{owner.Id.Value}'s camp"))
        {
            SocialCueSignals.Stamp(world, intruder, "CampExpelAccepted", owner.Id);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, owner.Id, "CampExpelAccepted",
                    $"Intruder=NPC{intruder.Id.Value} Health={intruder.Health:F2}");
            }
            Finish(world, owner, intruder, "Accepted", protectIntruder: false,
                keepIntruderGoal: true);
            return;
        }

        SocialCueSignals.Stamp(world, intruder, "CampExpelRefused", owner.Id);
        StopForScene(world, intruder, owner.Id);
        owner.Mind.SceneStartHealth = owner.Health;
        intruder.Mind.SceneStartHealth = intruder.Health;
        intruder.Mind.SceneBlowsPlanned = 0;
        intruder.Mind.AbuseBlows = 0;
        FightScene.Begin(world, owner, intruder, weaponId: null,
            blows: Spec82.TerritoryMaxBlows,
            spacingClips: Spec82.TerritoryBlowSpacingClips);
        intruder.Mind.CombatOpponentNpcId = owner.Id;
        intruder.IsFighting = true;
        owner.Mind.ExpulsionPhase = FightPhase;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, owner.Id, "CampExpelFightStarted",
                $"Intruder=NPC{intruder.Id.Value} Blows={Spec82.TerritoryMaxBlows}");
        }
    }

    internal static void AdvanceFight(WorldState world, NPCState owner, NPCState intruder)
    {
        FightScene.Latch(world, owner, intruder);

        var incapacitated = owner.Health <= 0f || intruder.Health <= 0f ||
            owner.IsUnconscious(world.Tick) || intruder.IsUnconscious(world.Tick) ||
            owner.Body.IsProne || intruder.Body.IsProne;
        var completed = FightScene.IsComplete(owner) &&
            world.Tick >= owner.Mind.SceneLastBlowRestTick;
        var timedOut = world.Tick - owner.Mind.ExpulsionPhaseStartedTick >=
            Spec82.TerritoryFightTimeoutTicks;
        if (!incapacitated && !completed && !timedOut)
        {
            return;
        }

        var ownerDamage = System.Math.Max(0f, owner.Mind.SceneStartHealth - owner.Health);
        var intruderDamage = System.Math.Max(0f, intruder.Mind.SceneStartHealth - intruder.Health);
        var anyDamage = ownerDamage > 0.0001f || intruderDamage > 0.0001f;
        // Ненулевая ничья — в пользу лагеря. Нулевой таймаут оставляет чужака.
        var intruderLost = anyDamage && intruderDamage >= ownerDamage;

        FightScene.End(world, owner, intruder);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, owner.Id, "CampExpelFightResolved",
                $"Intruder=NPC{intruder.Id.Value} OwnerDamage={ownerDamage:F3} " +
                $"IntruderDamage={intruderDamage:F3} Result=" +
                (intruderLost ? "Expelled" : anyDamage ? "IntruderHeld" : "NoDamage"));
        }

        if (intruderLost)
        {
            SocialCueSignals.Stamp(world, intruder, "CampExpelFled", owner.Id);
            var fled = intruder.Health > 0f && !intruder.IsUnconscious(world.Tick) &&
                !intruder.Body.IsProne &&
                MobSystem.TryFleeToCamp(world, intruder,
                    $"Lost expulsion fight to NPC{owner.Id.Value}");
            Finish(world, owner, intruder, "IntruderLost", protectIntruder: false,
                keepIntruderGoal: fled);

            // Загнанный в угол не исчезает и не считается изгнанным: сцена
            // переходит в обычный налёт и продолжает бой.
            if (!fled && intruder.Health > 0f && !intruder.IsUnconscious(world.Tick) &&
                !intruder.Body.IsProne && owner.Health > 0f)
            {
                RaidSystem.StartRaidOn(world, owner, intruder, "ExpulsionCornered");
            }

            return;
        }

        SocialCueSignals.Stamp(world, intruder, "CampExpelHeld", owner.Id);
        SocialCueSignals.Stamp(world, owner, "CampExpelFailed", intruder.Id);
        Finish(world, owner, intruder, anyDamage ? "IntruderWon" : "NoDamage",
            protectIntruder: true);
    }

    private static NPCState BestIntruder(WorldState world, NPCState owner)
    {
        NPCState best = null;
        var bestDistance = int.MaxValue;
        foreach (var npc in world.Entities.Npcs.Values.OrderBy(n => n.Id.Value))
        {
            if (npc.Id.Equals(owner.Id) || npc.Health <= 0f ||
                npc.IsUnconscious(world.Tick) || npc.IsPlayingDead(world.Tick) ||
                npc.Body.IsProne || npc.Mind.CurrentGoal is GoalType.Flee or GoalType.Expel ||
                GoalCatalog.IsReactive(npc.Mind.CurrentGoal) ||
                npc.Mind.CurrentGoal is GoalType.Prey or GoalType.LootHelpless ||
                npc.IsFighting || npc.Mind.PendingExpulsionFrom is not null ||
                npc.Mind.PendingAbuseFrom is not null ||
                npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                world.Tick < npc.Mind.ExpulsionProtectedUntilTick ||
                !FactionRelations.AreHostile(owner.Faction, npc.Faction) ||
                !ColonyQueries.InCamp(world, npc.Tile, owner.Faction))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(owner.Tile, npc.Tile);
            if (distance > Spec82.TerritoryRadiusTiles ||
                owner.CurrentJunction is not { } from ||
                npc.CurrentJunction is not { } to ||
                !Connectivity.Reachable(world, from, to, owner.Body.CanJump))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = npc;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static bool CanOwnChallenge(WorldState world, NPCState owner)
    {
        return world.FactionHomes.ContainsKey(owner.Faction) &&
            owner.Health > 0f && !owner.IsUnconscious(world.Tick) &&
            !owner.IsPlayingDead(world.Tick) && !owner.Body.IsProne &&
            owner.Execution.CurrentInteraction != InteractionType.Sleep &&
            !owner.IsFighting && world.Tick >= owner.Mind.TerritoryCooldownUntilTick &&
            owner.Mind.PendingExpulsionFrom is null &&
            owner.Mind.ExpulsionTargetNpcId is null &&
            owner.Mind.PendingAbuseFrom is null &&
            !GoalCatalog.IsReactive(owner.Mind.CurrentGoal) &&
            owner.Mind.CurrentGoal is not (GoalType.Prey or GoalType.LootHelpless) &&
            owner.CurrentJunction is not null &&
            ColonyQueries.InCamp(world, owner.Tile, owner.Faction);
    }

    private static void StopForScene(WorldState world, NPCState npc, EntityId peerId)
    {
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress ||
            npc.Movement.IsMoving ||
            npc.IsCarryingPerson)
        {
            PlanInterruption.TryAbortForCombat(
                world, npc, InterruptionCause.ScenePact, $"Camp expulsion with NPC{peerId.Value}");
        }

        npc.Plan.Goal = GoalType.Expel;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Movement.IsMoving = false;
    }

    private static void ClearOrphanClaims(WorldState world)
    {
        foreach (var intruder in world.Entities.Npcs.Values)
        {
            if (intruder.Mind.PendingExpulsionFrom is not { } ownerId)
            {
                continue;
            }

            if (world.Entities.Npcs.TryGetValue(ownerId, out var owner) &&
                owner.Mind.CurrentGoal == GoalType.Expel &&
                owner.Mind.ExpulsionTargetNpcId is { } targetId &&
                targetId.Equals(intruder.Id))
            {
                continue;
            }

            intruder.Mind.PendingExpulsionFrom = null;
            intruder.Mind.ExpulsionProtectedUntilTick =
                world.Tick + Spec82.TerritoryCooldownTicks;
            if (intruder.Mind.CurrentGoal == GoalType.Expel)
            {
                intruder.Mind.CurrentGoal = GoalType.None;
                intruder.Mind.GoalLock = null;
            }
            intruder.Mind.CombatOpponentNpcId = null;
            intruder.IsFighting = false;
            FightScene.ReleaseSwingSlot(intruder);
        }
    }

    private static void Finish(WorldState world, NPCState owner, NPCState intruder,
        string reason, bool protectIntruder, bool keepIntruderGoal = false)
    {
        if (owner != null)
        {
            if (owner.Plan.Status == PlanStatus.Active || owner.Movement.IsMoving)
            {
                PlanInterruption.TryAbort(world, owner, InterruptionCause.SceneInitiator, $"Expulsion ended: {reason}");
            }
            else if (owner.Plan.TargetJunctionId is { } reserved)
            {
                SpatialMutations.ReleaseJunctionReservation(world, reserved, owner.Id);
            }

            owner.Mind.ExpulsionTargetNpcId = null;
            owner.Mind.ExpulsionPhase = 0;
            owner.Mind.ExpulsionPhaseStartedTick = 0;
            owner.Mind.TerritoryCooldownUntilTick =
                world.Tick + Spec82.TerritoryCooldownTicks;
            owner.Mind.CombatOpponentNpcId = null;
            owner.Mind.GoalLock = null;
            owner.IsFighting = false;
            FightScene.ReleaseSwingSlot(owner);
            if (owner.Mind.CurrentGoal == GoalType.Expel)
            {
                owner.Mind.CurrentGoal = GoalType.None;
            }
            owner.Plan.Status = PlanStatus.Completed;
            owner.Plan.Steps.Clear();
            owner.Plan.TargetJunctionId = null;
            owner.Plan.TargetTile = null;
        }

        if (intruder != null)
        {
            intruder.Mind.PendingExpulsionFrom = null;
            if (protectIntruder)
            {
                intruder.Mind.ExpulsionProtectedUntilTick =
                    world.Tick + Spec82.TerritoryCooldownTicks;
            }
            intruder.Mind.CombatOpponentNpcId = null;
            intruder.IsFighting = false;
            FightScene.ReleaseSwingSlot(intruder);
            if (keepIntruderGoal &&
                intruder.Mind.GoalLock is { } oldExpelLock &&
                oldExpelLock.Goal == GoalType.Expel)
            {
                intruder.Mind.GoalLock = null;
            }
            if (!keepIntruderGoal && intruder.Mind.CurrentGoal == GoalType.Expel)
            {
                intruder.Mind.CurrentGoal = GoalType.None;
                intruder.Mind.GoalLock = null;
                intruder.Plan.Status = PlanStatus.Completed;
                intruder.Plan.Steps.Clear();
            }
        }

        if (owner != null)
        {
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, owner.Id, "CampExpelEnded", $"Reason={reason}");

            }
        }
    }
}

}
