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

// Spec 29C.3: dogs — roam, aggro, chase, bite. Combat is mutual and
// reactive: the bitten NPC is held in place and strikes back automatically.
internal static class CombatHelpSystem
{
    // §111.8: the victim is unconscious and therefore cannot produce the usual
    // §57 help cry. Every awake ally who can see the body search in this tight
    // radius treats the looter as an attacker immediately — no affinity gate,
    // compassion roll, or responder cap. This is a witnessed assault, not a
    // request the helper may politely decline.
    public static int RallyLootWitnesses(
        WorldState world,
        NPCState victim,
        EntityId looterId)
    {
        var responders = 0;
        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Id.Equals(victim.Id) ||
                helper.Id.Equals(looterId) ||
                helper.Health <= 0f ||
                helper.Body.IsProne ||
                helper.IsUnconscious(world.Tick) ||
                helper.IsPlayingDead(world.Tick) ||
                helper.Execution.CurrentInteraction == InteractionType.Sleep ||
                helper.IsFighting ||
                helper.Mind.CurrentGoal == GoalType.Flee ||
                helper.Mind.CurrentGoal == GoalType.GroupHunt ||
                // §121: ручную никто не срывает с места. Вписаться за подругу —
                // РЕШЕНИЕ, а решения за неё принимает игрок; он и увидел сцену
                // раньше её. Ответить на удары по себе она всё равно ответит —
                // это §109, и оно ниже уровня решений.
                ManualControlMath.IsManual(helper) ||
                !FactionRelations.AreAllies(helper, victim) ||
                HexSpatialMath.HexDistance(helper.Tile, victim.Tile) >
                    Spec111.LootWitnessRadiusTiles)
            {
                continue;
            }

            if (helper.Mind.CurrentGoal == GoalType.Defend &&
                helper.Mind.CombatAssistAttackerNpcId is { } current &&
                current.Equals(looterId))
            {
                continue; // already answering this exact assault
            }

            if (!CanReachAttacker(world, helper, null, looterId))
            {
                continue;
            }

            if (helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress ||
                helper.IsCarryingPerson)
            {
                PlanInterruption.TryAbortForCombat(world, helper, InterruptionCause.HelpFriend,
                    $"Witnessed looting of NPC{victim.Id.Value}");
            }

