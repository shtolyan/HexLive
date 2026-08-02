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
        TryStartAbuse(world);
        TryStartHunts(world);

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

            if (!InteractionReach.CanStrike(world, raider, victim))
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

    // §72: the hunt STARTS by interrupting, not by winning the auction.
    //
    // Bidding was the first design and it never fired once in ten game days on
    // six seeds. Two measured reasons: a lone man is permanently uncomfortable
    // and short on stamina, so his Sit ("leisure") bid runs as high as 0.85; and
    // DecisionSystem re-runs the auction ONLY between interactions, so the short
    // window in which a girl is actually alone almost never coincides with a
    // re-decision.
    //
    // This is the same shape §57's help cry and §62's first strike already use,
    // for the same reason — a time-critical opportunity aborts the current plan
    // and takes the goal directly. The auction bid stays as the quiet fallback
    // that walks him over when nothing of his own is pressing.
    private static void TryStartHunts(WorldState world)
    {
        if (world.Tick < Spec72.RaidGraceDays * EnvironmentSystem.DayLengthTicks)
        {
            return;
        }

        foreach (var raider in world.Entities.Npcs.Values)
        {
            // §82: ВТОРЖЕНИЕ. Подошла к его стоянке — он бьёт, и точка.
            //
            // Это не налёт и не шантаж: там он оппортунист и считает выгоду,
            // из-за чего выходил неприлично мирным — девушки ходили мимо его
            // очага, а он взвешивал расклад и не находил повода. Хозяин двора
            // повода не ищет. Поэтому проверка стоит ПЕРЕД всеми гейтами
            // охоты: ни льготные дни, ни кулдаун, ни «достаточно ли она
            // одинока», ни его собственный голод тут не спрашиваются.
            //
            // Дальше всё как в обычном налёте — сцепка, удары на быстром слое,
            // её выбор «драться или бежать», клич и отход. Отдельную сцену
            // заводить незачем: она бы делала ровно это же.
            if (Spec82.TerritorialEnabled &&
                raider.Faction != Faction.Colony &&
                raider.Health > 0f &&
                !raider.IsFighting &&
                !raider.IsUnconscious(world.Tick) &&
                !raider.Body.IsProne &&
                raider.Mind.CurrentGoal != GoalType.Flee &&
                world.Tick >= raider.Mind.TerritoryCooldownUntilTick &&
                world.FactionHomes.TryGetValue(raider.Faction, out var camp))
            {
                var intruder = NearestIntruder(world, raider, camp);
                if (intruder is not null)
                {
                    StartRaidOn(world, raider, intruder, "Trespass");
                    raider.Mind.TerritoryCooldownUntilTick =
                        world.Tick + Spec82.TerritoryCooldownTicks;
                    continue;
                }
            }

            if (raider.Faction == Faction.Colony ||
                raider.Health <= 0f ||
                raider.Mind.CurrentGoal == GoalType.Raid ||
                raider.Mind.CurrentGoal == GoalType.Flee ||
                raider.IsFighting ||
                raider.IsUnconscious(world.Tick) ||
                world.Tick < raider.Mind.RaidCooldownUntilTick ||
                raider.Needs.Hunger > Spec72.RaidSelfNeedCeiling ||
                raider.Needs.Thirst > Spec72.RaidSelfNeedCeiling ||
                raider.Needs.Energy < Spec72.RaidSelfEnergyFloor ||
                !RaidMath.IsFitToRaid(raider, world))
            {
                continue;
            }

            var victim = RaidMath.BestVictim(world, raider, out var opportunity);
            if (victim is null)
            {
                continue;
            }

            StartRaidOn(world, raider, victim, $"Opportunity Opp={opportunity:F2}");
        }
    }

    // §87: ⭐ АБЬЮЗ НАЧИНАЕТСЯ ПРЕРЫВАНИЕМ, а не победой в аукционе.
    //
    // Это ровно та же ошибка, на которой §72 потерял два круга и которая
    // записана в спеке чёрным по белому: DecisionSystem переигрывает аукцион
    // ТОЛЬКО между взаимодействиями. Пока чужак сидит на пеньке у костра —
    // а «посидеть» это длинное взаимодействие, и у одинокого человека оно
    // выигрывает постоянно, — выбора просто НЕ ПРОИСХОДИТ. Сколько ставку ни
    // повышай, она никогда не будет посчитана.
    //
    // Налёт из-за этого сделали прерыванием ещё в §72.9, а абьюз остался
    // чистой ставкой — и потому не срабатывал ни разу за четыре игровых дня,
    // при том что все замеры показывали «жертва есть в 95% времени». Замеры
    // мерили наличие жертвы, а не то, что его вообще спрашивают.
    //
    // Форма та же, что у клича §57 и первого удара §62: срочная возможность
    // рвёт текущий план и берёт цель напрямую.
    private static void TryStartAbuse(WorldState world)
    {
        if (!Spec81.AbuseEnabled)
        {
            return;
        }

        if (world.Tick < Spec81.AbuseGraceDays * EnvironmentSystem.DayLengthTicks)
        {
            return;
        }

        foreach (var abuser in world.Entities.Npcs.Values)
        {
            if (abuser.Faction == Faction.Colony ||
                abuser.Health <= 0f ||
                abuser.IsFighting ||
                abuser.IsUnconscious(world.Tick) ||
                abuser.Body.IsProne ||
                !abuser.Body.CanUseToolsOrWeapons ||
                abuser.Mind.CurrentGoal == GoalType.Abuse ||
                abuser.Mind.CurrentGoal == GoalType.Raid ||
                abuser.Mind.CurrentGoal == GoalType.Flee ||
                world.Tick < abuser.Mind.AbuseCooldownUntilTick ||
                AbuseMath.Drive(abuser) <= 0f)
            {
                continue;
            }

            var mark = AbuseMath.BestMark(world, abuser, out var hasLoot);
            if (mark is null)
            {
                continue;
            }

            if (abuser.Plan.Status == PlanStatus.Active ||
                abuser.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, abuser, $"Abusing NPC{mark.Id.Value}");
            }

            abuser.Mind.CurrentGoal = GoalType.Abuse;
            abuser.Mind.AbuseTargetNpcId = mark.Id;
            abuser.Mind.AbuseHasLoot = hasLoot;
            abuser.Mind.AbuseBeat = 0;
            abuser.Mind.AbuseBlows = 0;
            abuser.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.Abuse,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec81.AbuseLockTicks
            };

            Trace.Emit(world, abuser.Id, "AbuseTriggered",
                $"Mark=NPC{mark.Id.Value} Social={abuser.Needs.Social:F2} " +
                $"Drive={AbuseMath.Drive(abuser):F2} Loot={hasLoot} " +
                $"Dist={HexSpatialMath.HexDistance(abuser.Tile, mark.Tile)}");
        }
    }

    // §82: кто залез на его двор. Ближайший — а не «самый слабый»: он не
    // выбирает жертву, он гонит того, кто пришёл.
    private static NPCState NearestIntruder(WorldState world, NPCState owner, TileCoord camp)
    {
        NPCState nearest = null;
        var best = int.MaxValue;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Id.Equals(owner.Id) ||
                npc.Health <= 0f ||
                !FactionRelations.AreHostile(owner.Faction, npc.Faction))
            {
                continue;
            }

            var toCamp = HexSpatialMath.HexDistance(npc.Tile, camp);
            if (toCamp > Spec82.TerritoryRadiusTiles)
            {
                continue;
            }

            // До неё ещё надо дойти: девушка на другом берегу залива формально
            // «в радиусе», а фактически недосягаема.
            if (owner.CurrentJunction is not { } from ||
                npc.CurrentJunction is not { } to ||
                !Connectivity.Reachable(world, from, to, owner.Body.CanJump))
            {
                continue;
            }

            // Ничью разрывает меньший id — порядок обхода словаря не должен
            // протекать в реплей.
            if (toCamp < best || (toCamp == best && nearest is not null &&
                                  npc.Id.Value < nearest.Id.Value))
            {
                best = toCamp;
                nearest = npc;
            }
        }

        return nearest;
    }

    // §82: общий вход в налёт — им пользуются и аукционная охота, и выгон со
    // двора, чтобы «как начинается драка» было описано ровно в одном месте.
    private static void StartRaidOn(WorldState world, NPCState raider, NPCState victim, string why)
    {
        if (raider.Plan.Status == PlanStatus.Active ||
            raider.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, raider, $"Hunting NPC{victim.Id.Value}");
        }

        raider.Mind.CurrentGoal = GoalType.Raid;
        raider.Mind.RaidTargetNpcId = victim.Id;
        raider.Mind.RaidStartedTick = world.Tick;
        raider.Mind.RaidLastJunction = raider.CurrentJunction;
        raider.Mind.RaidStallSinceTick = 0;
        raider.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Raid,
            StartTick = world.Tick,
            EndTick = world.Tick + Spec72.RaidLockTicks
        };

        Trace.Emit(world, raider.Id, "RaidStarted",
            $"Victim=NPC{victim.Id.Value} Why={why} " +
            $"Allies={RaidMath.AlliesAround(world, victim)} " +
            $"Dist={HexSpatialMath.HexDistance(raider.Tile, victim.Tile)}");
    }

    // §85: вход в бой из сцены абьюза — «проигнорировала, значит будет драка».
    // Публичный, потому что зовут снаружи; кулдаун налёта тут не спрашивается:
    // это не выбор охотиться, а продолжение уже начатого столкновения.
    internal static void EscalateToRaid(WorldState world, NPCState raider, NPCState victim, string why)
    {
        if (raider.Health <= 0f || victim.Health <= 0f ||
            raider.IsUnconscious(world.Tick) || raider.Body.IsProne)
        {
            return;
        }

        StartRaidOn(world, raider, victim, why);
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
                !InteractionReach.CanStrike(world, defender, raider))
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
