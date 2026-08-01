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

        // Прибытие буквальное — стоять НА забронированном узле (§26.3 r2).
        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Plan.TargetJunctionId is { } wantJunction &&
            (npc.CurrentJunction is not { } atJunction || !atJunction.Equals(wantJunction)))
        {
            return;
        }

        // --- Условия, при которых сцена рассыпается на любом такте -----------
        //
        // Дистанция меряется СОЦИАЛЬНОЙ меркой, а не боевой. Первый заход мерил
        // MeleeSwing.InReach — и сцена не начиналась НИ РАЗУ (71 срыв на 4
        // сидах): подход бронируется «на расстоянии вытянутой руки» (0.9R,
        // потолок Aid = 1.3R), а это ДАЛЬШЕ, чем достаёт кулак. Он честно
        // доходил до забронированного узла и там же обрывал сцену, потому что
        // «слишком далеко, чтобы ударить». Разговор начинается на дистанции
        // разговора; бить он потом будет с этой же точки — ApplyHumanBlow
        // собственной проверки дальности не делает.
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
        if (!InteractionReach.CheckStart(world, npc, mark.Position,
                InteractionReach.Talk, $"Abuse NPC{markId.Value}"))
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

        if (RaidMath.AlliesAround(world, mark) >= Spec81.AbuseBreakOffDefenders ||
            npc.Health < Spec72.RaidFleeHealth)
        {
            SocialCueSignals.Stamp(world, npc, "AbuseFled", mark.Id);
            AbortAbuse(world, npc, "Outnumbered");
            return;
        }

        // --- Начало ----------------------------------------------------------
        if (npc.Execution.Status == ExecutionStatus.None)
        {
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

        // --- Такт 3: пара тычков ---------------------------------------------
        if (npc.Mind.AbuseBeat < 2 && elapsed >= Spec81.AbuseBeatBlowTicks)
        {
            npc.Mind.AbuseBeat = 2;
            Shove(world, npc, mark);
            return;
        }

        if (npc.Mind.AbuseBeat < 3 && elapsed >= Spec81.AbuseBeatBlowSecondTicks)
        {
            npc.Mind.AbuseBeat = 3;
            Shove(world, npc, mark);
            return;
        }

        // --- Такт 4: приговор -------------------------------------------------
        if (npc.Mind.AbuseBeat < 4 && elapsed >= Spec81.AbuseBeatVerdictTicks)
        {
            npc.Mind.AbuseBeat = 4;
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
            string taken = null;
            var want = AbuseMath.Wants(npc);
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
    private static void Shove(WorldState world, NPCState abuser, NPCState mark)
    {
        if (abuser.Mind.AbuseBlows >= Spec81.AbuseMaxBlows)
        {
            return;
        }

        abuser.Mind.AbuseBlows++;
        SocialCueSignals.Stamp(world, abuser, "AbuseStruck", mark.Id);
        SocialCueSignals.Stamp(world, mark, "AbuseHurt", abuser.Id);

        // Полуживую не бьют — пугать её незачем, а добить сцена не должна.
        if (mark.Health <= Spec81.AbuseNoBlowHealthFloor)
        {
            Trace.Emit(world, abuser.Id, "AbuseShoveSkipped",
                $"Mark=NPC{mark.Id.Value} Health={mark.Health:F2}");
            return;
        }

        // §91: по умолчанию — РУКОПАШКА. Кулак, локоть, нога: сцена начинается
        // как наезд, а не как поножовщина, и в подавляющем большинстве случаев
        // так и заканчивается.
        //
        // ⭐ Нож достаётся не по броску кубика, а ПО ОТНОШЕНИЯМ. Пока она для
        // него просто прохожая, он машет руками; когда он её уже возненавидел —
        // а симпатия падает с каждой сценой (§81) — в ход идёт то, что в руке.
        // Это тот же принцип, что и в пощаде §86: жестокость есть следствие
        // истории отношений, а не отдельная ручка.
        var affinity = abuser.Social.GetOrCreate(mark.Id).Affinity;
        var armed = abuser.Body.CanUseToolsOrWeapons &&
            affinity <= Spec81.AbuseWeaponAffinity;

        var weapon = GearCatalog.Fist;
        if (armed)
        {
            var best = SimBalance.BestMeleeWeapon(abuser.Inventory.Items, abuser.Body.IntactHands);
            if (!string.IsNullOrEmpty(best))
            {
                weapon = best;
            }
        }

        var damage = GearCatalog.Damage(weapon) * abuser.StrikeFactor() *
            Spec81.AbuseBlowDamageMult;
        MeleeSwing.ApplyHumanBlow(world, abuser, mark, damage, weapon, "AbuseStruck");

        // §91: и она может ОГРЫЗНУТЬСЯ. Это не решение «драться» — оно
        // принимается в приговоре, — а рефлекс: получила и ударила. Без него
        // сцена читалась как избиение столба, всегда одинаковое.
        //
        // Ответ возможен, только если она в состоянии его дать: без сознания и
        // на земле не отвечают.
        var canSnap = !mark.IsUnconscious(world.Tick) && !mark.Body.IsProne &&
            mark.Body.CanUseToolsOrWeapons;
        if (canSnap &&
            MathUtil.Hash01(world.Seed, world.Tick, mark.Id.Value, 4471) <
                Spec81.AbuseFightBackChance)
        {
            var herWeapon = SimBalance.BestMeleeWeapon(mark.Inventory.Items, mark.Body.IntactHands);
            if (string.IsNullOrEmpty(herWeapon))
            {
                herWeapon = GearCatalog.Fist;
            }

            var herDamage = GearCatalog.Damage(herWeapon) * mark.StrikeFactor() *
                Spec81.AbuseBlowDamageMult;
            MeleeSwing.ApplyHumanBlow(world, mark, abuser, herDamage, herWeapon, "AbuseFoughtBack");
            SocialCueSignals.Stamp(world, mark, "AbuseDefied", abuser.Id);
            Trace.Emit(world, mark.Id, "AbuseFoughtBack",
                $"Against=NPC{abuser.Id.Value} Weapon={herWeapon}");
        }

        // Кричать, когда тебя бьют, — правильно, и подруги должны прибежать. Но
        // зовём ТОЛЬКО дружеское прикрытие, без широкого клича §57: клич поднял
        // бы всю колонию, а порог отхода в три защитницы увёл бы его со сцены
        // раньше, чем она успела бы сдаться.
        MobSystem.RememberDanger(world, mark);
        CombatHelpSystem.RallyFriends(world, mark, null, abuser.Id,
            $"Abuse=NPC{abuser.Id.Value}");
    }

    private static void FinishAbuse(
        WorldState world, NPCState npc, NPCState mark, bool submitted, string taken)
    {
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
        if (npc.Plan.TargetJunctionId is { } jId)
        {
            SpatialMutations.FreeJunction(world, jId, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
        }

        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        PlanInterruption.Abort(world, npc, $"Abuse aborted: {reason}");
        PlanningSystem.AbandonAbuse(world, npc, reason);
    }
}

}
