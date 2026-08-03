using System.Collections.Generic;
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

// §108: форма расправы — кто с кем сцеплен, когда он бежит и чем всё кончилось.
// Удары, как всегда, не здесь: их раздаёт HumanCombatSystem по одному таймлайну
// замаха на бойца (§104), а тут только сцепки и клапаны — точно тот же раздел
// труда, что у RaidSystem с той же парой систем.
//
// Новой боевой машинерии НЕТ. «Трое бьют одного» уже умеет RaidSystem.
// PullDefenders: поставить каждой Mind.CombatOpponentNpcId — и три охотницы это
// три независимых замаха, а не три мгновенных удара в один тик.
public sealed class GroupHuntSystem : ISimulationSystem
{
    public string Name => nameof(GroupHuntSystem);

    // Средний слой — сразу после MobSystem, который каждый проход гасит
    // IsFighting у ВСЕХ и заново выводит его из собачьих сцепок. Флаг тут
    // именно перезащёлкивается, иначе три тика из четырёх охотницы шли бы в
    // мирной позе (грабли §103).
    public TickLayer Layer => TickLayer.Medium;

    private readonly List<NPCState> _hunters = new();

    public void Run(WorldState world)
    {
        if (!Spec108.GroupHuntEnabled || !Spec72.Enabled)
        {
            return;
        }

        _hunters.Clear();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.GroupHuntTargetNpcId is null)
            {
                continue;
            }

            // ⭐ Метка охоты БЕЗ цели охоты. Так бывает: цель у неё сбрасывает в
            // None любой, кто перебивает её своим боем — волк (MobSystem) или
            // он сам (RaidSystem), — и оба делают это молча. Без уборки здесь
            // охота растворялась без единого события: за шесть сидов ни одного
            // GroupHuntDone и ни одного GroupHuntFailed при живых сговорах.
            // Форма — сметание протухших ассистов из RaidSystem.
            if (npc.Mind.CurrentGoal != GoalType.GroupHunt)
            {
                EndHunt(world, npc, "GoalLost");
                continue;
            }

