using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §105: единственное место, где решается «умерла или ещё умирает».
//
// До §105 этот вопрос был размазан по восьми сайтам урона, и каждый отвечал на
// него одинаковой парой строк: `if (VitalDestroyed) { Health = 0; трасса; }`.
// Восемь копий одного правила — это восемь мест, где новое правило забудут.
// Теперь сайты зовут ResolveTrauma и не знают ничего ни про окно, ни про
// запас, ни про то, что голова — исключение.
//
// ⭐ Ключевой инвариант: УМИРАЮЩАЯ ЖИВА. Health держится над нулём (витальные
// зоны пиннятся на Spec105.BodyFloor), потому что около сорока мест в
// симуляции читают `Health <= 0f` как «труп» — и если бы умирающая проходила
// эту проверку, её перестали бы видеть ровно те системы, которые должны над
// ней склониться. Смерть по-прежнему наступает ровно одним способом: Health
// падает в ноль, и свип MobSystem уносит тело в Corpses.
internal static class MortalityHelpers
{
    private static readonly BodyPart[] AllBodyParts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    // ---- Кровотечение: одно определение на всю симуляцию ---------------------

    // Spec 40.2/§50: худшая зона для расчёта кровотечения (у культи свой пол —
    // без него ампутация кровит на максимуме весь период свёртывания).
    internal static float WorstBleedPart(NPCState npc)
    {
        var worst = 1f;
        foreach (var part in AllBodyParts)
        {
            var partHealth = npc.Body.IsSevered(part)
                ? System.Math.Max(npc.Body.Parts[part], Spec50.StumpBleedPartFloor)
                : npc.Body.Parts[part];
            if (partHealth < worst)
            {
                worst = partHealth;
            }
        }

        return worst;
    }

    // Spec 44: свёртывание — кровит только СВЕЖАЯ рана. Как только она начала
    // закрываться, кровь останавливается, поэтому смертельное окно — первые
    // часы после увечья, а не все двое суток заживления.
    internal static bool HasFreshWound(NPCState npc)
    {
        foreach (var wound in npc.Wounds)
        {
            if (wound.Heal01 < 0.3f && wound.Severity >= 0.05f)
            {
                return true;
            }
        }

        return false;
    }

    // Тот самый гейт, которым NeedsDecaySystem решает «кровит сейчас или нет».
    // Живёт здесь, а не копией в каждом читателе: §105 спрашивает ровно этот
    // вопрос, чтобы понять, отпустило ли кровопотерю, и две редакции одного
    // условия разошлись бы на первой же правке.
    internal static bool IsBleeding(NPCState npc) =>
        WorstBleedPart(npc) < 0.4f && HasFreshWound(npc);

    // ---- Вход ---------------------------------------------------------------

    // Витальные зоны не опускаются ниже пола — см. инвариант в шапке файла.
    private static void PinVitals(NPCState npc)
    {
        var floor = Spec105.BodyFloor;
        if (npc.Body.Parts[BodyPart.Head] < floor)
        {
            npc.Body.Parts[BodyPart.Head] = floor;
        }

        if (npc.Body.Parts[BodyPart.Torso] < floor)
        {
            npc.Body.Parts[BodyPart.Torso] = floor;
        }

        npc.Health = npc.Body.Mean();
    }

