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

// §56 Predation cannibalism: resolves the KILL half of "kill a housemate to eat
// them" (the EAT half is the existing §54 Butcher→meat→Eat chain). A predator is
// an NPC whose DecisionSystem picked GoalType.Prey — already gated to a starving,
// low-compassion survivor with no softer food. This system deals the strikes:
// once adjacent to the weakest housemate it wounds them each tick via WoundMath
// until they fall, then routes the death through the shared RemoveDeadNpc (which
// spawns the corpse and makes witnesses grieve) and applies the heavy social
// fallout. Mirrors MobSystem/RabbitSystem's adjacency-strike + death-sweep shape.
public sealed class PredationSystem : ISimulationSystem
{
    public string Name => nameof(PredationSystem);

    public TickLayer Layer => TickLayer.Medium;

    private readonly System.Collections.Generic.List<EntityId> _deadVictims = new();
    private readonly System.Collections.Generic.List<EntityId> _deadAttackers = new();

    public void Run(WorldState world)
    {
        if (!SimBalance.PredationEnabled)
        {
            return;
        }

        _deadVictims.Clear();
        _deadAttackers.Clear();
        foreach (var predator in world.Entities.Npcs.Values)
        {
            if (predator.Mind.CurrentGoal != GoalType.Prey ||
                predator.CurrentJunction is not { } predJunction)
            {
                continue;
            }

            // Strike whichever living housemate we're adjacent to — the pursued
            // weakest ends up here. Adjacency = same or neighbouring junction.
            NPCState? victim = null;
            foreach (var other in world.Entities.Npcs.Values)
            {
                if (other.Id == predator.Id || other.Health <= 0f ||
                    other.CurrentJunction is not { } otherJunction)
                {
                    continue;
                }

                var adjacent = predJunction.Equals(otherJunction) ||
                    (world.Junctions.Items.TryGetValue(otherJunction, out var oj) &&
                     oj.Neighbors.Contains(predJunction));
                if (adjacent && (victim is null || other.Health < victim.Health))
                {
                    victim = other;
                }
            }

            if (victim is null)
            {
                continue;
            }

            // A starved attacker is weak (StrikeFactor); weapons bite deeper
            // while heavy weapons strike less often.
            var weaponId = predator.Body.CanUseToolsOrWeapons
                ? SimBalance.BestMeleeWeapon(predator.Inventory.Items, predator.Body.IntactHands)
                : string.Empty;
            var weaponMult = SimBalance.MeleeStrikeBonus(weaponId);
            var attackSpeed = SimBalance.MeleeAttackSpeed(weaponId);
            if (!SimBalance.MeleeStrikeReady(world.Tick, predator.Id.Value, weaponId))
            {
                Trace.Emit(world, predator.Id, "PreyWindup",
                    $"Victim={victim.Id.Value} Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)} " +
                    $"Speed={attackSpeed:F1}");
                continue;
            }

            // Apply the strike exactly like a dog's bite (SimulationSystems dog
            // path): garment on the struck part absorbs, the part loses HP,
            // Health is the body mean, the hit files a wound decal, and a
            // destroyed vital is instant death. This mirrors the established
            // damage flow so the kill is detectable in the same tick.
            var part = AmputateSystemHelpers.RedirectFromStump(victim,
                PickKillPart(world, predator.Id.Value));
            var partArmor = EquipmentMath.ArmorForPart(world, victim, part);
            var damage = SimBalance.PredationStrikePerPass *
                predator.Body.StrikeFactor() * weaponMult * (1f - partArmor);
            victim.Body.Parts[part] = System.Math.Max(0f, victim.Body.Parts[part] - damage);
            victim.Health = victim.Body.Mean();
            DamageReactionSystemHelpers.GrantAdrenaline(world, victim, damage, "PredationStrike");
            WoundMath.Inflict(world, victim, part, damage);
            if (victim.Body.VitalDestroyed(out _))
            {
                victim.Health = 0f;
            }

            Trace.Emit(world, predator.Id, "Preyed",
                $"Victim={victim.Id.Value} {part} -{damage:F3} (armor={partArmor:F2}) " +
                $"Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)} Speed={attackSpeed:F1} " +
                $"VictimHealth={victim.Health:F2}");

            if (victim.Health <= 0f)
            {
                if (!_deadVictims.Contains(victim.Id))
                {
                    _deadVictims.Add(victim.Id);
                    ApplyKillConsequences(world, predator, victim);
                }

                continue; // the victim is down — no defence this tick
            }

            // --- The victim DEFENDS: this is a real fight, not an execution.
            // It reuses the dog-fight model (Spec 29C.4A) — remember the danger,
            // then either BOLT for an indoor refuge if badly hurt, or STAND and
            // trade a real blow at the attacker. An armed, healthy victim can
            // wound, kill, or outrun a starved predator — so predation can fail.
            MobSystem.RememberDanger(world, victim);
            CombatHelpSystem.RallyFriends(world, victim, null, predator.Id,
                $"Attacker=NPC{predator.Id.Value}");

            // Spec §60: a comatose victim lies senseless — no flight, no
            // counter-blow. Friends still rally to her defence above.
            if (victim.IsUnconscious(world.Tick))
            {
                continue;
            }

            var victimFleeing = victim.Mind.CurrentGoal == GoalType.Flee;
            if (!victimFleeing &&
                (victim.Health < SimBalance.PredationFleeHealth ||
                 MobSystem.WorstPartHealth(victim) < 0.35f))
            {
                victimFleeing = MobSystem.TryStartFlee(world, victim, 1, attackerNpcId: predator.Id);
            }

            if (victimFleeing)
            {
                RunNpcDefenders(world, predator, victim);
                if (predator.Health <= 0f && !_deadAttackers.Contains(predator.Id))
                {
                    _deadAttackers.Add(predator.Id);
                    Trace.EmitSystem(world, "PredatorKilled",
                        $"NPC{victim.Id.Value}'s helpers killed attacker NPC{predator.Id.Value}");
                }

                Trace.Emit(world, victim.Id, "PreyFled",
                    $"From NPC{predator.Id.Value} (Health={victim.Health:F2})");
                continue;
            }

            // Stand and fight back — drop the chores, swing at the attacker.
            victim.IsFighting = true;
            if (victim.Plan.Status == PlanStatus.Active ||
                victim.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, victim, $"Fighting off NPC{predator.Id.Value}");
                victim.Mind.CurrentGoal = GoalType.None;
            }

            var defWeaponId = victim.Body.CanUseToolsOrWeapons
                ? SimBalance.BestMeleeWeapon(victim.Inventory.Items, victim.Body.IntactHands)
                : string.Empty;
            var defWeapon = SimBalance.MeleeStrikeBonus(defWeaponId);
            var defAttackSpeed = SimBalance.MeleeAttackSpeed(defWeaponId);
            var defStrikeReady = SimBalance.MeleeStrikeReady(world.Tick, victim.Id.Value, defWeaponId);

            // The counter-blow uses the same NPC strike-back value that fends off
            // dogs, scaled by the defender's own StrikeFactor()/weapon and the
            // attacker's armor.
            var defPart = PickKillPart(world, victim.Id.Value + 7919);
            var defArmor = EquipmentMath.ArmorForPart(world, predator, defPart);
            var defDamage = defStrikeReady
                ? SimBalance.NpcStrikePerPass * victim.Body.StrikeFactor() * defWeapon * (1f - defArmor)
                : 0f;
            if (defDamage > 0f)
            {
                predator.Body.Parts[defPart] = System.Math.Max(0f, predator.Body.Parts[defPart] - defDamage);
                predator.Health = predator.Body.Mean();
                DamageReactionSystemHelpers.GrantAdrenaline(world, predator, defDamage, "PreyCounterStrike");
                WoundMath.Inflict(world, predator, defPart, defDamage);
                if (predator.Body.VitalDestroyed(out _))
                {
                    predator.Health = 0f;
                }
            }

            Trace.Emit(world, victim.Id, "PreyFoughtBack",
                $"Attacker=NPC{predator.Id.Value} {defPart} -{defDamage:F3} " +
                $"Weapon={(string.IsNullOrEmpty(defWeaponId) ? "fists" : defWeaponId)}" +
                $"{(defStrikeReady ? string.Empty : " recovering")} Speed={defAttackSpeed:F1} " +
                $"AttackerHealth={predator.Health:F2}");

            RunNpcDefenders(world, predator, victim);

            if (predator.Health <= 0f && !_deadAttackers.Contains(predator.Id))
            {
                _deadAttackers.Add(predator.Id);
                Trace.EmitSystem(world, "PredatorKilled",
                    $"NPC{victim.Id.Value} killed attacker NPC{predator.Id.Value} in self-defence");
            }
        }