            _hunters.Add(npc);
        }

        if (_hunters.Count == 0)
        {
            return;
        }

        _hunters.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));

        foreach (var hunter in _hunters)
        {
            if (hunter.Mind.GroupHuntTargetNpcId is not { } quarryId ||
                !world.Entities.Npcs.TryGetValue(quarryId, out var quarry))
            {
                EndHunt(world, hunter, "TargetGone");
                continue;
            }

            // Свалился — расправа удалась. Проверяется у КАЖДОЙ, потому что
            // финальный удар мог лечь на быстром слое между проходами.
            // §105.14: притворился мёртвым — для группы это тот же «свалился»,
            // они теряют к нему интерес и расходятся (как §72 бросает налёт).
            if (quarry.Health <= 0f || quarry.IsUnconscious(world.Tick) ||
                quarry.Body.IsProne || quarry.IsPlayingDead(world.Tick))
            {
                // Свалился — но их ли это заслуга? Ни одного удара значит, что
                // его достал кто-то другой (волк, голод, другая драка), и
                // записывать это себе в победу нечестно: расплата за расправу
                // выдавалась бы за чужую работу. Замерено на сиде 313: группа
                // получила GroupHuntDone, не ударив ни разу.
                var landed = PartyBlows(world, quarryId);
                FinishHunt(world, quarryId, done: landed > 0,
                    reason: landed > 0
                        ? $"{DownReason(world, quarry)} Blows={landed}"
                        : "SomebodyElseGotHim");
                break;
            }

            // ⭐ СЧЁТ ПРОВЕРЯЕТСЯ ПЕРВЫМ — раньше, чем «кто ещё на ногах». Иначе
            // добытая победа гасится тем, что за ней последовало: две охотницы
            // отходят зализывать разбитое, группа падает ниже минимума, и
            // PartyCollapsed пишет провал поверх шести уже всаженных ударов.
            // Замер: сид 42 — 6 ударов, сид 313 — 7, порог 6, обе записаны в
            // провал. Отступление ПОСЛЕ взбучки не отменяет взбучку.
            var routBlows = PartyBlows(world, quarryId);
            if (routBlows >= Spec108.GroupHuntBlowsToRout)
            {
                FinishHunt(world, quarryId, done: true, reason: $"Routed Blows={routBlows}");
                break;
            }

            if (hunter.Health <= 0f || hunter.IsUnconscious(world.Tick) || hunter.Body.IsProne)
            {
                EndHunt(world, hunter, "HunterDown");
                continue;
            }

            // ⭐ ОТСТУПЛЕНИЕ. Его не было вовсе, и это стоило колонии: у налёта
            // право убежать есть у ОБЕИХ сторон (RaidVictimFleeHealth и
            // RaidFleeHealth), а расправа шла до последней — втроём начали,
            // втроём и легли. Замер, сид 42: три головы, разбитые машете за 700
            // тиков, колония 4→1 с одной охоты.
            //
            // Мерка — УРОН, ПОЛУЧЕННЫЙ ЗДЕСЬ, а не абсолютное здоровье: с
            // абсолютным порогом колонистка со старым рубцом (Worst=0,28 при
            // здоровье 0,84) выбывала, не получив ни одного удара, и охота
            // рассыпалась ещё на подходе. Разницу считает §81.13 — та же мысль.
            //
            // Уходит ОДНА: остальные решают за себя, и охота кончается не
            // потому, что кто-то скомандовал, а потому что в ней осталось
            // меньше двоих (PartyCollapsed ниже).
            var worstNow = MobSystem.WorstPartHealth(hunter);
            var tookOverall = hunter.Mind.GroupHuntStartHealth - hunter.Health;
            var tookWorst = hunter.Mind.GroupHuntStartWorstPart - worstNow;
            if (tookOverall >= Spec108.GroupHuntHunterFleeDamage ||
                tookWorst >= Spec108.GroupHuntHunterFleeWorstDrop)
            {
                Trace.Emit(world, hunter.Id, "GroupHuntHunterFled",
                    $"Target=NPC{quarryId.Value} Health={hunter.Health:F2} " +
                    $"Took={tookOverall:F2} WorstDrop={tookWorst:F2} " +
                    $"Blows={hunter.Mind.GroupHuntBlowsLanded}");
                EndHunt(world, hunter, "Hurt");
                MobSystem.TryStartFlee(world, hunter, 1, attackerNpcId: quarryId);
                continue;
            }

            // Бюджет охоты. Единственный настоящий выход для него, кроме воды.
            if (hunter.Mind.GoalLock is not { } huntLock ||
                huntLock.Goal != GoalType.GroupHunt ||
                world.Tick >= huntLock.EndTick)
            {
                FinishHunt(world, quarryId, done: false, reason: "Timeout");
                break;
            }

            // §106: нырнул — вода одинаково укрывает и жертву, и обидчика.
            if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, quarry))
            {
                FinishHunt(world, quarryId, done: false, reason: "TargetSwimming");
                break;
            }

            if (GroupHuntMath.PartyAlive(world, quarryId) < Spec108.GroupHuntMinRemaining)
            {
                FinishHunt(world, quarryId, done: false, reason: "PartyCollapsed");
                break;
            }

            // «Побили и прогнали» проверено ВЫШЕ, до всех развалов: счёт ударов
            // и есть успешный конец расправы. Мера проста и честна — сколько раз
            // попали, и НИКАКИХ дополнительных условий: сначала требовалось ещё
            // и «он побежал», и загнанный в угол получал всё, что влезет в
            // бюджет (сид 20260803: 131 удар при пороге 6 — забой, а не
            // взбучка).

            if (!InteractionReach.CanStrike(world, hunter, quarry))
            {
                // Ещё идёт — план ведёт. Сцепку снять, чтобы не махала в
                // пустоту через полострова.
                Unpair(hunter);
                continue;
            }

            if (hunter.Mind.CombatOpponentNpcId is null)
            {
                Trace.Emit(world, hunter.Id, "GroupHuntEngaged",
                    $"Target=NPC{quarry.Id.Value} " +
                    $"Party={GroupHuntMath.PartyAlive(world, quarryId)} " +
                    $"Affinity={hunter.Social.GetOrCreate(quarry.Id).Affinity:F2}");
            }

            hunter.IsFighting = true;
            hunter.Mind.CombatOpponentNpcId = quarry.Id;
        }

        ResolveQuarry(world);
    }

    // Его сторона. Один на один он ещё огрызается — но трое это уже не драка,
    // и он бежит В СВОЙ ЛАГЕРЬ. Не MobSystem.TryStartFlee: тот ищет ближайшее
    // ПОМЕЩЕНИЕ, а стоя у них во дворе ближайшее помещение — их же хижина
    // (те же грабли, что записаны у RaidSystem.BreakOff).
    private void ResolveQuarry(WorldState world)
    {
        var handled = new HashSet<int>();
        foreach (var hunter in _hunters)
        {
            if (hunter.Mind.GroupHuntTargetNpcId is not { } quarryId ||
                !handled.Add(quarryId.Value) ||
                !world.Entities.Npcs.TryGetValue(quarryId, out var quarry) ||
                quarry.Health <= 0f)
            {
                continue;
            }

            var inReach = 0;
            NPCState nearest = null;
            foreach (var other in _hunters)
            {
                if (other.Mind.CombatOpponentNpcId is { } opponent &&
                    opponent.Equals(quarryId) &&
                    InteractionReach.CanStrike(world, other, quarry))
                {
                    inReach++;
                    if (nearest is null || other.Id.Value < nearest.Id.Value)
                    {
                        nearest = other;
                    }
                }
            }

            if (inReach == 0 || quarry.IsUnconscious(world.Tick) || quarry.Body.IsProne ||
                quarry.IsPlayingDead(world.Tick)) // §105.14
            {
                continue;
            }

            MobSystem.RememberDanger(world, quarry);

            var stands = inReach <= 1 && quarry.Health >= Spec108.GroupHuntTargetFleeHealth;
            if (stands)
            {
                if (quarry.Plan.Status == PlanStatus.Active ||
                    quarry.Execution.Status == ExecutionStatus.InProgress)
                {
                    PlanInterruption.Abort(world, quarry, $"Cornered by NPC{nearest.Id.Value}");
                    quarry.Mind.CurrentGoal = GoalType.None;
                }

                quarry.IsFighting = true;
                quarry.Mind.CombatOpponentNpcId = nearest.Id;
                continue;
            }

            if (quarry.Mind.CurrentGoal == GoalType.Flee && quarry.Plan.Status == PlanStatus.Active)
            {
                continue; // уже бежит — не перезапускать побег каждый проход
            }

            if (TryFleeHome(world, quarry, inReach))
            {
                Trace.Emit(world, quarry.Id, "GroupHuntTargetFled",
                    $"Hunters={inReach} Health={quarry.Health:F2}");
            }
            else
            {
                // Бежать некуда — драться. Загнанный в угол бьётся (§29C.4A).
                Trace.Emit(world, quarry.Id, "GroupHuntTargetCornered",
                    $"Hunters={inReach} Health={quarry.Health:F2} — бежать некуда");
                quarry.IsFighting = true;
                quarry.Mind.CombatOpponentNpcId = nearest.Id;
            }
        }
    }

    // Побег к СВОЕЙ стоянке. Своя — потому что чужая это их лагерь, а бежать
    // от троих в их же двор было бы не побегом.
    private static bool TryFleeHome(WorldState world, NPCState quarry, int hunters)
    {
        if (quarry.CurrentJunction is not { } from ||
            !world.FactionHomes.TryGetValue(quarry.Faction, out var camp))
        {
            return false;
        }

        JunctionId? best = null;
        var bestDistance = int.MaxValue;
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (!tile.Flags.HasFlag(TileFlags.Walkable) || tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            var toCamp = HexSpatialMath.HexDistance(tile.Coord, camp);
            if (toCamp >= bestDistance)
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!SpatialQueries.IsJunctionFree(world, junctionId) ||
                    !world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                    junction.Blocked ||
                    !Connectivity.Reachable(world, from, junctionId, quarry.Body.CanJump))
                {
                    continue;
                }

                bestDistance = toCamp;
                best = junctionId;
                break;
            }
        }

        if (best is not { } refuge || refuge.Equals(from))
        {
            return false;
        }

        PlanInterruption.Abort(world, quarry, $"Run home — {hunters} of them");
        quarry.IsFighting = false;
        quarry.Mind.CombatOpponentNpcId = null;
        quarry.Mind.FleeContactSinceTick = 0;
        quarry.Mind.CurrentGoal = GoalType.Flee;
        quarry.Plan.Goal = GoalType.Flee;
        quarry.Plan.TargetJunctionId = refuge;
        quarry.Plan.TargetTile = camp;
        quarry.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = refuge
        });
        quarry.Plan.CurrentStepIndex = 0;
        quarry.Plan.Status = PlanStatus.Active;
        return true;
    }

    // Сколько ударов группа всадила в эту голову за нынешнюю охоту.
    // Сколько всадили ЕМУ за эту расправу. Читается с цели, а не складывается
    // по охотницам: уходящей счёт обнуляют, и сумма по группе таяла вместе с
    // группой — шесть ударов при пороге шесть превращались в два, и взбучка
    // записывалась в провал (сид 42). Обнуляется при сговоре и на исходе.
    private static int PartyBlows(WorldState world, EntityId quarryId) =>
        world.Entities.Npcs.TryGetValue(quarryId, out var quarry)
            ? quarry.Mind.GroupHuntBlowsTaken
            : 0;

    private static string DownReason(WorldState world, NPCState quarry) =>
        quarry.Health <= 0f ? "Killed"
        : quarry.IsUnconscious(world.Tick) ? "Unconscious"
        : quarry.IsPlayingDead(world.Tick) ? "PlayingDead" // §105.14
        : "Prone";

    // Конец охоты для ВСЕЙ группы, с расплатой. Одно место на обе концовки —
    // иначе «удалась» и «сорвалась» разошлись бы в том, что чистят.
    internal static void FinishHunt(WorldState world, EntityId quarryId, bool done, string reason)
    {
        var party = new List<NPCState>();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CurrentGoal == GoalType.GroupHunt &&
                npc.Mind.GroupHuntTargetNpcId is { } target &&
                target.Equals(quarryId))
            {
                party.Add(npc);
            }
        }

        party.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        world.Entities.Npcs.TryGetValue(quarryId, out var quarry);
        if (quarry != null)
        {
            quarry.Mind.GroupHuntBlowsTaken = 0;
        }

        foreach (var hunter in party)
        {
            if (done)
            {
                // Отомстили — отпустило. И сроднились: общее дело.
                hunter.Needs.Stress = MathUtil.Clamp01(
                    hunter.Needs.Stress - Spec108.GroupHuntStressRelief);
                foreach (var mate in party)
                {
                    if (mate.Id.Equals(hunter.Id))
                    {
                        continue;
                    }

                    var bond = hunter.Social.GetOrCreate(mate.Id);
                    bond.Affinity = MathUtil.Clamp(
                        bond.Affinity + Spec108.GroupHuntBondAffinity, -1f, 1f);
                }

                if (quarry is not null)
                {
                    // Он их за это возненавидит — это его лестница оружия §91:
                    // побитый в следующий раз возьмётся за нож.
                    var grudge = quarry.Social.GetOrCreate(hunter.Id);
                    grudge.Affinity = MathUtil.Clamp(
                        grudge.Affinity - Spec108.GroupHuntTargetGrudge, -1f, 1f);
                }
            }

            Trace.Emit(world, hunter.Id, done ? "GroupHuntDone" : "GroupHuntFailed",
                $"Target=NPC{quarryId.Value} Reason={reason} Party={party.Count} " +
                $"Stress={hunter.Needs.Stress:F2}");
            EndHunt(world, hunter, reason, announce: false);
        }
    }

    // Снять ОДНУ охотницу с охоты. Публично для планировщика: он видит то же
    // истечение бюджета на своём слое и обязан разбирать его одинаково.
    //
    // announce=false — когда строку уже написал FinishHunt за всю группу.
    // Иначе пишем: выход поодиночке (умерла, отстала, цель исчезла) не виден
    // групповым событием, и охота, растаявшая по одной, читалась в трассе как
    // «начали и ничего» — ровно так выглядел сид 12345 (5 столкновений, ни
    // одного исхода).
    internal static void EndHunt(WorldState world, NPCState npc, string reason, bool announce = true)
    {
        if (announce)
        {
            Trace.Emit(world, npc.Id, "GroupHuntLeft", $"Reason={reason}");
        }

        Unpair(npc);
        npc.IsFighting = false;
        npc.Mind.GroupHuntTargetNpcId = null;
        npc.Mind.GroupHuntStartedTick = 0;
        npc.Mind.GroupHuntBlowsLanded = 0;
        npc.Mind.AssistHoldSinceTick = 0;
        npc.Mind.GroupHuntCooldownUntilTick = world.Tick + Spec108.GroupHuntCooldownTicks;
        if (npc.Mind.GoalLock is { } huntLock && huntLock.Goal == GoalType.GroupHunt)
        {
            npc.Mind.GoalLock = null;
        }

        if (npc.Mind.CurrentGoal == GoalType.GroupHunt)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }

        PlanningSystem.SetGoalCooldown(world, npc, GoalType.GroupHunt);
    }

    private static void Unpair(NPCState npc)
    {
        npc.Mind.CombatOpponentNpcId = null;
        // §104 r7: слот замаха отпускает одно место — иначе оборванная сцена
        // оставляет бойца с сентинелом «больше не бью» навсегда.
        FightScene.ReleaseSwingSlot(npc);
    }
}

}