    // Spec §60.2a: тело на земле лежит в ЦЕНТРЕ своего гекса, а свободный
    // джанкшн ищется только чтобы застолбить лежачий след (§29G) — соседки
    // обходят тело. Общий примитив для комы (§60) и умирания (§105): две
    // редакции этого сканирования разъехались бы, и одно из тел начало бы
    // свешиваться с кромки гекса.
    internal static void AnchorLyingBody(WorldState world, NPCState npc)
    {
        ExecutionSystem.LieDownCentered(world, npc);

        var center = npc.Position;
        JunctionId? spot = null;
        var best = float.MaxValue;
        if (world.Tiles.Items.TryGetValue(npc.Tile, out var tile))
        {
            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                    junction.Blocked ||
                    !SpatialQueries.IsJunctionFree(world, junctionId))
                {
                    continue;
                }

                var d = HexSpatialMath.Distance(junction.WorldPosition, center);
                if (d < best)
                {
                    best = d;
                    spot = junctionId;
                }
            }
        }

        if (spot is { } lieSpot)
        {
            npc.CurrentJunction = lieSpot;
            SpatialMutations.OccupyJunction(world, lieSpot, npc.Id);
            ExecutionSystem.ClaimLyingFootprint(world, npc, lieSpot);
        }
        else if (npc.CurrentJunction is { } here)
        {
            // Тесный гекс — свободного джанкшна нет; застолбить там, где лежит.
            ExecutionSystem.ClaimLyingFootprint(world, npc, here);
        }
    }

    // §105: тело падает и начинает умирать. Идемпотентно.
    internal static void EnterDying(WorldState world, NPCState npc, DyingCause cause)
    {
        if (!Spec105.DyingEnabled || cause == DyingCause.None || npc.IsDying)
        {
            return;
        }

        npc.Mind.DyingCause = cause;
        npc.Mind.DyingReserve = 1f;
        npc.Mind.DyingTickStamp = world.Tick;

        // Умирание глубже комы и заменяет её: иначе пробуждение по крови
        // (§60, порог 0.35) вытащило бы её из окна мимо всей этой механики.
        npc.Mind.ComaCause = ComaCause.None;
        npc.Mind.FaintedUntilTick = 0;

        PinVitals(npc);
        PlanInterruption.Abort(world, npc, "Collapsed — dying");
        npc.Mind.CurrentGoal = GoalType.None;
        npc.IsFighting = false; // тело, которое только что выключилось, не держит стойку
        AnchorLyingBody(world, npc);

        Trace.Emit(world, npc.Id, "Collapsed",
            $"Cause={cause} Health={npc.Health:F2} Blood={npc.Needs.Blood:F2} " +
            $"Hunger={npc.Needs.Hunger:F2} Thirst={npc.Needs.Thirst:F2}");
    }

    // ---- Тик ----------------------------------------------------------------

    private static int WindowTicks(DyingCause cause) => cause switch
    {
        DyingCause.BloodLoss => Spec105.WindowTicksBloodLoss,
        DyingCause.TorsoDestroyed => Spec105.WindowTicksTorso,
        DyingCause.Starvation => Spec105.WindowTicksStarvation,
        DyingCause.Dehydration => Spec105.WindowTicksDehydration,
        _ => Spec105.WindowTicksTorso
    };

    // Причина ушла — неважно, чьими руками. Симметрия намеренная: организм,
    // чья рана заклоттилась сама, встаёт по тому же правилу, что и спасённая.
    private static bool Recovered(NPCState npc, DyingCause cause) => cause switch
    {
        DyingCause.BloodLoss =>
            !IsBleeding(npc) && npc.Needs.Blood > Spec105.BloodExitFloor,
        DyingCause.TorsoDestroyed =>
            npc.Body.Parts[BodyPart.Torso] > Spec105.BodyFloor,
        DyingCause.Starvation =>
            npc.Needs.Hunger < SimBalance.StarveDeathThreshold,
        DyingCause.Dehydration =>
            npc.Needs.Thirst < SimBalance.StarveDeathThreshold,
        _ => true
    };

    // Над ней прямо сейчас работают НАД ТЕМ, ЧТО ЕЁ УБИВАЕТ. Запас на это время
    // замирает — доиграть перевязку помощница успевает всегда.
    //
    // ⭐ Совпадение с причиной — не придирка, а защита от бессмертия. Замри
    // запас от ЛЮБОЙ помощи, и соседка, поднёсшая воды истекающей кровью,
    // останавливала бы её смерть на все семьдесят тиков кормления — а голодная
    // колония кормит друг друга постоянно. Тест поймал это как «умирающая не
    // умерла за 3000 тиков»: её просто по очереди подкармливали.
    private static bool IsBeingAided(WorldState world, NPCState npc)
    {
        if (npc.Mind.PendingAidFrom is not { } helperId ||
            !world.Entities.Npcs.TryGetValue(helperId, out var helper) ||
            helper.Execution.Status != ExecutionStatus.InProgress)
        {
            return false;
        }

        return npc.Mind.DyingCause switch
        {
            DyingCause.Starvation => helper.Execution.CurrentInteraction == InteractionType.FeedOther,
            DyingCause.Dehydration => helper.Execution.CurrentInteraction == InteractionType.HydrateOther,
            _ => helper.Execution.CurrentInteraction is
                InteractionType.TreatOther or InteractionType.MedicateOther
        };
    }

    // §105: один шаг обратного отсчёта. Зовётся со Slow-слоя, но считает по
    // ФАКТИЧЕСКИ прошедшим тикам, поэтому окна в Spec105 — это честные тики, а
    // не «столько-то вызовов», и перенос системы на другой слой их не сдвинет.
    internal static void TickDying(WorldState world, NPCState npc)
    {
        if (!npc.IsDying)
        {
            return;
        }

        var cause = npc.Mind.DyingCause;
        if (Recovered(npc, cause))
        {
            ExitDying(world, npc, "Recovered");
            return;
        }

        var stamp = npc.Mind.DyingTickStamp;
        npc.Mind.DyingTickStamp = world.Tick;
        if (stamp <= 0 || stamp >= world.Tick)
        {
            // Первый шаг после входа или после загрузки сейва (штамп не
            // сериализуется): перештамповать и пропустить вычет.
            return;
        }

        if (Spec105.AidFreezesReserve && IsBeingAided(world, npc))
        {
            return;
        }

        var window = WindowTicks(cause) * AttributeMath.DyingHoldMult(npc, cause);
        npc.Mind.DyingReserve -= (world.Tick - stamp) / System.MathF.Max(1f, window);

        if (npc.Mind.DyingReserve <= 0f)
        {
            Die(world, npc, cause);
        }
        else
        {
            Trace.Emit(world, npc.Id, "Dying",
                $"Cause={cause} Reserve={npc.Mind.DyingReserve:F3}");
        }
    }

    // Урон по лежащей срезает запас: волк догрызает.
    private static void BleedReserve(WorldState world, NPCState npc, float landed, string source)
    {
        var cause = npc.Mind.DyingCause;
        npc.Mind.DyingReserve -= landed * Spec105.DamageReserveFactor;
        PinVitals(npc);

        if (npc.Mind.DyingReserve <= 0f)
        {
            Die(world, npc, cause, source);
        }
    }

    // ---- Исходы -------------------------------------------------------------

    // Запас кончился. Событие-причина эмитится ЗДЕСЬ, потому что MobSystem
    // собирает Cause= для DeathRecord из трассы за последние 240 тиков, а свип
    // подберёт тело только следующим Medium-проходом.
    private static void Die(WorldState world, NPCState npc, DyingCause cause, string source = null)
    {
        var eventType = cause switch
        {
            DyingCause.BloodLoss => "BledOut",
            DyingCause.Starvation or DyingCause.Dehydration => "StarvedToDeath",
            _ => "VitalPartDestroyed"
        };

        npc.Mind.DyingCause = DyingCause.None;
        npc.Mind.DyingReserve = 0f;
        npc.Mind.DyingTickStamp = 0;
        npc.Health = 0f;

        Trace.Emit(world, npc.Id, eventType,
            source is null
                ? $"Dying window ran out ({cause})"
                : $"Dying window cut short by {source} ({cause})");
    }

    // §105: выкарабкалась. Причина безразлична — важно, что показатель ушёл
    // из красной зоны. Дальше несколько часов она еле ходит (Convalescent).
    internal static void ExitDying(WorldState world, NPCState npc, string reason)
    {
        if (!npc.IsDying)
        {
            return;
        }

        var cause = npc.Mind.DyingCause;
        npc.Mind.DyingCause = DyingCause.None;
        npc.Mind.DyingReserve = 0f;
        npc.Mind.DyingTickStamp = 0;
        npc.Mind.ConvalescentUntilTick = world.Tick + Spec105.ConvalescentTicks;

        // Как после сна/комы: встать и прийти в себя (§41.5), отпустить
        // лежачий след и джанкшн, который держало тело.
        npc.Mind.WakeGraceUntilTick = world.Tick + AiBalance.WakeGraceTicks;
        ExecutionSystem.ReleaseClaims(world, npc);
        if (npc.CurrentJunction is { } lay)
        {
            SpatialMutations.FreeJunction(world, lay, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lay, npc.Id);
        }

        Trace.Emit(world, npc.Id, "Rescued",
            $"Cause={cause} Reason={reason} Health={npc.Health:F2} Blood={npc.Needs.Blood:F2}");
    }

    // §53: помощь довела показатель до выхода — проверить прямо на месте, а не
    // ждать следующего Slow-тика (иначе спасённая ещё полтора десятка тиков
    // лежит «умирающей» уже после того, как её напоили).
    internal static void TryExitAfterAid(WorldState world, NPCState npc)
    {
        if (npc.IsDying && Recovered(npc, npc.Mind.DyingCause))
        {
            ExitDying(world, npc, "Aided");
        }
    }

    // ---- Единая развилка сайтов урона ---------------------------------------

    // Зовётся КАЖДЫМ сайтом урона по человеку, после того как урон списан с
    // зоны. Три исхода: голова в ноль — мгновенная смерть (единственный
    // оставшийся мгновенный исход, намеренно); уже умирает — удар срезает
    // запас; грудь в ноль — тело падает и начинает умирать.
    internal static void ResolveTrauma(WorldState world, NPCState npc, float landed, string source)
    {
        if (!Spec105.DyingEnabled)
        {
            // Кил-свитч: доигровое поведение, вплоть до текста трассы.
            if (npc.Body.VitalDestroyed(out var vital))
            {
                npc.Health = 0f;
                Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"{vital} destroyed by {source}");
            }

            return;
        }

        // Голова — исключение: её разбили, и никакое окно тут не помогает.
        if (npc.Body.Parts[BodyPart.Head] <= 0f)
        {
            npc.Mind.DyingCause = DyingCause.None;
            npc.Health = 0f;
            Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"Head destroyed by {source}");
            return;
        }

        if (npc.IsDying)
        {
            BleedReserve(world, npc, landed, source);
            return;
        }

        if (npc.Body.Parts[BodyPart.Torso] <= 0f)
        {
            Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"Torso destroyed by {source}");
            EnterDying(world, npc, DyingCause.TorsoDestroyed);
        }
    }
}

}