        foreach (var deadId in _deadVictims)
        {
            // Shared death path: spawns corpse.npc (butcherable) + grieves witnesses.
            MobSystem.RemoveDeadNpc(world, deadId);
        }

        foreach (var deadId in _deadAttackers)
        {
            // A predator felled by its intended prey — an ordinary death (no
            // "Murdered" fallout; self-defence isn't a colony crime).
            if (!_deadVictims.Contains(deadId))
            {
                MobSystem.RemoveDeadNpc(world, deadId);
            }
        }
    }

    // Lethal intent: aim for the vitals far more than a dog's leg-first bite, so
    // the kill actually comes rather than merely maiming.
    private static BodyPart PickKillPart(WorldState world, int predatorId)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick, predatorId, 561);
        if (roll < 0.55f) return BodyPart.Torso;
        if (roll < 0.80f) return BodyPart.Head;
        if (roll < 0.90f) return BodyPart.Pelvis;
        if (roll < 0.95f) return BodyPart.ArmR;
        return BodyPart.LegR;
    }

    private static void RunNpcDefenders(WorldState world, NPCState attacker, NPCState victim)
    {
        if (attacker.CurrentJunction is not { } attackerJunction)
        {
            return;
        }

        foreach (var defender in world.Entities.Npcs.Values)
        {
            if (defender.Id == attacker.Id ||
                defender.Id == victim.Id ||
                defender.Health <= 0f ||
                defender.Mind.CurrentGoal != GoalType.Defend ||
                defender.Mind.CombatAssistAttackerNpcId is not { } assistId ||
                !assistId.Equals(attacker.Id) ||
                defender.CurrentJunction is not { } defenderJunction)
            {
                continue;
            }

            var adjacent = defenderJunction.Equals(attackerJunction) ||
                (world.Junctions.Items.TryGetValue(attackerJunction, out var attackerJ) &&
                 attackerJ.Neighbors.Contains(defenderJunction));
            if (!adjacent)
            {
                continue;
            }

            defender.IsFighting = true;
            var weaponId = defender.Body.CanUseToolsOrWeapons
                ? SimBalance.BestMeleeWeapon(defender.Inventory.Items, defender.Body.IntactHands)
                : string.Empty;
            var weaponMult = SimBalance.MeleeStrikeBonus(weaponId);
            var attackSpeed = SimBalance.MeleeAttackSpeed(weaponId);
            var strikeReady = SimBalance.MeleeStrikeReady(world.Tick, defender.Id.Value, weaponId);
            var part = PickKillPart(world, defender.Id.Value + 271);
            var armor = EquipmentMath.ArmorForPart(world, attacker, part);
            var damage = strikeReady
                ? SimBalance.NpcStrikePerPass * defender.Body.StrikeFactor() * weaponMult * (1f - armor)
                : 0f;
            if (damage > 0f)
            {
                attacker.Body.Parts[part] = System.Math.Max(0f, attacker.Body.Parts[part] - damage);
                attacker.Health = attacker.Body.Mean();
                DamageReactionSystemHelpers.GrantAdrenaline(world, attacker, damage, "HelpCryDefended");
                WoundMath.Inflict(world, attacker, part, damage);
                if (attacker.Body.VitalDestroyed(out _))
                {
                    attacker.Health = 0f;
                }
            }

            Trace.Emit(world, defender.Id, "HelpCryDefended",
                $"Victim=NPC{victim.Id.Value} Attacker=NPC{attacker.Id.Value} {part} -{damage:F3} " +
                $"Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)}" +
                $"{(strikeReady ? string.Empty : " recovering")} Speed={attackSpeed:F1} " +
                $"AttackerHealth={attacker.Health:F2}");
            SocialCueSignals.Stamp(world, defender, "HelpCryDefended", victim.Id);

            if (attacker.Health <= 0f)
            {
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    if (npc.Mind.CombatAssistAttackerNpcId is { } id && id.Equals(attacker.Id))
                    {
                        CombatHelpSystem.ClearAssist(npc);
                    }
                }
                return;
            }
        }
    }

    // §56: killing to eat is a colony trauma — a heavy comfort hit on the killer
    // and a sharp relationship collapse toward them from every witness. (Grief on
    // witnesses is already triggered by the shared death sweep, RemoveDeadNpc.)
    private static void ApplyKillConsequences(WorldState world, NPCState killer, NPCState victim)
    {
        killer.Needs.Comfort = MathUtil.Clamp(
            killer.Needs.Comfort - SimBalance.PredationComfortPenalty, 0f, 1f);

        foreach (var witness in world.Entities.Npcs.Values)
        {
            if (witness.Id == killer.Id || witness.Id == victim.Id ||
                HexSpatialMath.HexDistance(witness.Tile, victim.Tile) > 6)
            {
                continue;
            }

            var rel = witness.Social.GetOrCreate(killer.Id);
            rel.Affinity = MathUtil.Clamp(
                rel.Affinity - SimBalance.PredationWitnessAffinityLoss, -1f, 1f);
            witness.Execution.LastTalkResultTick = world.Tick;
            witness.Execution.LastTalkAffinityDelta = -SimBalance.PredationWitnessAffinityLoss;
            SocialCueSignals.Stamp(world, witness, "WitnessedMurder", killer.Id);
            Trace.Emit(world, witness.Id, "RelationshipChanged",
                $"NPC{witness.Id.Value}->NPC{killer.Id.Value} " +
                $"Aff={rel.Affinity:F2} (-{SimBalance.PredationWitnessAffinityLoss:F2}) " +
                $"after witnessing murder");
        }

        Trace.EmitSystem(world, "Murdered",
            $"NPC{killer.Id.Value} killed NPC{victim.Id.Value} for meat [predation] " +
            $"KillerComfort={killer.Needs.Comfort:F2}");
    }
}

}