            helper.Mind.CurrentGoal = GoalType.Defend;
            helper.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.Defend,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec111.LootHelplessMaxSceneTicks
            };
            helper.Mind.CombatAssistDogId = null;
            helper.Mind.CombatAssistAttackerNpcId = looterId;
            helper.Mind.PendingTalkFrom = null;
            helper.Mind.PendingAidFrom = null;
            SocialCueSignals.Stamp(world, helper, "LootWitnessed", looterId);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, helper.Id, "LootWitnessRallied",
                    $"Victim=NPC{victim.Id.Value} Looter=NPC{looterId.Value} " +
                    $"Dist={HexSpatialMath.HexDistance(helper.Tile, victim.Tile)}");
            }
            responders++;
        }

        return responders;
    }

    public static void CallForHelpFromDog(WorldState world, NPCState victim, int dogId, int attackers)
    {
        CallForHelp(world, victim, dogId, null, $"Dog={dogId}", attackers);
    }

    public static void CallForHelpFromNpc(WorldState world, NPCState victim, EntityId attackerId, int attackers)
    {
        CallForHelp(world, victim, null, attackerId, $"Attacker=NPC{attackerId.Value}", attackers);
    }

    /// <summary>§121.9: ручной крик о помощи. Возвращает false с причиной,
    /// когда крик невозможен: не в бою, кулдаун §57.9 не прошёл, фича
    /// выключена. Успех идёт ровно тем же приватным <see cref="CallForHelp"/>,
    /// что и автоматика — радиус, Mortal-надбавка и отбор помощниц не
    /// дублируются. Атакующий берётся из боевых полей самой жертвы: без
    /// агрессора крик механически пуст (Defend некому назначить цель).</summary>
    public static bool TryCallForHelpManual(WorldState world, NPCState victim, out string reason)
    {
        reason = string.Empty;
        if (!Spec57.HelpCryEnabled)
        {
            reason = "FeatureDisabled";
            return false;
        }

        if (world.Tick - victim.Mind.LastHelpCryTick < Spec57.HelpCryCooldownTicks)
        {
            reason = "Cooldown";
            return false;
        }

        if (victim.Mind.CombatOpponentNpcId is { } attackerId &&
            world.Entities.Npcs.TryGetValue(attackerId, out var attacker) &&
            attacker.Health > 0f)
        {
            CallForHelpFromNpc(world, victim, attackerId, 1);
            return true;
        }

        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f && mob.TargetNpc is { } targetNpc &&
                targetNpc.Equals(victim.Id))
            {
                CallForHelpFromDog(world, victim, mob.Id, 1);
                return true;
            }
        }

        reason = "NotInCombat";
        return false;
    }

    private static void CallForHelp(
        WorldState world,
        NPCState victim,
        int? dogId,
        EntityId? attackerId,
        string attackerLabel,
        int attackers)
    {
        if (!Spec57.HelpCryEnabled ||
            world.Tick - victim.Mind.LastHelpCryTick < Spec57.HelpCryCooldownTicks)
        {
            return;
        }

        victim.Mind.LastHelpCryTick = world.Tick;

        // §57.9: смертельная беда кричит ГРОМЧЕ — дальше слышно, больше рук,
        // надбавка к решению каждой. Порог тот же, что у §53.8-тяжести по
        // духу: «при смерти, лежит или разбита» — это уже не потасовка.
        var mortal = victim.IsDying ||
            victim.Body.IsProne ||
            victim.Health < Spec57.HelpCryMortalPlight ||
            MobSystem.WorstPartHealth(victim) < Spec57.HelpCryMortalPlight;
        var radius = mortal ? Spec57.HelpCryMortalRadiusTiles : Spec57.HelpCryRadiusTiles;
        var maxResponders = mortal ? Spec57.MaxMortalCryResponders : Spec57.MaxHelpCryResponders;

        // Суффикс «:кто напал» — вид рисовал собаку на ЛЮБОЙ крик о помощи, в
        // том числе когда резал человек. Пир — АТАКУЮЩИЙ, а не сама жертва:
        // прежний victim.Id всплывал её же лицом над её же головой.
        SocialCueSignals.Stamp(world, victim, dogId.HasValue ? "HelpCry:dog" : "HelpCry:npc", attackerId);
        Trace.Emit(world, victim.Id, "HelpCry",
            $"{attackerLabel} Radius={radius} " +
            $"Health={victim.Health:F2} Attackers={attackers}{(mortal ? " Mortal" : "")}");

        var responders = 0;
        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Id == victim.Id ||
                helper.Health <= 0f ||
                // §72: only her own side answers. Without this the outsider who
                // just cut her hears the cry, rolls compassion, takes the Defend
                // goal against HIMSELF and stands frozen for the goal-lock —
                // which reads as a pathing bug, not a faction bug.
                !FactionRelations.AreAllies(helper, victim) ||
                (attackerId is { } attackerEntity && helper.Id.Equals(attackerEntity)) ||
                // §60: asleep or out cold — the cry does not register at all:
                // no waking into Defend, no "ignored" cue, not even an emoji.
                helper.IsUnconscious(world.Tick) ||
                helper.IsPlayingDead(world.Tick) || // §105.14: лежит и не выдаёт себя
                helper.Execution.CurrentInteraction == InteractionType.Sleep ||
                // §121: крик о помощи ручную не поднимает — см. выше.
                ManualControlMath.IsManual(helper) ||
                HexSpatialMath.HexDistance(helper.Tile, victim.Tile) > radius)
            {
                continue;
            }

            var relationship = helper.Social.GetOrCreate(victim.Id);
            // §57.9: «своих в беде не бросают» — пол дружбы. Крик о помощи это
            // всегда про жизнь, поэтому вражда больше не глушит отклик: злишься
            // на неё — спасёшь и выскажешь. Дружба выше пола добавляет как
            // прежде; личность (CompassionTrait) остаётся главным членом.
            var affinity01 = System.Math.Max(
                (relationship.Affinity + 1f) * 0.5f, Spec57.HelpCryAffinityFloor);
            var compassionPressure = 1f - helper.Needs.Compassion;
            var score = helper.CompassionTrait * 0.55f +
                affinity01 * 0.35f +
                compassionPressure * 0.10f +
                (mortal ? Spec57.HelpCryMortalBonus : 0f);
            var roll = MathUtil.Hash01(world.Seed, world.Tick, victim.Id.Value, helper.Id.Value);
            var canHelp = helper.Health >= Spec57.HelpCryHealthGate &&
                helper.Needs.Blood >= Spec57.HelpCryHealthGate &&
                !helper.Mind.IsStarving &&
                !helper.Mind.IsDehydrated &&
                !helper.IsFighting &&
                helper.Mind.CurrentGoal != GoalType.Flee &&
                helper.Mind.CurrentGoal != GoalType.Defend &&
                helper.Mind.CurrentGoal != GoalType.GroupHunt && // §108: она уже идёт бить

                CanReachAttacker(world, helper, dogId, attackerId);

            if (!canHelp || score < Spec57.HelpCryDecisionThreshold || roll > score)
            {
                SocialCueSignals.Stamp(world, helper, "HelpCryIgnored", victim.Id);
                Trace.Emit(world, helper.Id, "HelpCryIgnored",
                    $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                    $"Score={score:F2} Roll={roll:F2} CanHelp={canHelp}");
                continue;
            }

            if (helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress ||
                helper.IsCarryingPerson)
            {
                PlanInterruption.TryAbortForCombat(world, helper, InterruptionCause.HelpFriend,
                    $"Answering help cry from NPC{victim.Id.Value}");
            }

            helper.Mind.CurrentGoal = GoalType.Defend;
            helper.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.Defend,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec57.HelpCryCooldownTicks
            };
            helper.Mind.CombatAssistDogId = dogId;
            helper.Mind.CombatAssistAttackerNpcId = attackerId;
            helper.Mind.PendingTalkFrom = null;
            helper.Mind.PendingAidFrom = null;
            SocialCueSignals.Stamp(world, helper, "HelpCryAnswer", victim.Id);
            SocialCueSignals.Stamp(world, victim, "HelpCryAnswered", helper.Id);
            Trace.Emit(world, helper.Id, "HelpCryAnswered",
                $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                $"Score={score:F2} Roll={roll:F2} Affinity={relationship.Affinity:F2} " +
                $"CompassionTrait={helper.CompassionTrait:F2}");

            responders++;
            if (responders >= maxResponders)
            {
                break;
            }
        }
    }

    // §57.10: стон умирающей ВНЕ боя. Боевой крик живёт в укусах и бегстве;
    // но волки уходят, а она остаётся истекать — и раньше молчала навсегда:
    // помощь §53 находила её только глазами или по устаревающей памяти.
    // Теперь лежащая в тяжести И В СОЗНАНИИ раз в кулдаун крика стонет, и
    // каждая союзница в смертельном радиусе запоминает её как только что
    // увиденную (позиция + тяжесть + чем помочь). Дальше её ведёт обычная
    // §53.8-помощь по памяти — без дисконта, потому что «подруга умирает» не
    // слух. Целей и планов стон не назначает: решение остаётся за аукционом.
    public static void TryMoanForHelp(WorldState world, NPCState victim)
    {
        if (!Spec57.DyingMoanEnabled || !Spec57.HelpCryEnabled ||
            victim.Health <= 0f ||
            victim.IsFighting || // в бою зовёт боевая ветка — со своим пиром
            victim.IsUnconscious(world.Tick) || // §60: без сознания не стонут
            victim.IsPlayingDead(world.Tick) || // §105.14: не выдаёт себя
            world.Tick - victim.Mind.LastHelpCryTick < Spec57.HelpCryCooldownTicks)
        {
            return;
        }

        // §118.4 r2 (#166): «застрял, ноги сломаны, не могу добраться до дома» —
        // тоже повод звать. До этого стон был только предсмертным, и живая
        // переломанная колонистка, отрезанная от дома, молчала до конца.
        var stranded = KenshiRescueMath.IsStranded(world, victim);
        var mortal = stranded || victim.IsDying ||
            (victim.Body.IsProne &&
             MobSystem.WorstPartHealth(victim) < Spec57.HelpCryMortalPlight);
        if (!mortal)
        {
            return;
        }

        victim.Mind.LastHelpCryTick = world.Tick;
        SocialCueSignals.Stamp(world, victim, "HelpMoan", null);
        Trace.Emit(world, victim.Id, "HelpMoan",
            $"Radius={Spec57.HelpCryMortalRadiusTiles} Health={victim.Health:F2} " +
            $"Dying={(victim.IsDying ? 1 : 0)} Prone={(victim.Body.IsProne ? 1 : 0)} " +
            $"Stranded={(stranded ? 1 : 0)}");

        var aidKind = AidAssessment.Assess(victim, world.Tick, out var severity);
        foreach (var hearer in world.Entities.Npcs.Values)
        {
            if (hearer.Id.Equals(victim.Id) ||
                hearer.Health <= 0f ||
                hearer.IsUnconscious(world.Tick) ||
                hearer.Execution.CurrentInteraction == InteractionType.Sleep || // §60: сон глух
                !FactionRelations.AreAllies(hearer, victim) ||
                HexSpatialMath.HexDistance(hearer.Tile, victim.Tile) >
                    Spec57.HelpCryMortalRadiusTiles)
            {
                continue;
            }

            // Память, не взгляд: LastSeenTick на тик позади, иначе перцепция
            // примет запись за «вижу сейчас» и выкинет из Remembered (см.
            // BuildRememberedAgents). Поля — ровно как у живого взгляда §125.3.
            if (!hearer.Memory.KnownAgents.TryGetValue(victim.Id, out var met))
            {
                met = new Memory.AgentMemory { Id = victim.Id };
                hearer.Memory.KnownAgents[victim.Id] = met;
            }

            met.Faction = victim.Faction;
            met.Tile = victim.Tile;
            met.Junction = victim.CurrentJunction;
            met.LastSeenTick = world.Tick - 1;
            met.Suffering = severity;
            met.AidKind = aidKind;
            met.Helpless = true;
            SocialCueSignals.Stamp(world, hearer, "MoanHeard", victim.Id);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, hearer.Id, "MoanHeard",
                    $"Victim=NPC{victim.Id.Value} Suffering={severity:F2} Kind={aidKind}");
            }
        }
    }

    // 29C.4B friend-guard: runs every medium fight pass, BEFORE any cry.
    // A friend (affinity >= FriendGuardAffinity) within FriendGuardRadiusTiles
    // of the victim needs no cry and no compassion roll — she aborts whatever
    // she is doing and takes the Defend goal at the aggressor. The cry
    // (score + roll + health gate) stays as the wider-radius fallback for
    // everyone who is not that close a friend.
    public static void RallyFriends(
        WorldState world,
        NPCState victim,
        int? dogId,
        EntityId? attackerId,
        string attackerLabel)
    {
        if (!Spec57.FriendGuardEnabled || (!dogId.HasValue && !attackerId.HasValue))
        {
            return;
        }

        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Id == victim.Id ||
                helper.Health <= 0f ||
                helper.Body.IsProne ||
                helper.IsUnconscious(world.Tick) || // §60: out cold — no rushing anywhere
                helper.IsPlayingDead(world.Tick) || // §105.14: и притворяющаяся не вскакивает
                helper.Execution.CurrentInteraction == InteractionType.Sleep || // §60: sleepers ignore cries too
                helper.IsFighting ||
                helper.Mind.CurrentGoal == GoalType.Defend ||
                helper.Mind.CurrentGoal == GoalType.Flee ||
                // §108: она уже идёт бить его — своей волей и вместе с двумя
                // подругами. Перекинуть её в Defend значило бы разменять
                // расправу на конвой и развалить группу на полпути.
                helper.Mind.CurrentGoal == GoalType.GroupHunt ||
                !FactionRelations.AreAllies(helper, victim) || // §72: her side only
                (attackerId is { } aId && helper.Id.Equals(aId)) ||
                HexSpatialMath.HexDistance(helper.Tile, victim.Tile) > Spec57.FriendGuardRadiusTiles)
            {
                continue;
            }

            // §72: an OUTSIDER attacking one of ours rallies the whole camp, with
            // no affinity gate. The 0.25 friendship threshold is right for a
            // domestic scrap, but the girls have not built that affinity up in
            // the opening days — and "they fight back as one" has to be true
            // exactly then, when a lone stranger is picking them off one by one.
            var attackerIsOutsider = attackerId is { } outsiderId &&
                world.Entities.Npcs.TryGetValue(outsiderId, out var attackerNpc) &&
                FactionRelations.AreHostile(attackerNpc, victim);
            var skipAffinityGate = attackerIsOutsider && Spec72.RallyIgnoresAffinityVsOutsider;

            var relationship = helper.Social.GetOrCreate(victim.Id);
            if (!skipAffinityGate && relationship.Affinity < Spec57.FriendGuardAffinity)
            {
                continue;
            }

            // §109: вписаться — РЕШЕНИЕ, а не рефлекс. Раньше порог дружбы
            // тащил в драку любую соседку независимо от её шансов и состояния.
            // Теперь она взвешивает: дорога ли ей та, кого бьют; потянет ли
            // она ЭТОГО противника; и жива ли сама. Умирающая и разбитая не
            // лезет никогда.
            if (helper.Health < Spec57.FriendGuardHealthGate ||
                helper.IsDying ||
                // §121: и здесь решает игрок, а не порог дружбы.
                ManualControlMath.IsManual(helper) ||
                !helper.Body.CanUseToolsOrWeapons)
            {
                continue;
            }

            // Шансы: размен Force против человека; против зверя — об константу
            // (у собак нет оружия и брони, их Force не считается).
            var myForce = AbuseMath.Force(world, helper);
            var foeForce = attackerId is { } foeId &&
                world.Entities.Npcs.TryGetValue(foeId, out var foe)
                    ? AbuseMath.Force(world, foe)
                    : Spec57.FriendGuardDogForce;
            var edge = MathUtil.Clamp01(
                myForce / System.Math.Max(foeForce, 0.0001f));

            var condition = MathUtil.Clamp01(
                System.Math.Min(helper.Health, MobSystem.WorstPartHealth(helper)));
            var affinity01 = MathUtil.Clamp01((relationship.Affinity + 1f) * 0.5f);
            var hatred = attackerId is { } hatedId
                ? MathUtil.Clamp01(-helper.Social.GetOrCreate(hatedId).Affinity)
                : 0f;

            var score = affinity01 * Spec57.FriendGuardAffinityWeight +
                edge * Spec57.FriendGuardEdgeWeight +
                condition * Spec57.FriendGuardConditionWeight +
                hatred * Spec57.FriendGuardHatredBonus;

            // Бросок на ОКНО боя, не на тик: RallyFriends зовут каждый средний
            // проход, и по-тиковый переброс превратил бы любой порог в
            // «рано или поздно да».
            var window = world.Tick / System.Math.Max(1, Spec57.HelpCryCooldownTicks);
            var roll = MathUtil.Hash01(
                world.Seed, window, victim.Id.Value, helper.Id.Value);
            if (score < Spec57.FriendGuardDecisionFloor || roll > score)
            {
                if (world.Tick % 64 == 0)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, helper.Id, "FriendGuardDeclined",
                            $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                            $"Score={score:F2} Roll={roll:F2} Edge={edge:F2} " +
                            $"Cond={condition:F2} Aff={relationship.Affinity:F2}");
                    }
                }
                continue;
            }

            if (!CanReachAttacker(world, helper, dogId, attackerId))
            {
                continue;
            }

            if (helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress ||
                helper.IsCarryingPerson)
            {
                PlanInterruption.TryAbortForCombat(world, helper, InterruptionCause.HelpFriend,
                    $"Rushing to defend friend NPC{victim.Id.Value}");
            }

            helper.Mind.CurrentGoal = GoalType.Defend;
            helper.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.Defend,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec57.HelpCryCooldownTicks
            };
            helper.Mind.CombatAssistDogId = dogId;
            helper.Mind.CombatAssistAttackerNpcId = attackerId;
            helper.Mind.PendingTalkFrom = null;
            helper.Mind.PendingAidFrom = null;
            SocialCueSignals.Stamp(world, helper, "HelpCryAnswer", victim.Id);
            SocialCueSignals.Stamp(world, victim, "HelpCryAnswered", helper.Id);
            Trace.Emit(world, helper.Id, "FriendGuard",
                $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                $"Score={score:F2} Roll={roll:F2} Edge={edge:F2} " +
                $"Affinity={relationship.Affinity:F2} " +
                $"Dist={HexSpatialMath.HexDistance(helper.Tile, victim.Tile)}");
        }
    }

    /// <summary>Combat events are outside the ordinary goal auction, so they
    /// must explicitly honour both its cooldown and the planner's exact
    /// availability contract.</summary>
    internal static bool CanReachAttacker(
        WorldState world, NPCState helper, int? dogId, EntityId? attackerId)
    {
        if (PlanningSystem.IsGoalOnCooldown(helper, GoalType.Defend, world.Tick))
        {
            return false;
        }

        JunctionId? attackerJunction = null;
        if (dogId is { } wantedDog)
        {
            foreach (var dog in world.Mobs)
            {
                if (dog.Id == wantedDog && dog.Health > 0f)
                {
                    attackerJunction = dog.Junction;
                    break;
                }
            }
        }
        else if (attackerId is { } wantedAttacker &&
                 world.Entities.Npcs.TryGetValue(wantedAttacker, out var attacker) &&
                 attacker.Health > 0f)
        {
            attackerJunction = attacker.CurrentJunction;
        }

        return attackerJunction is { } target &&
            PlanningSystem.HasReachableDefendApproach(world, helper, target);
    }

    // §57.9: спасение — событие для ОБЕИХ. Взаимный подъём отношений в момент
    // СНЯТОЙ угрозы: зверь мёртв или враг отступил, а подмога жива и дралась.
    // Тот же протокол, что у Talk/§53-aid: обе стороны, клампы, штампы для
    // «+»-попа над головами и RelationshipChanged в хронику (формат разбирает
    // GameHistoryFormatter — «->NPC» обязателен).
    public static void GrantRescueGratitude(WorldState world, NPCState rescuer, NPCState victim)
    {
        if (rescuer.Id.Equals(victim.Id))
        {
            return;
        }

        var delta = Spec57.RescueGratitudeAffinity;
        var rescuerRel = rescuer.Social.GetOrCreate(victim.Id);
        var victimRel = victim.Social.GetOrCreate(rescuer.Id);
        rescuerRel.Affinity = MathUtil.Clamp(rescuerRel.Affinity + delta, -1f, 1f);
        rescuerRel.Familiarity = MathUtil.Clamp01(rescuerRel.Familiarity + delta);
        victimRel.Affinity = MathUtil.Clamp(victimRel.Affinity + delta, -1f, 1f);
        victimRel.Familiarity = MathUtil.Clamp01(victimRel.Familiarity + delta);

        rescuer.Execution.LastTalkResultTick = world.Tick;
        rescuer.Execution.LastTalkAffinityDelta = delta;
        victim.Execution.LastTalkResultTick = world.Tick;
        victim.Execution.LastTalkAffinityDelta = delta;

        Trace.Emit(world, rescuer.Id, "RelationshipChanged",
            $"NPC{rescuer.Id.Value}->NPC{victim.Id.Value} " +
            $"Fam={rescuerRel.Familiarity:F2} (+{delta:F2}) " +
            $"Aff={rescuerRel.Affinity:F2} (+{delta:0.00}) Cause=[Rescue]");
        Trace.Emit(world, victim.Id, "RelationshipChanged",
            $"NPC{victim.Id.Value}->NPC{rescuer.Id.Value} " +
            $"Fam={victimRel.Familiarity:F2} (+{delta:F2}) " +
            $"Aff={victimRel.Affinity:F2} (+{delta:0.00}) Cause=[Rescue]");
    }

    public static void ClearAssist(NPCState npc)
    {
        npc.Mind.CombatAssistDogId = null;
        npc.Mind.CombatAssistAttackerNpcId = null;
        npc.Mind.AssistHoldSinceTick = 0;
        if (npc.Mind.CurrentGoal == GoalType.Defend)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }
    }
}

}
