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

// §72: the shape of a raid — who is locked onto whom, who rallies, who runs,
// who dies. The blows themselves are HumanCombatSystem's (Fast layer); this is
// the MobSystem half of the same split.
//
// Almost nothing here is new machinery. The victim's side is the §29C.4A/§29C.4B
// response the colony already gives a wolf: remember the danger, cry for help,
// pull the friend-guard, bolt below a health floor, or stand and swing back —
// and every girl who took the Defend goal against this attacker piles in. What
// §72 adds is the attacker being a person, and knowing when he has had enough.
public sealed class RaidSystem : ISimulationSystem
{
    public string Name => nameof(RaidSystem);

    public TickLayer Layer => TickLayer.Medium;

    private readonly System.Collections.Generic.List<EntityId> _dead = new();

    public void Run(WorldState world)
    {
        if (!Spec72.Enabled)
        {
            return;
        }

        _dead.Clear();

        // Safety net. A human fight resolves on the FAST layer, so it can kill
        // between medium passes; MobSystem's own sweep (which removes EVERY
        // 0-health NPC, not just its dogs) runs just before this and normally
        // gets them first. This catches anyone it did not.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f && npc.Mind.CombatOpponentNpcId is not null)
            {
                CollectDead(world, npc, _dead);
            }
        }

        foreach (var raider in world.Entities.Npcs.Values)
        {
            if (raider.Mind.CurrentGoal != GoalType.Raid ||
                raider.Health <= 0f ||
                raider.Mind.RaidTargetNpcId is not { } victimId ||
                !world.Entities.Npcs.TryGetValue(victimId, out var victim))
            {
                continue;
            }

            if (victim.Health <= 0f)
            {
                CollectDead(world, victim, _dead);
                PlanningSystem.AbandonRaid(world, raider, "VictimDown");
                raider.IsFighting = false;
                continue;
            }

            if (!MeleeSwing.InReach(world, raider, victim))
            {
                // Still stalking — the plan walks him in. Drop the pairing so
                // nobody swings at thin air across the island.
                Unpair(raider);
                Unpair(victim);
                continue;
            }

            // --- Contact. -----------------------------------------------------
            if (raider.Mind.CombatOpponentNpcId is null)
            {
                Trace.Emit(world, raider.Id, "RaidEngaged",
                    $"Victim=NPC{victim.Id.Value} " +
                    $"Weapon={WeaponLabel(raider)} VictimWeapon={WeaponLabel(victim)}");
            }

            raider.IsFighting = true;
            raider.Mind.CombatOpponentNpcId = victim.Id;

            // --- Her side: the standard §29C response to being attacked. ------
            MobSystem.RememberDanger(world, victim);
            CombatHelpSystem.RallyFriends(world, victim, null, raider.Id,
                $"Outsider=NPC{raider.Id.Value}");
            CombatHelpSystem.CallForHelpFromNpc(world, victim, raider.Id, 1);

            var defenders = PullDefenders(world, raider, victim);

            // §60: a body that cannot act does not flee and does not swing.
            // Her friends still rally to her above — that is the whole point.
            if (victim.IsUnconscious(world.Tick) || victim.Body.IsProne)
            {
                Unpair(victim);
            }
            else
            {
                var fleeing = victim.Mind.CurrentGoal == GoalType.Flee;
                if (!fleeing &&
                    (victim.Health < Spec72.RaidVictimFleeHealth ||
                     MobSystem.WorstPartHealth(victim) < 0.35f))
                {
                    fleeing = MobSystem.TryStartFlee(world, victim, 1, attackerNpcId: raider.Id);
                }

                if (fleeing)
                {
                    Unpair(victim);
                    Trace.Emit(world, victim.Id, "RaidVictimFled",
                        $"From=NPC{raider.Id.Value} Health={victim.Health:F2}");
                }
                else
                {
                    // Stand and fight: drop the chores, square up, swing back.
                    if (victim.Plan.Status == PlanStatus.Active ||
                        victim.Execution.Status == ExecutionStatus.InProgress)
                    {
                        PlanInterruption.Abort(world, victim, $"Fighting off NPC{raider.Id.Value}");
                        victim.Mind.CurrentGoal = GoalType.None;
                    }

                    victim.IsFighting = true;
                    victim.Mind.CombatOpponentNpcId = raider.Id;
                }
            }

            // --- His side: does he keep at it? --------------------------------
            if (raider.Health < Spec72.RaidFleeHealth ||
                MobSystem.WorstPartHealth(raider) < 0.35f ||
                defenders >= Spec72.RaidBreakOffDefenders)
            {
                Trace.Emit(world, raider.Id, "RaidBrokeOff",
                    $"Health={raider.Health:F2} Defenders={defenders} " +
                    $"Reason={(defenders >= Spec72.RaidBreakOffDefenders ? "Outnumbered" : "Wounded")}");
                BreakOff(world, raider, victim);
            }
        }

