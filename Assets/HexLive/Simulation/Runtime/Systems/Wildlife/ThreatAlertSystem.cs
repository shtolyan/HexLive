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

// Spec §62: far threat detection. Dogs smell a girl at 2 tiles; she SEES the
// dog at Spec62.SpotRadiusTiles and reacts before contact. On a fresh sighting
// a ⚠️ cue pops over her head, then one of two branches:
//   FIT (every body part >= 80%, nothing severed, a real melee weapon she can
//   swing, the threat is alone) — she attacks first: the existing Defend
//   machinery walks her to the mob and the ordinary melee exchange resolves
//   the fight, with her at full strength instead of ambushed mid-haul.
//   UNFIT (wounded, prone, bare-handed, starving, or it's a pack) — the mob's
//   tile goes into danger memory (§29C.4A producer bias) and a route that
//   passes the §62 danger ring is torn up; the rebuild detours via the soft
//   ring cost in HexPathfinder.
// Reactive melee, flee assessment and help cries are untouched — this system
// only ever acts BEFORE the chase starts.
public sealed class ThreatAlertSystem : ISimulationSystem
{
    public string Name => nameof(ThreatAlertSystem);

    public TickLayer Layer => TickLayer.Medium;

    // Re-warn gate per (girl, mob). Deliberately transient system state, NOT
    // NPCState — the save format stays untouched; a loaded save at worst
    // re-pops one ⚠️ per pair.
    private readonly System.Collections.Generic.Dictionary<long, int> _lastCueTick = new();

    public void Run(WorldState world)
    {
        if (!Spec62.ThreatAlertEnabled)
        {
            return;
        }

        if (world.Mobs.Count == 0)
        {
            if (_lastCueTick.Count > 0)
            {
                _lastCueTick.Clear();
            }

            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f ||
                npc.IsUnconscious(world.Tick) ||
                npc.Execution.CurrentInteraction == InteractionType.Sleep ||
                npc.IsFighting ||
                npc.Mind.CurrentGoal == GoalType.Flee ||
                npc.Mind.CurrentGoal == GoalType.Defend ||
                MobSystem.IsNpcInSanctuary(world, npc))
            {
                continue;
            }

            // Nearest live threat in sight, plus how many share the radius —
            // a pack in view is never a first-strike target.
            Wildlife.MobState threat = null;
            var bestDistance = int.MaxValue;
            var pack = 0;
            foreach (var mob in world.Mobs)
            {
                if (mob.Health <= 0f)
                {
                    continue;
                }

                var distance = HexSpatialMath.HexDistance(npc.Tile, mob.Tile);
                if (distance > Spec62.SpotRadiusTiles)
                {
                    continue;
                }

                pack++;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    threat = mob;
                }
            }

            if (threat is null)
            {
                continue;
            }

            var key = ((long)npc.Id.Value << 32) | (uint)threat.Id;
            if (_lastCueTick.TryGetValue(key, out var lastTick) &&
                world.Tick - lastTick < Spec62.CueCooldownTicks)
            {
                continue;
            }

            _lastCueTick[key] = world.Tick;
            PruneStaleCues(world.Tick);

            SocialCueSignals.Stamp(world, npc, "DangerSpotted", npc.Id);
            var fit = IsFitToFight(npc) && pack <= Spec62.AttackMaxPack;
            Trace.Emit(world, npc.Id, "ThreatSpotted",
                $"Mob={threat.Id} Dist={bestDistance} Pack={pack} Fit={fit} " +
                $"WorstPart={MobSystem.WorstPartHealth(npc):F2}");

            if (fit)
            {
                StartFirstStrike(world, npc, threat);
            }
            else
            {
                AvoidThreat(world, npc, threat);
            }
        }
    }

    // §62 fitness: "no significant wounds" = every body part at 80%+ and no
    // stump, "armed" = a melee weapon she can actually swing right now (fists
    // never qualify; a spear with one hand doesn't either — BestMeleeWeapon
    // already skips two-handed gear she can't hold). Starving or dehydrated
    // girls have bigger problems than picking fights.
    internal static bool IsFitToFight(NPCState npc)
    {
        if (npc.Body.AnySevered ||
            !npc.Body.CanUseToolsOrWeapons ||
            npc.Mind.IsStarving ||
            npc.Mind.IsDehydrated ||
            MobSystem.WorstPartHealth(npc) < Spec62.FitBoneHealth)
        {
            return false;
        }

        var weaponId = SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands);
        return Content.GearCatalog.For(weaponId).MeleePriority > 0;
    }

    // The pre-emptive attack rides the help-cry Defend machinery unchanged:
    // BuildDefendPlan walks her to a junction adjacent to the mob, the dog
    // aggros on approach, and the standard melee exchange (where she is the
    // healthy, armed side) settles it.
    private static void StartFirstStrike(WorldState world, NPCState npc, Wildlife.MobState threat)
    {
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, npc, $"Attacking spotted dog {threat.Id} first");
        }

        npc.Mind.CurrentGoal = GoalType.Defend;
        npc.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Defend,
            StartTick = world.Tick,
            EndTick = world.Tick + Spec62.AttackLockTicks
        };
        npc.Mind.CombatAssistDogId = threat.Id;
        npc.Mind.CombatAssistAttackerNpcId = null;
        npc.Mind.PendingTalkFrom = null;
        npc.Mind.PendingAidFrom = null;

        // §52: both hands on the spear before the charge, not at first blood.
        if (npc.Body.IntactHands >= 2 &&
            Content.GearCatalog.For(SimBalance.BestMeleeWeapon(
                npc.Inventory.Items, npc.Body.IntactHands)).TwoHanded)
        {
            MobSystem.ReadySpearHands(world, npc);
        }

        Trace.Emit(world, npc.Id, "ThreatAttack",
            $"Mob={threat.Id} first strike (fit and armed)");
    }

    private static void AvoidThreat(WorldState world, NPCState npc, Wildlife.MobState threat)
    {
        MobSystem.RememberDangerAt(world, npc, threat.Tile);
        if (npc.Plan.Status != PlanStatus.Active)
        {
            return;
        }

        // Tear up a route that passes the danger ring; the rebuild pathfinds
        // with the §62 soft cost and detours. Only on the fresh sighting (the
        // cue gate above), so a genuinely unavoidable crossing is not aborted
        // again every medium tick.
        var ring = PathfindingSystem.DangerRing(world);
        for (var i = npc.Movement.PathIndex; i < npc.Movement.JunctionPath.Count; i++)
        {
            if (!ring.Contains(npc.Movement.JunctionPath[i]))
            {
                continue;
            }

            Trace.Emit(world, npc.Id, "ThreatAvoid",
                $"Mob={threat.Id} Tile={threat.Tile.Q},{threat.Tile.R} rerouting");
            PlanInterruption.Abort(world, npc,
                $"Route passes spotted dog {threat.Id} — rerouting");
            return;
        }
    }

    private void PruneStaleCues(int tick)
    {
        if (_lastCueTick.Count <= 64)
        {
            return;
        }

        var stale = new System.Collections.Generic.List<long>();
        foreach (var pair in _lastCueTick)
        {
            if (tick - pair.Value >= Spec62.CueCooldownTicks)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var key in stale)
        {
            _lastCueTick.Remove(key);
        }
    }
}

}
