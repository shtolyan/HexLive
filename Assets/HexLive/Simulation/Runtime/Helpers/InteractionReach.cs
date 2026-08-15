using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

/// <summary>Единственные два ответа на «достаю ли я до неё» (§102 r4).</summary>
internal enum MeleeApproach
{
    /// <summary>Не достаю — иду.</summary>
    Approach,

    /// <summary>Достаю — начинаю или продолжаю.</summary>
    Act,
}

// Spec §26.3 r3: the ONE table of "how close is close enough" for starting an
// interaction. The recurring "acts from a whole hex away" family (build /
// craft / harvest / feed / treat at range) always traces back to some site
// rolling its own tolerance constant; every start gate must read this class,
// and every start must be measured through CheckStart so a soak run can count
// violations ("InteractionTooFar" events) instead of waiting for a screenshot.
//
// ⭐ ПЯТЬ МЕР БЛИЗОСТИ, и путать их дорого. Раньше этот комментарий обещал «одну
// таблицу», а соседство узлов жило в боевом хелпере и в таблицу не входило —
// ровно из этого зазора и вырос §102. Теперь таблица честная:
//
//   мера                      | что значит                        | где
//   --------------------------|-----------------------------------|-------------
//   CanStrike                 | СОСЕДСТВО УЗЛОВ: свой узел или     | здесь
//                             | смежный. Рука дальше не достаёт.   |
//                             | Плюс СРЕДА (§106): по воде и из    |
//                             | воды человек не бьёт — это не      |
//                             | шестая мерка дистанции, а второй   |
//                             | замер того же гейта, как           |
//                             | CanTouchAcross у CheckObjectStart. |
//   Talk / Aid / ForObject    | МЕТРИЧЕСКАЯ дистанция в мировых    | здесь
//                             | единицах, через CheckStart.        |
//   CheckObjectStart          | метрика И проходимость границы     | здесь
//                             | (через обрыв не дотянешься).       |
//   HexDistance               | ЦЕЛЫЕ тайлы. Прикидка «далеко ли», | HexSpatialMath
//                             | не гейт старта.                    |
//   Connectivity.Reachable    | СУЩЕСТВУЕТ ЛИ МАРШРУТ вообще.      | Connectivity
//                             | Не про близость: сюда не сводится. |
//
// Первые три — «достаточно ли близко, чтобы действовать», и жить им положено
// здесь. Последние две отвечают на ДРУГИЕ вопросы и намеренно оставлены
// снаружи: свести их сюда значило бы сделать вид, что «в двух тайлах» и «туда
// есть дорога» — про одно и то же.
internal static class InteractionReach
{
    // §26.6A r5 в ОДНОМ месте: рубильник читается здесь и только здесь, иначе
    // планировщик и гейт старта смогут прочесть его по-разному, а расхождение
    // между «куда её послали» и «откуда ей разрешено работать» — это шун-шторм
    // (цель отвергается на месте, помечается shun, цикл), а не тихая мелочь.
    // Выключенный рубильник = в точности r4: стена только терраин.
    // ⭐ §26.6A r5, вторая половина правила: со скольких СВОБОДНЫХ клеток можно
    // законно поработать с предметом, лежащим на этом узле. Ноль — предмет,
    // который никто никогда не поднимет.
    //
    // Живёт здесь, а не у каждого места выкладки, ровно по той же причине, по
    // которой здесь живут дистанции: пока «куда положить» и «откуда достать»
    // считает ОДИН предикат, разойтись им негде. Пока это был first-fit «первый
    // проходимый узел», мир исправно ронял кокосы вплотную к стволу — а с r5
    // это уже не «неудобно», а еда, которая сгниёт нетронутой (замер: 12 сидов ×
    // 10 дней, упало столько же, подобрано 1772 → 1595, сгнило 625 → 651).
    private static readonly System.Collections.Generic.List<JunctionId> _approachScratch = new();

    public static int CountApproaches(WorldState world, JunctionId at)
    {
        // Предмет на земле футпринта не несёт, поэтому owner здесь null не для
        // краткости: это в точности тот ответ, который потом даст CheckObjectStart.
        SpatialQueries.CollectStandableAround(
            world, at, _approachScratch, 96, SpatialQueries.BesideReach(0f), null, RimMode);

        var free = 0;
        foreach (var rim in _approachScratch)
        {
            if (SpatialQueries.IsJunctionFree(world, rim))
            {
                free++;
            }
        }

        return free;
    }

    public static SpatialQueries.RimPurpose RimMode =>
        SimBalance.ReachThroughBodiesBlocked
            ? SpatialQueries.RimPurpose.Reach
            : SpatialQueries.RimPurpose.Route;

