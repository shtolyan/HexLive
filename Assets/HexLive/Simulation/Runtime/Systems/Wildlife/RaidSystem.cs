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

            // §106: she dove — a silent Unpair here would leave the plan alive,
            // and between medium rebuilds it would walk him into the sea after
            // her. An explicit abandon, same as the stalk-side "Swimming" valve.
            if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, victim))
            {
                Unpair(raider);
                Unpair(victim);
                PlanningSystem.AbandonRaid(world, raider, "Swimming");
                raider.IsFighting = false;
                continue;
            }

            // §105.14: она притворилась мёртвой — то же runtime-зеркало, что и
            // у воды: молчаливого Unpair мало, план надо оборвать явно.
            if (victim.IsPlayingDead(world.Tick))
            {
                Unpair(raider);
                Unpair(victim);
                PlanningSystem.AbandonRaid(world, raider, "PlayDead");
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
                 // §109: сцена абьюза — тоже бой, в который вписываются.
                 // Раньше метла требовала строго Raid и сносила ассист
                 // защитницы на первом же среднем тике сцены: подруги
                 // НИКОГДА не могли вступиться в абьюз, только в налёт.
                 (attacker.Mind.CurrentGoal != GoalType.Raid &&
                  attacker.Mind.CurrentGoal != GoalType.Abuse)))
            {
                CombatHelpSystem.ClearAssist(npc);
                npc.Mind.CombatOpponentNpcId = null;
                npc.IsFighting = false;
            }
        }

        AnswerBlows(world);
        BraceMarks(world);

        foreach (var deadId in _dead)
        {
            MobSystem.RemoveDeadNpc(world, deadId);
        }
    }

    // §81.15: она ЧУВСТВУЕТ, что за ней идут. Жертва дорубала кокос, пока
    // через два гекса к ней бежал человек с целью «докопаться», — дела она
    // бросала только в момент старта сцены. Теперь заявленная метка
    // (PendingAbuseFrom) при приближении обидчика бросает занятие, встаёт в
    // стойку и разворачивается к нему лицом — ждёт. Пары НЕТ намеренно: пара
    // в HumanCombatSystem означает замах, а «ждать, что он сделает» — не
    // «ударить первой». Решение отвечать принимает сцена (AnswersBack).
    private static void BraceMarks(WorldState world)
    {
        foreach (var mark in world.Entities.Npcs.Values)
        {
            if (mark.Health <= 0f ||
                mark.Mind.PendingAbuseFrom is not { } claimerId ||
                !world.Entities.Npcs.TryGetValue(claimerId, out var claimer) ||
                claimer.Health <= 0f ||
                claimer.Mind.CurrentGoal != GoalType.Abuse ||
                // Сцена уже идёт — дела бросает сама сцена.
                claimer.Execution.CurrentInteraction == InteractionType.Abuse ||
                mark.IsUnconscious(world.Tick) ||
                mark.Execution.CurrentInteraction == InteractionType.Sleep ||
                mark.Mind.CurrentGoal == GoalType.Flee ||
                // §109.10: идущая ДРАТЬСЯ не «готовится» — она уже готова.
                // Braces обрывал охотнице §108 цель через 32 тика после
                // сговора (GoalLost) — метка и охотница бывают одним человеком.
                mark.Mind.CurrentGoal == GoalType.GroupHunt ||
                mark.Mind.CurrentGoal == GoalType.Defend ||
                mark.Mind.CurrentGoal == GoalType.Raid ||
                mark.Mind.CurrentGoal == GoalType.Abuse ||
                HexSpatialMath.HexDistance(mark.Tile, claimer.Tile) >
                    Spec57.AnswerReadyRadiusTiles)
            {
                continue;
            }

            // §110/§81.15: рыдающая при подходе обидчика ОБРЫВАЕТ рыдания —
            // опасность важнее слёз (боль их уже обрывает, WoundMath).
            // Иначе она лежала, плакала и лишь поворачивалась вслед — вместо
            // того чтобы встать, взять нож и ждать в стойке.
            if (mark.IsCrying(world.Tick))
            {
                mark.Mind.CryingUntilTick = 0;
                Trace.Emit(world, mark.Id, "MarkBraces",
                    $"Abuser=NPC{claimer.Id.Value} CutCrying=True");
            }

            if (mark.Plan.Status == PlanStatus.Active ||
                mark.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, mark,
                    $"Braces for NPC{claimer.Id.Value}");
                mark.Mind.CurrentGoal = GoalType.None;
                Trace.Emit(world, mark.Id, "MarkBraces",
                    $"Abuser=NPC{claimer.Id.Value} " +
                    $"Dist={HexSpatialMath.HexDistance(mark.Tile, claimer.Tile)}");
            }

            // §109.13: разворот — не слежение. Доворачиваем, только когда он
            // заметно сбоку: FaceOpponent на КАЖДОМ среднем тике держал её
            // прицеленной в бегущего, тело подруливало без остановки, и
            // снаружи это читалось как «крутится туда-сюда». Стойка — только
            // вплотную; пока он бежит, она просто стоит и смотрит.
            var toHim = HexSpatialMath.AngleDegrees(new Float2(
                claimer.Position.X - mark.Position.X,
                claimer.Position.Y - mark.Position.Y));
            var delta = (toHim - mark.RotationDegrees) % 360f;
            if (delta > 180f) { delta -= 360f; }
            if (delta < -180f) { delta += 360f; }
            var off = System.Math.Abs(delta);
            if (off > Spec57.BraceFaceDeadzoneDegrees)
            {
                HumanCombatSystem.FaceOpponent(world, mark, claimer);
            }

            if (InteractionReach.CanStrike(world, mark, claimer))
            {
                mark.IsFighting = true;
            }
        }
    }

    // §109: ЕСЛИ ТЕБЯ БЬЮТ — БЕЙ В ОТВЕТ, кем бы ты ни был. Защитницы §57 и
    // охотницы §108 сцепляются через CombatOpponentNpcId, а сторону ЦЕЛИ не
    // ставил никто: чужак стоял под тремя ножами (или продолжал собирать
    // палки, пока за ним бежали) и умирал, не вынув мачете. Правило общее и
    // симметричное — работает для любого NPC под ударами людей.
    //
    // Порядок важен: после MobSystem (он гасит IsFighting у всех) и ДО
    // GroupHuntSystem — решение §108 «квари бежит домой» имеет право
    // перекрыть стойку, и наоборот не бывает.
    private static void AnswerBlows(WorldState world)
    {
        if (!Spec57.AnswerBlowsEnabled)
        {
            return;
        }

        // §109.12: ⭐ ОДНА МЕТЛА НА ВСЕ ЗАВИСШИЕ ПАРЫ. Пара
        // (CombatOpponentNpcId) — это вечные замахи от HumanCombatSystem, а
        // подметали её только по ассисту (CombatAssistAttackerNpcId) и только
        // в конце сцены. Сцепка от «бей в ответ» не имеет ассиста, и когда
        // противник уходил, умирал или переставал драться, ответившая
        // оставалась молотить воздух — «бьёт и скользит», ровно как видел
        // игрок. Держим пару только пока ОБОСНОВАНИЕ живо: боевая цель,
        // идущая сцена, или встречная сцепка.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CombatOpponentNpcId is not { } oppId)
            {
                continue;
            }

            var opponentAlive = world.Entities.Npcs.TryGetValue(oppId, out var opp) &&
                opp.Health > 0f && !opp.IsUnconscious(world.Tick);
            var mineJustified = npc.Mind.CurrentGoal is GoalType.Raid or GoalType.Abuse
                    or GoalType.Defend or GoalType.GroupHunt or GoalType.Prey ||
                npc.Execution.CurrentInteraction == InteractionType.Abuse ||
                npc.Mind.PendingAbuseFrom is not null;
            var theirsJustified = opponentAlive &&
                (opp.Mind.CombatOpponentNpcId is { } back && back.Equals(npc.Id) ||
                 opp.Mind.CurrentGoal is GoalType.Raid or GoalType.Abuse
                     or GoalType.Defend or GoalType.GroupHunt or GoalType.Prey);

            if (!opponentAlive || (!mineJustified && !theirsJustified))
            {
                npc.Mind.CombatOpponentNpcId = null;
                npc.IsFighting = false;
                FightScene.ReleaseSwingSlot(npc);
                CombatHelpSystem.ClearAssist(npc);
            }
        }

        foreach (var target in world.Entities.Npcs.Values)
        {
            if (target.Health <= 0f ||
                target.IsUnconscious(world.Tick) ||
                target.Body.IsProne ||
                !target.Body.CanUseToolsOrWeapons ||
                // Бегущий бежит: клапаны §29C.4A/§108 сами решают, когда
                // бегство превращается в бой до победного.
                target.Mind.CurrentGoal == GoalType.Flee ||
                // Боевые цели ведут собственную сцепку — не дёргать.
                // Abuse — особый случай НИЖЕ: идущий докапываться под ударами
                // отвечает, но похода не бросает.
                target.Mind.CurrentGoal == GoalType.Raid ||
                target.Mind.CurrentGoal == GoalType.Defend ||
                target.Mind.CurrentGoal == GoalType.GroupHunt ||
                // Сценой абьюза правит сцена — с обеих сторон: он в такте,
                // она приняла решение в AnswersBack, и «не отвечает» — тоже
                // решение.
                target.Execution.CurrentInteraction == InteractionType.Abuse ||
                target.Mind.PendingAbuseFrom is not null)
            {
                continue;
            }

            // Кто на него идёт: сцепленные (CombatOpponentNpcId на него) —
            // с любой дистанции; погоня (Defend/GroupHunt с ним как целью) —
            // в радиусе готовности.
            NPCState nearest = null;
            var nearestDist = int.MaxValue;
            foreach (var attacker in world.Entities.Npcs.Values)
            {
                if (attacker.Id.Equals(target.Id) || attacker.Health <= 0f)
                {
                    continue;
                }

                // «Сцеплен» = пара + ЖИВОЙ бой. MobSystem гасит IsFighting у
                // всех в начале среднего прохода, а владеющая боем система
                // (сцена/налёт/охота) перезащёлкивает его заново — значит
                // пара без флага к этому месту прохода — ПРИЗРАК: бой давно
                // кончился, а сцепку никто не отпустил. Такой призрак после
                // первой же сцены замораживал чужака навсегда.
                var paired = attacker.Mind.CombatOpponentNpcId is { } oppId &&
                    oppId.Equals(target.Id);
                if (paired && !attacker.IsFighting &&
                    attacker.Mind.CurrentGoal != GoalType.Raid &&
                    attacker.Mind.CurrentGoal != GoalType.Abuse &&
                    attacker.Mind.CurrentGoal != GoalType.Defend &&
                    attacker.Mind.CurrentGoal != GoalType.GroupHunt &&
                    attacker.Mind.CurrentGoal != GoalType.Prey &&
                    attacker.Execution.CurrentInteraction != InteractionType.Abuse)
                {
                    attacker.Mind.CombatOpponentNpcId = null;
                    FightScene.ReleaseSwingSlot(attacker);
                    continue;
                }

                var engaged = paired && attacker.IsFighting;
                var pursuing =
                    (attacker.Mind.CurrentGoal == GoalType.Defend &&
                     attacker.Mind.CombatAssistAttackerNpcId is { } aId &&
                     aId.Equals(target.Id)) ||
                    (attacker.Mind.CurrentGoal == GoalType.GroupHunt &&
                     attacker.Mind.GroupHuntTargetNpcId is { } hId &&
                     hId.Equals(target.Id));
                if (!engaged && !pursuing)
                {
                    continue;
                }

                var dist = HexSpatialMath.HexDistance(attacker.Tile, target.Tile);
                if (!engaged && dist > Spec57.AnswerReadyRadiusTiles)
                {
                    continue;
                }

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = attacker;
                }
            }

            if (nearest is null)
            {
                continue;
            }

            // §109.8: разбитый ВЫХОДИТ из размена — бежит домой, как налётчик
            // при BreakOff (тот же порог). «Бей в ответ» без «отступи, когда
            // разбит» оказалось смертным приговором: клапаны отхода есть у
            // налёта (0.55) и у квари охоты (0.85), а отвечающий дрался до
            // разбитой головы — чужак погибал раньше, чем колония успевала
            // накопить ненависть на сговор §108. Одержимого (цель Abuse)
            // клапан не трогает — его перебивает только нокаут (§81.14).
            if (target.Health < Spec72.RaidFleeHealth &&
                target.Mind.CurrentGoal != GoalType.Abuse &&
                MobSystem.TryFleeToCamp(world, target,
                    $"Beaten by NPC{nearest.Id.Value}"))
            {
                target.Mind.CombatOpponentNpcId = null;
                FightScene.ReleaseSwingSlot(target);
                continue;
            }

            // Уже отвечает живому противнику — не перебивать его размен.
            if (target.IsFighting &&
                target.Mind.CombatOpponentNpcId is { } currentOpp &&
                world.Entities.Npcs.TryGetValue(currentOpp, out var currentFoe) &&
                currentFoe.Health > 0f)
            {
                continue;
            }

            // §81.14: идущий докапываться под ударами ОТВЕЧАЕТ, но похода не
            // бросает: пара ставится, план и цель — нет. Заморозка (первая
            // версия) рвала его план каждый средний тик и мигала
            // «пить↔гнобить»; полное игнорирование (вторая) делало его грушей
            // для защитниц — стоял, пока не падал.
            if (target.Mind.CurrentGoal == GoalType.Abuse)
            {
                if (InteractionReach.CanStrike(world, target, nearest))
                {
                    target.IsFighting = true;
                    var freshPair = target.Mind.CombatOpponentNpcId is not { } po ||
                        !po.Equals(nearest.Id);
                    target.Mind.CombatOpponentNpcId = nearest.Id;
                    if (freshPair)
                    {
                        Trace.Emit(world, target.Id, "AnswersBlows",
                            $"Attacker=NPC{nearest.Id.Value} Dist={nearestDist} KeptGoal=Abuse");
                    }
                }
                continue;
            }

            // Бросает дела: сборщик под ножами — покойник.
            if (target.Plan.Status == PlanStatus.Active ||
                target.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, target,
                    $"Attacked by NPC{nearest.Id.Value}");
                target.Mind.CurrentGoal = GoalType.None;
            }

            // Лучшее оружие, что есть, — никаких сценных ограничений.
            target.Mind.ForcedMeleeWeaponId = null;

            // §109.13: стойка — ТОЛЬКО когда бой в самом деле начался. Флаг
            // стоял до проверки досягаемости, и «в бою без пары» набегало
            // десятками тиков: вид честно ставил боевую позу человеку, до
            // которого противник ещё бежит, и походка ломалась ни за что.
            if (InteractionReach.CanStrike(world, target, nearest))
            {
                target.IsFighting = true;
                // Трасса — только на НОВУЮ сцепку: перезащёлкивание идёт
                // каждый средний тик (MobSystem гасит IsFighting у всех), и
                // без этой кромки одна драка давала сотни одинаковых строк.
                var fresh = target.Mind.CombatOpponentNpcId is not { } prevOpp ||
                    !prevOpp.Equals(nearest.Id);
                target.Mind.CombatOpponentNpcId = nearest.Id;
                if (fresh)
                {
                    Trace.Emit(world, target.Id, "AnswersBlows",
                        $"Attacker=NPC{nearest.Id.Value} Dist={nearestDist}");
                }
            }
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

        // §81.11: world-гейта по льготным дням здесь больше НЕТ — грейс стал
        // персональным (AbuseMath.GraceHolds: календарь ИЛИ одержимость) и
        // проверяется ниже, в цикле по абьюзерам. Латч-цикл поэтому обязан
        // работать и до тика 48000: одержимая сцена возможна с первого дня.

        // §103: ⭐ ПЕРЕЗАЩЁЛКНУТЬ БОЙ ИДУЩЕЙ СЦЕНЕ.
        //
        // MobSystem каждый средний проход гасит IsFighting у ВСЕХ и заново
        // выводит его из собачьих сцепок; налёту флаг возвращает код ниже, а
        // сцене абьюза не возвращал никто. Исполнитель ставил его на быстром
        // слое, и на каждом среднем тике флаг снова падал — из четырёх тиков
        // секунды NPC был «в бою» три. Для модели почти незаметно, а вид читает
        // IsFighting как боевую стойку, и сцена шла в мирной позе с мигающими
        // замахами.
        //
        // Здесь — сразу после MobSystem, тем же приёмом, что у налёта.
        foreach (var scene in world.Entities.Npcs.Values)
        {
            if (scene.Mind.CurrentGoal != GoalType.Abuse ||
                scene.Execution.Status != ExecutionStatus.InProgress ||
                scene.Mind.AbuseTargetNpcId is not { } sceneMark ||
                !world.Entities.Npcs.TryGetValue(sceneMark, out var sceneVictim))
            {
                continue;
            }

            FightScene.Latch(world, scene, sceneVictim);

            // §109: свидетельницы решают вписаться ВСЮ сцену, а не только на
            // её старте — как у собак и налёта, каждый средний проход. Та, что
            // подошла на пятом тике сцены, тоже видит драку.
            CombatHelpSystem.RallyFriends(world, sceneVictim, null, scene.Id,
                $"Abuse=NPC{scene.Id.Value}");

            // §109: вписавшиеся защитницы дерутся и в сцене абьюза, не только
            // в налёте. Раньше их сюда не тянул никто: PullDefenders работал
            // лишь для goal==Raid, и подруга с целью Defend стояла рядом со
            // сценой, не нанося ни одного удара.
            PullDefenders(world, scene, sceneVictim);
        }

        // §81.11: причина «почему НЕ абьюзит» видна в трассе, а не вычисляется
        // четырьмя археологами. Троттл как у RaidScored: раз в 64 тика (medium
        // тикает каждый 4-й, 64 % 4 == 0 — строка гарантированно случается).
        // Причины считаются только на тике эмита; в остальные тики цепочка
        // ниже стоит ровно столько же, сколько стоила.
        var explain = world.Tick % 64 == 0;

        foreach (var abuser in world.Entities.Npcs.Values)
        {
            if (abuser.Faction == Faction.Colony ||
                abuser.Health <= 0f)
            {
                continue;
            }

            if (abuser.Mind.CurrentGoal == GoalType.Abuse)
            {
                // §81.14: одержимость — это и есть адреналин. Пока он идёт
                // докапываться, тело не даст ему заснуть на полдороге: без
                // подпитки поход, взятый на последней энергии, кончался
                // «вырубился в трёх гексах от жертвы». Продлеваем, когда
                // осталось меньше половины, — иначе трейс шумел бы каждые
                // четыре тика.
                if (abuser.Mind.AdrenalineUntilTick - world.Tick <
                    SimBalance.AdrenalineTicks / 2)
                {
                    DamageReactionSystemHelpers.GrantAdrenaline(
                        world, abuser, 1f, "AbuseObsession");
                }

                // Сцена/поход уже идут — это не блокировка, AbuseBlocked
                // молчит. Но §81.12: по пути он смотрит по сторонам.
                TryRetargetCloserMark(world, abuser);
                continue;
            }

            if (AbuseMath.GraceHolds(world, abuser))
            {
                if (explain)
                {
                    var graceEnd = Spec81.AbuseGraceDays * EnvironmentSystem.DayLengthTicks;
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=Grace Social={abuser.Needs.Social:F2} " +
                        $"Left={graceEnd - world.Tick}");
                }
                continue;
            }

            if (world.Tick < abuser.Mind.AbuseCooldownUntilTick)
            {
                if (explain)
                {
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=Cooldown Until={abuser.Mind.AbuseCooldownUntilTick}");
                }
                continue;
            }

            if (abuser.IsUnconscious(world.Tick) ||
                abuser.Body.IsProne ||
                !abuser.Body.CanUseToolsOrWeapons)
            {
                if (explain)
                {
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=Unfit Uncon={abuser.IsUnconscious(world.Tick)} " +
                        $"Prone={abuser.Body.IsProne} " +
                        $"Hands={abuser.Body.CanUseToolsOrWeapons}");
                }
                continue;
            }

            // §81.16 (баг #3): сильно ранен — витальная зона (голова/грудь/
            // таз) потеряла больше половины. Новую сцену не начинает; идущую
            // (goal==Abuse, выше) и самозащиту гейт не трогает.
            if (AbuseMath.BadlyWounded(abuser))
            {
                if (explain)
                {
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=Wounded Vital={abuser.Body.VitalHealth():F2} " +
                        $"Floor={Spec81.AbuseWoundedVitalFloor:F2}");
                }
                continue;
            }

            if (abuser.IsFighting ||
                abuser.Mind.CurrentGoal == GoalType.Raid ||
                abuser.Mind.CurrentGoal == GoalType.Flee)
            {
                if (explain)
                {
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=Busy Goal={abuser.Mind.CurrentGoal} " +
                        $"Fighting={abuser.IsFighting}");
                }
                continue;
            }

            if (AbuseMath.Drive(abuser) <= 0f)
            {
                if (explain)
                {
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=NoDrive Social={abuser.Needs.Social:F2} " +
                        $"H={abuser.Needs.Hunger:F2} W={abuser.Needs.Thirst:F2}");
                }
                continue;
            }

            var mark = AbuseMath.BestMark(world, abuser, out var hasLoot, out var tally);
            if (mark is null)
            {
                if (explain)
                {
                    Trace.Emit(world, abuser.Id, "AbuseBlocked",
                        $"Reason=NoMark {tally.ToMessage()}");
                }
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

    // §81.12: он бежит через полкарты к выбранной — а по пути ближе прошла
    // другая. Держаться старой в этот момент читается как телепатия («он ЗНАЕТ,
    // что дальняя лучше»); человек передумывает. Гистерезис
    // AbuseRetargetGainTiles бережёт от метания между равноудалёнными —
    // пинг-понг ретаргета уже стоил нам охоты на краба (§29F.2).
    private static void TryRetargetCloserMark(WorldState world, NPCState abuser)
    {
        // Сцена уже идёт — поздно передумывать.
        if (abuser.Execution.Status == ExecutionStatus.InProgress)
        {
            return;
        }

        if (abuser.Mind.AbuseTargetNpcId is not { } currentId ||
            !world.Entities.Npcs.TryGetValue(currentId, out var current))
        {
            // Рыскал без цели (prowl §90) — и кто-то показался. Оборвать
            // поход; ближайшее планирование возьмёт её через ResolveAbuseMark.
            if (abuser.Plan.Status == PlanStatus.Active &&
                AbuseMath.BestMark(world, abuser, out _) is { } spotted)
            {
                PlanInterruption.Abort(world, abuser, $"Spotted NPC{spotted.Id.Value}");
                Trace.Emit(world, abuser.Id, "AbuseSpotted",
                    $"Mark=NPC{spotted.Id.Value} " +
                    $"Dist={HexSpatialMath.HexDistance(abuser.Tile, spotted.Tile)}");
            }
            return;
        }

        var currentDistance = HexSpatialMath.HexDistance(abuser.Tile, current.Tile);
        var best = AbuseMath.BestMark(world, abuser, out var hasLoot);
        if (best is null || best.Id.Equals(currentId))
        {
            return;
        }

        var bestDistance = HexSpatialMath.HexDistance(abuser.Tile, best.Tile);
        if (bestDistance + Spec81.AbuseRetargetGainTiles > currentDistance)
        {
            return;
        }

        // Снять заявку со старой ОБЯЗАТЕЛЬНО — иначе она помечена навсегда
        // и её не выберет никто (тот же инвариант, что в AbandonAbuse).
        if (current.Mind.PendingAbuseFrom is { } claimed && claimed.Equals(abuser.Id))
        {
            current.Mind.PendingAbuseFrom = null;
        }

        abuser.Mind.AbuseTargetNpcId = best.Id;
        abuser.Mind.AbuseHasLoot = hasLoot;
        abuser.Mind.AbuseBeat = 0;
        abuser.Mind.AbuseBlows = 0;

        if (abuser.Plan.Status == PlanStatus.Active)
        {
            PlanInterruption.Abort(world, abuser, $"Retarget NPC{best.Id.Value}");
        }

        Trace.Emit(world, abuser.Id, "AbuseRetarget",
            $"From=NPC{currentId.Value} To=NPC{best.Id.Value} " +
            $"Dist={currentDistance}->{bestDistance}");
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
        // §104 r7: слот замаха отпускает ОДНО место — иначе оборванная мимо
        // FightScene.End сцена оставляла бойца с сентинелом «больше не бью» и
        // без замахов навсегда.
        FightScene.ReleaseSwingSlot(npc);
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
            SocialCueSignals.Stamp(world, defender, "HelpCryDefended:npc", victim.Id);
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