        // A raid that ended while the raider was already gone (killed by a
        // defender's Fast-layer swing between medium passes) still has to clear
        // the assists, or every girl sits in a Defend goal-lock forever.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CombatAssistAttackerNpcId is { } attackerId &&
                (!world.Entities.Npcs.TryGetValue(attackerId, out var attacker) ||
                 attacker.Health <= 0f ||
                 attacker.Mind.CurrentGoal != GoalType.Raid))
            {
                CombatHelpSystem.ClearAssist(npc);
                npc.Mind.CombatOpponentNpcId = null;
                npc.IsFighting = false;
            }
        }

        foreach (var deadId in _dead)
        {
            MobSystem.RemoveDeadNpc(world, deadId);
        }
    }

    private static string WeaponLabel(NPCState npc)
    {
        var id = npc.Body.CanUseToolsOrWeapons
            ? SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands)
            : string.Empty;
        return string.IsNullOrEmpty(id) ? "fists" : id;
    }

    private static void Unpair(NPCState npc)
    {
        npc.Mind.CombatOpponentNpcId = null;
        npc.StrikeLandsAtTick = 0;
    }

    // Every ally who answered the cry and is standing next to him joins the
    // exchange. They do not deal damage here — HumanCombatSystem gives each of
    // them a properly timed swing, so three defenders are three swings on three
    // separate clocks, not three instant hits in one tick.
    private static int PullDefenders(WorldState world, NPCState raider, NPCState victim)
    {
        var defenders = 0;
        foreach (var defender in world.Entities.Npcs.Values)
        {
            if (defender.Id.Equals(raider.Id) || defender.Id.Equals(victim.Id) ||
                defender.Health <= 0f ||
                defender.IsUnconscious(world.Tick) ||
                defender.Body.IsProne ||
                defender.Mind.CurrentGoal != GoalType.Defend ||
                defender.Mind.CombatAssistAttackerNpcId is not { } assistId ||
                !assistId.Equals(raider.Id) ||
                !MeleeSwing.InReach(world, defender, raider))
            {
                continue;
            }

            defenders++;
            defender.IsFighting = true;
            defender.Mind.CombatOpponentNpcId = raider.Id;
            SocialCueSignals.Stamp(world, defender, "HelpCryDefended", victim.Id);
        }

        return defenders;
    }

    // He has had enough: drop the hunt, take the cooldown, and walk home. NOT
    // MobSystem.TryStartFlee — that runs for the nearest INDOOR junction, which
    // standing in the girls' yard is their own hut.
    private static void BreakOff(WorldState world, NPCState raider, NPCState victim)
    {
        Unpair(raider);
        Unpair(victim);
        raider.IsFighting = false;
        victim.IsFighting = false;
        MobSystem.RememberDanger(world, raider);
        PlanningSystem.AbandonRaid(world, raider, "BrokeOff");
    }

    private static void CollectDead(
        WorldState world, NPCState npc, System.Collections.Generic.List<EntityId> dead)
    {
        if (!dead.Contains(npc.Id))
        {
            dead.Add(npc.Id);
        }
    }
}

}