    // Object work (harvest/craft/build/pickup/sit...): the object's physical
    // footprint plus one sub-grid step — see SpatialQueries.BesideReach.
    public static float ForObject(float obstacleRadius) =>
        SpatialQueries.BesideReach(obstacleRadius);

    // Person-to-person care (feed/hydrate/treat/medicate/console): the plan
    // walks to arm's length (0.9*R beside her); junction snap can push the
    // legit spot out to ~1.3*R. Anything past that reads as feeding from
    // across the camp.
    public static float Aid => HexSpatialMath.HexRadius * 1.3f;

    // Talking carries a little farther than touch — but 4*R (a hex and a
    // half) read as chatting across the camp. The planner reserves the same
    // arm's-length approach as aid; 2*R is drift slack, not a target.
    public static float Talk => HexSpatialMath.HexRadius * 2f;

    // Рука достаёт до СВОЕГО узла и до СМЕЖНОГО — и не дальше. Мера
    // топологическая, а не метрическая: через обрыв между двумя близкими по
    // прямой узлами кулаком не дотянешься, а вдоль пологой границы — да.
    //
    // Жила в MeleeSwing и потому не считалась «дистанцией взаимодействия».
    // Именно это и стоило §102: преследование мерило метрикой, старт — вот
    // этим, и кольцо между мерками молчало обеими.
    public static bool CanStrike(WorldState world, NPCState actor, NPCState target)
    {
        // §106: вода — убежище. Пловец не бьёт, и по пловцу с суши не бьют;
        // терренный гейт стоит ПЕРЕД меркой соседства, чтобы каждый вызывающий
        // (HumanCombat, Raid, Abuse, AssessMelee) получил его одинаково. Уже
        // НАЧАТЫЙ замах при этом долетает по таймеру и уходит в воздух — это
        // «она отступила», ядро MeleeSwing (§104) среду не знает.
        if (!CombatMedium.NpcMelee(world, actor, target))
        {
            return false;
        }

        return actor.CurrentJunction is { } aj && target.CurrentJunction is { } bj &&
            (aj.Equals(bj) ||
             (world.Junctions.Items.TryGetValue(bj, out var junction) &&
              junction.Neighbors.Contains(aj)));
    }

    // ⭐ Правило §102 r4 одной функцией: НЕ ДОСТАЮ → ИДУ; ДОСТАЮ → НАЧИНАЮ.
    //
    // Смысл в том, что и «пора догонять», и «можно начинать» отвечает ОДНО
    // место. Пока это были два условия в разных строках, между ними existовал
    // зазор, в котором молчали оба: замер на арене — 2951 из 12000 тиков в этом
    // кольце, самый длинный застой 2872 тика подряд. Теперь «идти» — это
    // буквально «не действовать», и третьего состояния взяться неоткуда.
    //
    // Гистерезис у меры честный и намеренный, как у порогов нужд: ВОЙТИ в сцену
    // можно только с ударной дистанции (§98 — иначе сцены начинались там, откуда
    // рука не достаёт), а УДЕРЖИВАТЬ её позволено на разговорной (§89 — за узкую
    // мерку внутри сцены платили десятками срывов за прогон, стоило жертве
    // переступить). Разные пороги на вход и на выход — не расхождение мер, если
    // они названы и живут рядом.
    public static MeleeApproach AssessMelee(
        WorldState world, NPCState actor, NPCState target, bool sceneStarted, string what)
    {
        var close = sceneStarted
            ? CheckStart(world, actor, target.Position, Talk, what)
            : CanStrike(world, actor, target);

        return close ? MeleeApproach.Act : MeleeApproach.Approach;
    }

