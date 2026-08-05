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
        // §105 r4: по списку витальных зон (голова, грудь, ТАЗ), а не по паре,
        // вписанной руками — см. BodyState.VitalParts.
        foreach (var part in BodyState.VitalParts)
        {
            if (npc.Body.Parts[part] < floor)
            {
                npc.Body.Parts[part] = floor;
            }
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
        npc.Mind.CryingUntilTick = 0; // §110: слёзы тоже вытесняются
        // §105.14: и притворство — умирание глубже; ExitDying переспросит.
        npc.Mind.PlayDeadUntilTick = 0;
        npc.Mind.PlayDeadSinceTick = 0;

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
        DyingCause.VitalCrushed => Spec105.WindowTicksTorso,
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
        // ⭐ ВЫШЕ порога падения, а не «выше пола». Пол — это то, во что её
        // запиннило падение; спрашивать про него значит спрашивать «поднялась
        // ли хоть на волосок», и она вставала через один тик.
        //
        // §105 r4: по ХУДШЕЙ витальной зоне. Раз таз смертелен наравне с
        // грудью, вставать с разбитым тазом и целой грудью нельзя ровно так
        // же, как наоборот.
        DyingCause.VitalCrushed =>
            npc.Body.VitalHealth() > Spec105.VitalExitHealth,
        DyingCause.Starvation =>
            npc.Needs.Hunger < SimBalance.StarveDeathThreshold,
        DyingCause.Dehydration =>
            npc.Needs.Thirst < SimBalance.StarveDeathThreshold,
        _ => true
    };

    // Сколько тиков она уже лежит. Выводится из ЗАПАСА, а не из отдельного
    // поля: запас тает ровно по прошедшим тикам, поэтому «сколько вытекло» и
    // есть «сколько лежит» — и это переживает сохранение, ничего не добавляя в
    // блоб. (Время под руками помощницы сюда не идёт: там запас заморожен —
    // и правильно, лежать под перевязкой это не «отлёживаться».)
    private static float TicksLain(NPCState npc, DyingCause cause) =>
        (1f - npc.Mind.DyingReserve) * WindowTicks(cause) *
        AttributeMath.DyingHoldMult(npc, cause);

    // Можно ли уже вставать: и показатель вернулся, и она отлежала минимум.
    private static bool CanStandUp(NPCState npc, DyingCause cause) =>
        TicksLain(npc, cause) >= Spec105.MinDyingTicks && Recovered(npc, cause);

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
        if (CanStandUp(npc, cause))
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

        StayDownIfNeeded(world, npc);
    }

    // §105 r5: ⭐ ПЕРЕД ТЕМ КАК ВСТАТЬ — СПРОСИТЬ СЕБЯ, А НАДО ЛИ.
    //
    // Она приходила в себя, поднималась на ноги — и тут же ложилась обратно,
    // потому что первое, что решал аукцион у вымотанного тела, был сон. Со
    // стороны это читается как сбой: встала, постояла, легла.
    //
    // Теперь очнувшаяся вымотанная просто НЕ ВСТАЁТ: она переворачивается и
    // засыпает там же. Механика для этого уже есть целиком — §60 r2 «сон без
    // задних ног»: тот же лежачий покой, пробуждение по энергии 0.45, и
    // экспортёр подаёт его виду как обычный сон. Вид доигрывает это одним
    // движением (переход FallenIdle → Sleep), не поднимая её на ноги.
    //
    // Порог — тот самый, по которому аукцион и выбрал бы сон
    // (SimBalance.SleepEnergyThreshold): спрашивать надо ровно то, что она
    // решила бы сама, иначе «остаться лежать» и «пойти спать» разойдутся.
    // §105.14 добавил вторую причину остаться лежать — враг рядом. Порядок
    // важен: вымотанная сначала засыпает (сон восстанавливает, притворство —
    // нет), и только бодрая переходит к притворству.
    private static void StayDownIfNeeded(WorldState world, NPCState npc)
    {
        if (Spec105.StayDownIfSpent && npc.Needs.Energy <= SimBalance.SleepEnergyThreshold)
        {
            NeedsDecaySystem.EnterComa(world, npc, ComaCause.Exhaustion);
            Trace.Emit(world, npc.Id, "StayedDown",
                $"Too spent to get up (Energy={npc.Needs.Energy:F2}) — rolled over and slept");
            return;
        }

        TryStartPlayDead(world, npc);
    }

    // §105.14: ⭐ ПРИТВОРЯЕТСЯ МЁРТВОЙ (механика Kenshi).
    //
    // Очнулась — а рядом волк. Вставать невыгодно: собьют снова, и §105 срежет
    // запас смерти с каждого удара по лежащей. Поэтому она НЕ встаёт: лежит
    // неподвижно, пока враг не потеряет интерес и не уйдёт.
    //
    // ⭐ Половина механики живёт НА СТОРОНЕ ВРАГА, и без неё притворство было бы
    // самоубийством: в этом проекте беспомощных догрызают (MobSystem helpless,
    // RaidMath.Opportunity весит беспомощность В ПЛЮС). Поэтому IsPlayingDead
    // намеренно НЕ входит в IsUnconscious, а четыре вражьих системы получают
    // пару гейтов по форме §106 «Вода — убежище»: новая агрессия не наводится
    // И уже ведущаяся погоня бросается. Одного гейта мало — охотник вечно
    // шагал бы к недосягаемой цели (кольцо §102).
    //
    // ⭐ ГЛАВНАЯ точка чтения Spec105.PlayDeadEnabled: выключенная ручка значит
    // «окно никогда не взводится», и все проверки IsPlayingDead ниже по течению
    // становятся инертны — прежнее поведение побитово (проверено golden trace,
    // 3 сида × 4000 тиков, пресет scores).
    //
    // Ручку спрашивает ещё DecisionSystem — на краю окна обморока и плача, и
    // это НЕ дублирование по невнимательности: край там ловится обнулением
    // протухшего поля, а обнуление читается везде одинаково, но ХЭШИРУЕТСЯ
    // иначе, и с выключенной ручкой трасса расходилась на ровном месте.
    internal static bool TryStartPlayDead(WorldState world, NPCState npc)
    {
        if (!Spec105.PlayDeadEnabled ||
            npc.Health <= 0f ||
            npc.IsDying ||
            npc.Mind.ComaCause != ComaCause.None ||
            OwnCrisisOutranksHiding(npc) ||
            !HostileNearby(world, npc))
        {
            return false;
        }

        // ⭐ ИДЕМПОТЕНТНОСТЬ, и она НЕ косметическая. Обморок §40.13 поверх уже
        // притворяющейся приводит сюда второй раз, и перештамповка старта
        // обнуляла бы отсчёт потолка — предохранитель не наступал НИКОГДА
        // (сид 42: «PlayDeadStarted» каждые ~270 тиков без единого «Ended»).
        if (npc.Mind.PlayDeadSinceTick == 0)
        {
            npc.Mind.PlayDeadSinceTick = world.Tick;
        }

        npc.Mind.PlayDeadUntilTick = System.Math.Min(
            world.Tick + Spec105.PlayDeadHoldTicks,
            npc.Mind.PlayDeadSinceTick + Spec105.PlayDeadMaxTicks);
        // Путь пробуждения только что выдал грацию подъёма (§41.5) и отпустил
        // лежачий след — но вставать она передумала: грацию снять, след занять
        // обратно (её выдаст EndPlayDead, когда она действительно поднимется).
        npc.Mind.WakeGraceUntilTick = 0;

        // ⭐ СНОС ПЛАНА, зеркало EnterComa (§60) и слома в плач (§110) — и это
        // НЕ формальность ради единообразия. Обнулить одну лишь цель мало:
        // летящий план переживает укладывание, MovementSystem продолжает вести
        // тело по его шагам, и получается «бежит и притворяется мёртвой
        // одновременно» — вид рисует лежачую позу, а координаты едут (баг #7:
        // абьюзер «поскользил в анимации лежачие куда-то дальше, в закат»).
        //
        // Порядок обязателен: Abort освобождает джанкшны и брони плана, а
        // AnchorLyingBody ниже занимает лежачий след — поменяй местами, и она
        // сама себе освободит только что застолблённое место.
        PlanInterruption.Abort(world, npc, "Playing dead");
        npc.Mind.CurrentGoal = GoalType.None;
        npc.IsFighting = false; // притворяющаяся не держит боевую стойку
        AnchorLyingBody(world, npc);

        Trace.Emit(world, npc.Id, "PlayDeadStarted",
            $"Hostile within {Spec105.PlayDeadRadiusTiles} tiles — staying limp");
        return true;
    }

    // §105.14: ⭐ СОБСТВЕННЫЙ КРИЗИС СИЛЬНЕЕ ВОЛКА — притворство это тактика, а
    // не способ умереть лёжа.
    //
    // У комы §60 сонный метаболизм, у притворства его нет: нужды тают полным
    // ходом, рана течёт, а решений она не принимает. Соак выставил счёт дважды,
    // на одном и том же сиде 42 (0 выживших из 4): сперва все трупы были с
    // Thirst=1,00 в трёх шагах от воды, а когда голод с жаждой закрыли — стали
    // умирать от кровопотери, лёжа с неперевязанной раной.
    //
    // Порог крови НЕ свой: это ровно та черта, по которой §53 заставляет её
    // бросить всё и перевязаться. Заведи здесь вторую — и «встать, чтобы
    // спастись» разъехалось бы с «чем именно спасаться».
    //
    // ОДНО определение на оба гейта (не лечь / встать досрочно): две копии
    // этого предиката неизбежно разошлись бы, и она вставала бы, чтобы тут же
    // лечь обратно.
    internal static bool OwnCrisisOutranksHiding(NPCState npc) =>
        npc.Mind.IsStarving ||
        npc.Mind.IsDehydrated ||
        npc.Needs.Blood < Spec53.SelfTreatBleedBlood;

    // §105.14: враг ушёл (или вышел потолок) — теперь можно вставать. Зеркало
    // выхода из комы: грация подъёма и отпущенный лежачий след.
    internal static void EndPlayDead(WorldState world, NPCState npc, string reason)
    {
        npc.Mind.PlayDeadUntilTick = 0;
        npc.Mind.PlayDeadSinceTick = 0;
        npc.Mind.WakeGraceUntilTick = world.Tick + AiBalance.WakeGraceTicks; // §41.5

        ExecutionSystem.ReleaseClaims(world, npc);
        if (npc.CurrentJunction is { } lay)
        {
            SpatialMutations.FreeJunction(world, lay, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lay, npc.Id);
        }

        Trace.Emit(world, npc.Id, "PlayDeadEnded", $"Reason={reason} — getting up");
    }

    // §105.14: «враг рядом» — одно определение на все точки входа. Радиус —
    // своя ручка: радиус восприятия и радиус агро отмеряны под другое.
    //
    // ⭐ Каннибал §56 по фракции СОЮЗНИК, поэтому одной проверки AreHostile
    // мало: без ветки Prey притворство против него не взводилось бы вовсе, и
    // гейт в PredationSystem был бы недостижим.
    //
    // Зовётся только для лежащих (единицы за тик) — линейный проход дёшев.
    internal static bool HostileNearby(WorldState world, NPCState npc)
    {
        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f &&
                HexSpatialMath.HexDistance(mob.Tile, npc.Tile) <= Spec105.PlayDeadRadiusTiles)
            {
                return true;
            }
        }

        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Equals(npc.Id) ||
                other.Health <= 0f ||
                other.IsUnconscious(world.Tick))
            {
                continue;
            }

            var hostile = FactionRelations.AreHostile(npc, other) ||
                other.Mind.CurrentGoal == GoalType.Prey; // §56
            if (hostile &&
                HexSpatialMath.HexDistance(other.Tile, npc.Tile) <= Spec105.PlayDeadRadiusTiles)
            {
                return true;
            }
        }

        return false;
    }

    // §53: помощь довела показатель до выхода — проверить прямо на месте, а не
    // ждать следующего Slow-тика (иначе спасённая ещё полтора десятка тиков
    // лежит «умирающей» уже после того, как её напоили).
    internal static void TryExitAfterAid(WorldState world, NPCState npc)
    {
        if (npc.IsDying && CanStandUp(npc, npc.Mind.DyingCause))
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
        // Баг #9: кровь пачкает. landed — зонные единицы (одна из
        // Body.Parts.Count частей), в общем HP это landed / Count; при
        // HygieneDamageLoss = 3 суммарная треть максимума здоровья обнуляет
        // гигиену — полностью помыться. Сюда стекают ВСЕ сайты урона, включая
        // ожоги и болезнь, — ровно те же зоны, по которым художник сыплет
        // кровяные капли, так что грязь и капли ходят парой (баг #10).
        if (landed > 0f && SimBalance.HygieneDamageLoss > 0f &&
            npc.Body.Parts.Count > 0)
        {
            npc.Needs.Hygiene = MathUtil.Clamp01(
                npc.Needs.Hygiene -
                landed / npc.Body.Parts.Count * SimBalance.HygieneDamageLoss);
        }

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

        // §105 r4: грудь и ТАЗ роняют в умирание одинаково — оба смертельны,
        // и разбираются они по общему списку витальных зон, а не поимённо.
        if (npc.Body.VitalDestroyed(out var crushed))
        {
            Trace.Emit(world, npc.Id, "VitalPartDestroyed", $"{crushed} destroyed by {source}");
            EnterDying(world, npc, DyingCause.VitalCrushed);
        }
    }
}

}
