using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// §81: сама сцена. Пять тактов внутри ОДНОГО взаимодействия — требование,
// плач, пара тычков, приговор, добыча.
//
// ⭐ Удары наносятся СЦЕНАРНО, а Mind.CombatOpponentNpcId не трогается вовсе.
// Это не мелочь и не лень: боевая сцепка одновременно и пара, и заявка на
// единственный слот замаха, и триггер для трёх посторонних читателей. Выставь
// её — и HumanCombatSystem на быстром слое начнёт бесконечный обмен ударами
// поверх взаимодействия, которое держит обоих на месте; закончить его будет
// некому, потому что RaidSystem расцепляет только тех, у кого цель Raid. Плюс
// MobSystem каждый средний проход гасит IsFighting, а PerceivedAgent считает
// дерущуюся «занятой» — и она пропадает из поля зрения собственных помощниц
// ровно тогда, когда они нужнее всего.
//
// Поэтому: ApplyHumanBlow напрямую, ровно AbuseMaxBlows раз, по расписанию,
// которым владеет сцена. Кулаками, не оружием.
public sealed partial class ExecutionSystem
{
    private static void RunAbuse(WorldState world, NPCState npc)
    {
        if (npc.Plan.TargetAgentId is not { } markId ||
            !world.Entities.Npcs.TryGetValue(markId, out var mark))
        {
            AbortAbuse(world, npc, "MarkVanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Movement.Status == MovementStatus.Blocked)
        {
            AbortAbuse(world, npc, "ApproachBlocked");
            return;
        }

        // §102 r3: ⭐ ПРИБЫТИЕ — ЭТО ДОСТАЛ ИЛИ НЕТ, а не «стою на том самом
        // узле». Прежнее буквальное прибытие (§26.3 r2) вешало его намертво:
        // план активен, два шага, выполнение не начинается НИКОГДА — он ждёт
        // узла, к которому не идёт, потому что идти некуда, он уже рядом.
        //
        // Снаружи это и было «зависанием»: стоит вплотную к жертве, цель «хочу
        // докопаться», тяга 3.00, здоровье 1.00 — и полчаса ничего.
        //
        // Для сцены важно ровно одно: достаёт ли он до неё. Достаёт — начинаем
        // здесь; не достаёт и не идёт — ждём, как раньше.
        if (npc.Execution.Status == ExecutionStatus.None &&
            !InteractionReach.CanStrike(world, npc, mark) &&
            npc.Plan.TargetJunctionId is { } wantJunction &&
            (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(wantJunction)))
        {
            return;
        }

        // --- Условия, при которых сцена рассыпается на любом такте -----------
        //
        // Жертва по-настоящему пропала: умерла или отключилась. Тут сцене конец.
        if (mark.Health <= 0f || mark.IsUnconscious(world.Tick))
        {
            AbortAbuse(world, npc, "MarkGone");
            return;
        }

        // §89: ⭐ ОНА ПРОСТО ОТОШЛА — он идёт следом, а не бросает затею.
        //
        // Раньше любой её шаг убивал сцену целиком: обрыв «MarkGone» случался
        // десятками за прогон, и со стороны это выглядело как «подошёл и ушёл».
        // Между тем уйти от него — самое естественное, что она может сделать.
        //
        // Поэтому сбрасывается только ВЫПОЛНЕНИЕ: цель и жертва остаются, замок
        // цели держит его на ней, и планировщик на следующем тике строит новый
        // подход. Он преследует, пока не истечёт замок.
        //
        // §102 r4: ⭐ и «пора догонять», и «можно начинать» отвечает ОДНО место —
        // InteractionReach.AssessMelee. Пока это были два условия в разных
        // строках (метрический Talk у преследования, соседство узлов у старта),
        // между ними жило кольцо, в котором молчали оба: он не догонял, потому
        // что уже близко, и не начинал, потому что рукой не достаёт. Замер на
        // арене — 2951 из 12000 тиков в этом кольце, застой 2872 тика подряд.
        // Теперь «идти» это буквально «не действовать», и третьему состоянию
        // взяться неоткуда. Гистерезис вход/удержание живёт внутри Assess.
        //
        // До узкой проверки дело доходит только СТОЯ: пока подход строится или
        // идётся, выходы выше (движение, §102 r3) возвращают раньше, поэтому
        // свежий план она не сбрасывает.
        var withinScene = npc.Execution.Status != ExecutionStatus.None;
        if (InteractionReach.AssessMelee(world, npc, mark, withinScene,
                $"Abuse NPC{markId.Value}") == MeleeApproach.Approach)
        {
            if (npc.Plan.TargetJunctionId is { } heldJunction)
            {
                SpatialMutations.FreeJunction(world, heldJunction, npc.Id);
                SpatialMutations.ReleaseJunctionReservation(world, heldJunction, npc.Id);
            }

            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Mind.AbuseBeat = 0;
            npc.Plan.Status = PlanStatus.Completed;
            npc.Plan.Steps.Clear();
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetAgentId = null;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;
            Trace.Emit(world, npc.Id, "AbusePursues",
                $"Mark=NPC{markId.Value} Dist={HexSpatialMath.HexDistance(npc.Tile, mark.Tile)}");
            return;
        }

        // §102: сбегается ТОЛПА — уходит. Это правило про людей вокруг, и оно
        // остаётся.
        if (RaidMath.AlliesAround(world, mark) >= Spec81.AbuseBreakOffDefenders)
        {
            SocialCueSignals.Stamp(world, npc, "AbuseFled", mark.Id);
            AbortAbuse(world, npc, "Outnumbered");
            return;
        }

        // ⭐ А вот абсолютный порог здоровья отсюда УБРАН, и это была не
        // мелочь. Он копировался из правил налёта («ранен — не охоться»), но
        // для наезда работал как ВЕЧНЫЙ ЗАМОК: стоило здоровью раз опуститься
        // ниже 0.55 — а оно у забронированного медленно тает от перегрева, —
        // и он не мог тронуть никого уже никогда. Снаружи это выглядело как
        // зависание: подходит, разворачивается, подходит снова, и так часами.
        //
        // Причём метка врала: срыв назывался «Outnumbered», хотя союзниц рядом
        // было ноль. На это ушёл отдельный круг диагностики.
        //
        // Его состояние и так учтено — оно входит в расклад сил (AbuseMath.Force
        // умножает на здоровье и на худшую часть тела). Избитый против крепкой
        // и вооружённой не полезет по РАСЧЁТУ, а не по абсолютному числу. А
        // избитый против слабой — вполне: пары затрещин он ещё стоит.

        // --- Начало ----------------------------------------------------------
        if (npc.Execution.Status == ExecutionStatus.None)
        {
            // §98: начинать сцену можно только с УДАРНОЙ дистанции — иначе
            // первая сцена стабильно начиналась там, откуда рука не достаёт:
            // отношения портились, а драки не было ни одной. После §102 r4
            // преследование выше меряет ТОЙ ЖЕ меркой, так что сюда доходят
            // уже достающие; проверка остаётся страховкой на случай, если
            // какой-нибудь будущий путь пустит сцену в обход неё — и идёт она
            // через ТУ ЖЕ функцию, поэтому разойтись с преследованием больше
            // не может даже при желании.
            if (InteractionReach.AssessMelee(world, npc, mark, sceneStarted: false,
                    $"Abuse NPC{markId.Value}") != MeleeApproach.Act)
            {
                return;
            }

            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Abuse;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + Spec81.AbuseDurationTicks;
            npc.Mind.AbuseBeat = 0;
            npc.Mind.AbuseBlows = 0;
            if (npc.Plan.TargetJunctionId is { } jId)
            {
                SpatialMutations.OccupyJunction(world, jId, npc.Id);
            }

            // Она бросает свои дела: когда на тебя орут в упор, посуду не моют.
            if (mark.Plan.Status == PlanStatus.Active ||
                mark.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, mark, $"Abused by NPC{npc.Id.Value}");
                mark.Mind.CurrentGoal = GoalType.None;
            }

            // §97: ⭐ ВХОД В НАСТОЯЩИЙ БОЙ. Прежде сцена била «сценарно», в
            // обход боевой пары, — и это было костылём, который тянул за собой
            // ещё два: невидимый замах и ручное открытие окна анимации.
            //
            // Теперь всё как у людей: обе стороны в бою, удары наносит
            // HumanCombatSystem по своим таймингам, замах рисуется штатно,
            // кровь и раны идут общим путём. От смерти держит пощада §86, а
            // РАНО ВЫЙТИ из боя умеет сама сцена — см. LeaveCombat.
            // §97: чем он будет бить — решает глубина неприязни, а не «что
            // получше в рюкзаке». Наезд это наезд: кулаки; тесак достают, когда
            // уже ненавидят.
            npc.Mind.ForcedMeleeWeaponId = PickAbuseWeapon(npc, mark);
            npc.Mind.AbuseBlows = 0;

            npc.IsFighting = true;
            npc.Mind.CombatOpponentNpcId = mark.Id;
            // §100: она отвечает НЕ ВСЕГДА. Испугалась — стоит и терпит, и он
            // просто пару раз бьёт. Бросок детерминированный, поэтому реплей
            // повторяется в точности.
            var answers = AbuseMath.AnswersBack(world, npc, mark);
            if (answers)
            {
                mark.IsFighting = true;
                mark.Mind.CombatOpponentNpcId = npc.Id;
            }
            MobSystem.RememberDanger(world, mark);
            CombatHelpSystem.RallyFriends(world, mark, null, npc.Id,
                $"Abuse=NPC{npc.Id.Value}");

            SocialCueSignals.Stamp(world, npc, "AbuseDemand", mark.Id);
            SocialCueSignals.Stamp(world, mark, "AbuseThreatened", npc.Id);
            Trace.Emit(world, npc.Id, "AbuseStarted",
                $"Mark=NPC{mark.Id.Value} Loot={npc.Mind.AbuseHasLoot} " +
                $"Ratio={AbuseMath.Ratio(world, npc, mark):F2} " +
                $"Social={npc.Needs.Social:F2} Hunger={npc.Needs.Hunger:F2}");
            return;
        }

        var elapsed = world.Tick - npc.Execution.StartTick;

        // --- Такт 2: она плачет ----------------------------------------------
        if (npc.Mind.AbuseBeat < 1 && elapsed >= Spec81.AbuseBeatCryTicks)
        {
            npc.Mind.AbuseBeat = 1;
            mark.Needs.Stress = MathUtil.Clamp01(mark.Needs.Stress + Spec81.AbuseMarkStressCost);
            SocialCueSignals.Stamp(world, mark, "AbuseCry", npc.Id);
            return;
        }

        // --- Такт 3: драка. Удары наносит HumanCombatSystem — сцена только
        // отмечает такт и подаёт кьюшки. ---------------------------------------
        if (npc.Mind.AbuseBeat < 3 && elapsed >= Spec81.AbuseBeatBlowTicks)
        {
            npc.Mind.AbuseBeat = 3;
            SocialCueSignals.Stamp(world, npc, "AbuseStruck", mark.Id);
            SocialCueSignals.Stamp(world, mark, "AbuseHurt", npc.Id);
            return;
        }

        // --- Такт 4: приговор. Наступает по ЧИСЛУ УДАРОВ, а не только по
        // времени: стычка кончается тем, что он своё сказал руками. --------
        // §100: приговор строго ПО ВРЕМЕНИ. Раньше он наступал ещё и по числу
        // ударов, и сцена схлопывалась за пару секунд, не успев прочитаться.
        // Сколько ударов лечь успеет — столько и ляжет, но пять секунд драки
        // будут.
        if (npc.Mind.AbuseBeat < 4 && elapsed >= Spec81.AbuseBeatVerdictTicks)
        {
            npc.Mind.AbuseBeat = 4;

            // §97: ⭐ ВЫХОД ИЗ БОЯ ИМЕННО ЗДЕСЬ. Приговор — это и есть конец
            // драки: он своё сказал руками. Дальше идут только последствия
            // (сдалась/огрызнулась, отдала припас), и махать во время них
            // некому. Без этого пара оставалась сцепленной до конца окна и
            // молотила друг друга ещё десяток тиков.
            LeaveCombat(world, npc, mark);

            // Расклад читается ЗАНОВО: за сорок тиков она могла подобрать копьё,
            // а подруга — подойти на шесть гексов.
            if (AbuseMath.Ratio(world, npc, mark) < Spec81.AbuseSubmitRatio)
            {
                SocialCueSignals.Stamp(world, mark, "AbuseDefied", npc.Id);
                SocialCueSignals.Stamp(world, npc, "AbuseRefused", mark.Id);
                Trace.Emit(world, npc.Id, "AbuseDefied",
                    $"Mark=NPC{mark.Id.Value} Ratio={AbuseMath.Ratio(world, npc, mark):F2}");

                // §85: ОТКАЗ ПЕРЕВОДИТ СЦЕНУ В БОЙ. Раньше он ворчал и уходил,
                // и со стороны это читалось как «подошёл, потоптался, ушёл» —
                // то есть как будто ничего не произошло.
                //
                // Теперь так: если его проигнорировали, он не отступает. Она
                // получила два тычка и не отдала — значит будет драка, и там
                // она уже сама решает, стоять или бежать (это умеет RaidSystem).
                //
                // Убийство отсюда возможно, и это осознанно: цена отказа должна
                // быть настоящей, иначе отказывать будут всегда.
                FinishAbuse(world, npc, mark, submitted: false, taken: null);
                RaidSystem.EscalateToRaid(world, npc, mark, "AbuseDefied");
                return;
            }

            SocialCueSignals.Stamp(world, mark, "AbuseGaveUp", npc.Id);
            SocialCueSignals.Stamp(world, npc, "AbuseSubmit", mark.Id);
            return;
        }

        // --- Такт 5: добыча ---------------------------------------------------
        if (npc.Mind.AbuseBeat < 5 && elapsed >= Spec81.AbuseBeatTakeTicks)
        {
            npc.Mind.AbuseBeat = 5;
            // §93: берём то, что у неё ЕСТЬ, а не только то, чего ему хочется.
            string taken = null;
            var want = AbuseMath.WhatToTake(world, npc, mark);
            if (want != AidKind.None)
            {
                AbuseMath.TryTake(world, npc, mark, want, out taken);
            }

            if (taken is not null)
            {
                SocialCueSignals.Stamp(world, npc, "AbuseTook", mark.Id);
            }

            FinishAbuse(world, npc, mark, submitted: true, taken: taken);
            return;
        }

        // --- Страховка: сцена не может длиться дольше своего окна -------------
        if (world.Tick >= npc.Execution.EndTick)
        {
            FinishAbuse(world, npc, mark, submitted: npc.Mind.AbuseBeat >= 4, taken: null);
        }
    }

    // Тычок для острастки. Кулаком: ему нужны её припасы и её страх, а не её
    // труп — два удара ножом загнали бы её ниже порога бегства, и сцена свалилась
    // бы в обычный налёт, не дойдя до «она сдалась».
    // §97: ⭐ РАННИЙ ВЫХОД ИЗ БОЯ. Ради этого и стоило заходить в него
    // по-настоящему: вход бесплатный, а выход — это и есть вся механика.
    //
    // Сцена не «драка до победы»: он пришёл забрать своё и напугать, а не
    // убить. Поэтому пара расцепляется по её окончании — по приговору, по
    // добыче, по подошедшим защитницам или просто по концу окна. Дальше уже
    // обычный мир: никто ни за кем не гонится, потому что цепочки нет.
    //
    // Расцеплять надо ОБЕ стороны и обязательно: HumanCombatSystem работает
    // ровно по CombatOpponentNpcId, и забытая пара — это вечная драка.
    // §97: лестница ненависти — теперь она назначает оружие настоящему бою, а
    // не рисует отдельный «сценарный» удар.
    private static string PickAbuseWeapon(NPCState abuser, NPCState mark)
    {
        var affinity = abuser.Social.GetOrCreate(mark.Id).Affinity;
        if (!abuser.Body.CanUseToolsOrWeapons || affinity > Spec81.AbuseWeaponAffinity)
        {
            return GearCatalog.Fist;
        }

        var best = SimBalance.BestMeleeWeapon(abuser.Inventory.Items, abuser.Body.IntactHands);
        if (string.IsNullOrEmpty(best))
        {
            return GearCatalog.Fist;
        }

        var depth = MathUtil.Clamp01(
            (Spec81.AbuseWeaponAffinity - affinity) /
            System.Math.Max(0.0001f, 1f + Spec81.AbuseWeaponAffinity));
        return depth >= Spec81.AbuseHeavyWeaponDepth ? best : LighterThan(abuser, best);
    }

    // Ступенька ниже самого тяжёлого — нож вместо мачете. Если ничего легче
    // нет, остаются кулаки: лёгкая злость не берётся за тесак.
    private static string LighterThan(NPCState npc, string heaviest)
    {
        string lighter = null;
        foreach (var item in npc.Inventory.Items)
        {
            var id = item.DefinitionId;
            if (id == heaviest)
            {
                continue;
            }

            var gear = GearCatalog.For(id);
            if (gear.Id != id || gear.MeleePriority <= 0)
            {
                continue;
            }

            if (lighter is null || GearCatalog.Damage(id) > GearCatalog.Damage(lighter))
            {
                lighter = id;
            }
        }

        return lighter ?? GearCatalog.Fist;
    }

    private static void LeaveCombat(WorldState world, NPCState a, NPCState b)
    {
        a.Mind.ForcedMeleeWeaponId = null;
        if (b is not null)
        {
            b.Mind.ForcedMeleeWeaponId = null;
        }

        a.IsFighting = false;
        a.Mind.CombatOpponentNpcId = null;
        a.StrikeLandsAtTick = 0;

        if (b is not null && b.Mind.CombatOpponentNpcId is { } held && held.Equals(a.Id))
        {
            b.IsFighting = false;
            b.Mind.CombatOpponentNpcId = null;
            b.StrikeLandsAtTick = 0;
        }
    }

    private static void FinishAbuse(
        WorldState world, NPCState npc, NPCState mark, bool submitted, string taken)
    {
        LeaveCombat(world, npc, mark);

        // ⭐ Ради этого всё и затевалось: сцена закрывает ЕГО нужду в общении.
        // Разговор ему недоступен (собеседники только среди своих), амбиентное
        // общение считает соседей по фракции, а фракция у него из одного
        // человека — Social падал в ноль и там оставался. Контакт силой — тоже
        // контакт, и это единственный, который у него есть.
        npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + Spec81.AbuseSocialGain);
        if (Spec81.AbuseMarkSocialGain > 0f)
        {
            mark.Needs.Social = MathUtil.Clamp01(mark.Needs.Social + Spec81.AbuseMarkSocialGain);
        }

        // §91: портится с ОБЕИХ сторон. Первая версия роняла симпатию только у
        // неё — «ему всё равно», — и это оказалось не красивой деталью, а
        // багом: оружие он достаёт именно по СВОЕЙ неприязни (§91), а она не
        // росла, значит нож не появился бы никогда.
        //
        // Да и по сути: презрение к тому, кого сам же трясёшь, копится. Он
        // теряет к ней меньше, чем она к нему, — бьют всё-таки её.
        var mine = npc.Social.GetOrCreate(mark.Id);
        mine.Affinity = MathUtil.Clamp(
            mine.Affinity - Spec81.AbuseAffinityLoss * Spec81.AbuserOwnAffinityShare, -1f, 1f);
        mine.Familiarity = MathUtil.Clamp01(mine.Familiarity + 0.05f);

        var rel = mark.Social.GetOrCreate(npc.Id);
        rel.Affinity = MathUtil.Clamp(rel.Affinity - Spec81.AbuseAffinityLoss, -1f, 1f);
        rel.Trust = MathUtil.Clamp(rel.Trust - Spec81.AbuseTrustLoss, -1f, 1f);
        rel.Familiarity = MathUtil.Clamp01(rel.Familiarity + 0.05f);

        // Бесплатный «минус» над её головой — тем же каналом, которым §28.15E
        // показывает исход разговора.
        mark.Execution.LastTalkResultTick = world.Tick;
        mark.Execution.LastTalkAffinityDelta = -Spec81.AbuseAffinityLoss;

        Trace.Emit(world, npc.Id, submitted ? "AbuseDone" : "AbuseRebuffed",
            $"Mark=NPC{mark.Id.Value} Took={taken ?? "nothing"} " +
            $"Social={npc.Needs.Social:F2} MarkAffinity={rel.Affinity:F2}");

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        if (mark.Mind.PendingAbuseFrom is { } claimed && claimed.Equals(npc.Id))
        {
            mark.Mind.PendingAbuseFrom = null;
        }

        npc.Mind.AbuseTargetNpcId = null;
        npc.Mind.AbuseBeat = 0;
        npc.Mind.AbuseBlows = 0;
        npc.Mind.AbuseHasLoot = false;
        npc.Mind.AbuseCooldownUntilTick = world.Tick + Spec81.AbuseCooldownTicks;
        // Ограбил — значит сегодня не убивает: сцена отталкивает налёт.
        npc.Mind.RaidCooldownUntilTick = System.Math.Max(
            npc.Mind.RaidCooldownUntilTick, world.Tick + Spec81.AbuseRaidLockoutTicks);

        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetAgentId = null;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.PathIndex = 0;
    }

    private static void AbortAbuse(WorldState world, NPCState npc, string reason)
    {
        if (npc.Mind.AbuseTargetNpcId is { } leavingId &&
            world.Entities.Npcs.TryGetValue(leavingId, out var leaving))
        {
            LeaveCombat(world, npc, leaving);
        }
        else
        {
            LeaveCombat(world, npc, null);
        }

        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        // Четыре сброса выполнения, стоявшие здесь, убраны: Abort строкой ниже
        // выставляет ровно их — и ещё освобождает клеймы, раскладку крафта,
        // приглашение к разговору, брони оставшихся шагов и несомую вещь.
        // Дубль перед вызовом создавал ложное впечатление, будто демонтаж тут
        // свой.
        PlanInterruption.Abort(world, npc, $"Abuse aborted: {reason}");
        PlanningSystem.AbandonAbuse(world, npc, reason);
    }
}

}