    // True when the NPC stands close enough to the anchor to begin; otherwise
    // emits the standardized InteractionTooFar trace (the caller aborts with
    // its own cleanup — shun/claim-release/cooldown differ per kind).
    public static bool CheckStart(
        WorldState world, NPCState npc, Float2 anchor, float reach, string what)
    {
        var distance = HexSpatialMath.Distance(npc.Position, anchor);
        if (distance <= reach)
        {
            return true;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InteractionTooFar",
                $"{what} at {distance:F2}wu > reach {reach:F2}wu");
        }
        return false;
    }

    // §120 / bug #102: person interactions need the same terrain-side proof as
    // object work. Metric proximity alone lets an outside approach sit one
    // sub-grid step from a patient inside the hut and feed/search through the
    // wall. The target junction is the contact anchor; CollectStandableAround
    // crosses neither wall nor cliff, so only a rim point on the target's side
    // is legal. A route may still enter normally through a Door junction.
    public static bool CanTouchPersonAcross(
        WorldState world, JunctionId stand, JunctionId target, float reach) =>
        SpatialQueries.CanTouchAcross(world, stand, target, reach, null, RimMode);

    /// <summary>
    /// §53.4 r6: a patient in a bed is touched across HER support furniture,
    /// not across the stale wake junction kept in CurrentJunction. The bed's
    /// own footprint is transparent to the hands; walls, cliffs and third
    /// objects remain barriers.
    /// </summary>
    internal static bool CanTouchBedOccupantAcross(
        WorldState world, JunctionId stand, WorldObjectState bed)
    {
        if (bed?.Junctions.Count <= 0)
        {
            return false;
        }

        return SpatialQueries.CanTouchAcross(
            world, stand, bed.Junctions[0],
            SpatialQueries.BesideReach(LyingSpot.SolidRadius(world, bed)),
            bed, RimMode);
    }

    internal static bool CheckBedOccupantStart(
        WorldState world, NPCState npc, NPCState target,
        WorldObjectState bed, string what)
    {
        if (!CheckStart(world, npc, target.Position, Aid, what))
        {
            return false;
        }

        if (npc.CurrentJunction is not { } stand)
        {
            return true; // off-grid interpolation: metric is the only honest proof
        }

        if (CanTouchBedOccupantAcross(world, stand, bed))
        {
            return true;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InteractionTooFar",
                $"{what} cannot reach the occupied bed from j{stand.Value} " +
                "without crossing a wall/cliff/third object");
        }
        return false;
    }

    public static bool CheckPersonStart(
        WorldState world, NPCState npc, NPCState target, Float2 anchor,
        float reach, string what)
    {
        if (!CheckStart(world, npc, anchor, reach, what))
        {
            return false;
        }

        if (npc.CurrentJunction is not { } stand ||
            target.CurrentJunction is not { } targetJunction)
        {
            return true; // off-grid interpolation: metric is the only honest proof
        }

        if (CanTouchPersonAcross(world, stand, targetJunction, reach))
        {
            return true;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InteractionTooFar",
                $"{what} is across an impassable border (cliff/wall/obstacle) " +
                $"from j{stand.Value} to j{targetJunction.Value}");
        }
        return false;
    }

    // Spec §26.6A r4: the whole gate for WORLD-OBJECT work (harvest, chop,
    // craft, build, pick-up, sit, sleep, fuel, draw...). Distance alone was
    // never enough: a hex border that stops the feet — a cliff face, a hut
    // wall — sits well inside BesideReach, so an NPC one sub-grid step BELOW a
    // ledge could pierce the coconut / fell the palm / sit on the stump THROUGH
    // it. Reach is therefore measured twice: straight-line, and along the
    // ground (CanTouchAcross). Both failures emit the same InteractionTooFar
    // trace, so the §26.6A soak counter covers this family too.
    public static bool CheckObjectStart(
        WorldState world, NPCState npc, WorldObjectState worldObject, float obstacleRadius)
    {
        var what = worldObject.DefinitionId;
        var reach = ForObject(obstacleRadius);

        // The anchor must NEVER be unavailable — an object with no linked
        // junction (edge case) falls back to its tile centre with a hex of
        // slack, so no interaction kind can slip past the gate entirely. That
        // fallback has no junction to walk from, so it stays distance-only.
        if (worldObject.Junctions.Count == 0 ||
            !world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchorJunction))
        {
            return CheckStart(world, npc, HexSpatialMath.TileToWorld(worldObject.Tile),
                reach + HexSpatialMath.HexRadius, what);
        }

        if (!CheckStart(world, npc, anchorJunction.WorldPosition, reach, what))
        {
            return false;
        }

        if (npc.CurrentJunction is not { } standJunction)
        {
            return true; // off-grid (mid-hop): distance is all we can honestly measure
        }

        // r5: the object being worked is passed in, so its OWN footprint stays
        // crossable (fireside rim, bed frame) while a THIRD body in the way — a
        // palm trunk between her knife and the coconut — is a wall like a cliff.
        if (SpatialQueries.CanTouchAcross(world, standJunction, anchorJunction.Id, reach, worldObject, RimMode))
        {
            return true;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "InteractionTooFar",
                $"{what} at {HexSpatialMath.Distance(npc.Position, anchorJunction.WorldPosition):F2}wu " +
                $"is across an impassable border (cliff/wall/obstacle) from j{standJunction.Value}");
        }
        return false;
    }
}

}
