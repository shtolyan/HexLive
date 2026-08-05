using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// Spec §113 — ОДИН решатель «куда именно на этом гексе лечь и как повернуться».
//
// До §113 центр гекса был для лежащего тела безусловной истиной: §60.2a клал её
// в геометрический центр, §29G r3 расширил центр до ШЕРЕНГИ из трёх мест
// (0 / +1 / −1 поперёк общего курса) — но обе редакции знали только про ДРУГИЕ
// ТЕЛА. Мир при этом стоит на тех же гексах: в центре гекса горит костёр, лежит
// валун, стоит кровать. Обморок у костра клал девушку РОВНО В КОСТЁР, потому
// что «центр свободен» означало «на нём никто не лежит».
//
// ⭐ Правило §113: гекс с крупной вещью не запрещён для лежания — запрещена
// САМА ВЕЩЬ. Место ищется тем же единственным алгоритмом шеренги, только
// занятым считается ещё и то место, куда вещь физически не пускает. Костёр
// занимает центральное место — значит ложатся сбоку, слева или справа, ровно
// как ложится вторая девушка рядом с первой. Если и боковые места заняты
// вещью, тело ПОВОРАЧИВАЕТСЯ: шеренга перебирается по всем шести осям гекса,
// потому что валун сбоку мешает одному курсу и не мешает перпендикулярному.
//
// ⭐ Физический радиус вещи — НЕ её ObstacleRadius. Тот радиус про ПРОХОДИМОСТЬ
// (у костра это 0.55R угольного кольца, сквозь которое не ходят, а сам огонь
// втрое меньше), и мерить им «во что нельзя лечь» значило бы прогнать тело с
// гекса костра целиком — ровно того, чего просили не делать. Поэтому у
// определения есть свой SolidRadius (см. ObjectDefinition), а ObstacleRadius
// остаётся фолбэком для вещей, у которых он и есть честный габарит (кровать).
//
// Геометрия намеренно прямоугольная, а не «точка против диска»: лежащее тело
// длинное (≈1.32 wu) и узкое (≈0.36 wu), и весь смысл поворота в том, что
// поперёк вещь пропускает, а вдоль — нет. Диск этой разницы не видит.
internal static class LyingSpot
{
    // Что решил §113: куда лечь, каким курсом, каким местом шеренги — и удалось
    // ли найти ЧИСТОЕ место (false = гекс забит, легли как до §113).
    internal readonly struct Placement
    {
        internal Placement(Float2 position, float heading, int slot, bool clear)
        {
            Position = position;
            Heading = heading;
            Slot = slot;
            Clear = clear;
        }

        internal Float2 Position { get; }

        internal float Heading { get; }

        internal int Slot { get; }

        internal bool Clear { get; }
    }

    // Шаг между соседними местами шеренги (§29G r3).
    internal static float BerthSpacing =>
        HexSpatialMath.HexRadius * Spec49.SleepBerthSpacingFactor;

    // Половина габарита лежащего тела в мировых единицах.
    internal static float BodyHalfLength =>
        HexSpatialMath.HexRadius * Spec49.LieBodyLengthFactor * 0.5f;

    internal static float BodyHalfWidth =>
        HexSpatialMath.HexRadius * Spec49.LieBodyWidthFactor * 0.5f;

    // §111.9 (bug #19 r3): every interaction with a living lying body uses one
    // authored pose. LieDown/Sleep are backward falls: the actor ROOT keeps
    // its standing forward, so while she lies that root axis runs HEAD->FEET.
    // The helper must therefore stand at +forward and face -forward. Calling
    // RotationDegrees itself "feet->head" was the old sign bug: it put every
    // helper at the head, facing the feet, and was especially obvious with the
    // curled second sleep pose.
    internal static Float2 InteractionFeet(NPCState target) =>
        target.Position + Forward(target.RotationDegrees) * BodyHalfLength;

    internal static float InteractionHeading(NPCState target) =>
        Wrap360(target.RotationDegrees + 180f);

    // The route ends on a FREE junction beside the occupied body footprint;
    // the authored interaction station itself deliberately sits inside that
    // footprint, at its feet. The final move is therefore a short scene snap,
    // not another pathfinding step and not a melee/topology test against the
    // ward's occupied junction. Aid is the approach radius; BodyHalfLength is
    // the furthest the station can lie from the body's centre.
    internal static float InteractionStationReach =>
        InteractionReach.Aid + BodyHalfLength;

    internal static void AlignInteractorAtFeet(NPCState actor, NPCState target)
    {
        var heading = InteractionHeading(target);
        actor.Position = InteractionFeet(target);
        actor.RotationDegrees = heading;
        actor.Movement.DesiredRotationDegrees = heading;
        actor.Movement.DesiredDirection = Forward(heading);
    }

    // §66.5 + §111.9 r3: a bed attach point owns the VISIBLE body's centre and
    // yaw. Keep the simulation body on that same pose, otherwise aid/loot uses
    // the approach junction and stale walk yaw while the renderer silently
    // pins the sleeper somewhere else and helpers aim at empty space.
    internal static void AlignBodyToObject(
        WorldState world, NPCState body, WorldObjectState worldObject, Float2 anchorPosition)
    {
        if (body.Tile != worldObject.Tile)
        {
            var previousTile = body.Tile;
            body.Tile = worldObject.Tile;
            SpatialMutations.MoveEntityToTile(world, body.Id, previousTile, body.Tile);
        }

        body.Position = anchorPosition;
        body.RotationDegrees = Wrap360(worldObject.RotationDegrees);
        body.Movement.DesiredRotationDegrees = body.RotationDegrees;
        body.Movement.DesiredDirection = Forward(body.RotationDegrees);
    }

    /// <summary>
    /// Место и курс для тела, которое ложится на своём гексе. Порядок перебора
    /// фиксирован и не зависит от порядка обхода словарей — трасса на том же
    /// сиде обязана воспроизводиться байт в байт.
    /// </summary>
    internal static Placement Solve(WorldState world, NPCState npc)
    {
        var center = HexSpatialMath.TileToWorld(npc.Tile);
        var lead = FindLead(world, npc);
        var baseHeading = lead is not null
            ? lead.RotationDegrees
            : SnapToHexAxis(npc.RotationDegrees);

        // 1) Курс шеренги (или свой, приснапленный к оси гекса) — пока на нём
        //    есть чистое место, тело не разворачивается: соседки должны лежать
        //    ПАРАЛЛЕЛЬНО (§29G r3), и поворот ради поворота эту картинку рушит.
        if (TryHeading(world, npc, center, baseHeading, out var placement))
        {
            return placement;
        }

        // 2) Курс мешает — крутим тело по осям гекса: ±60°, ±120°, 180°.
        //    Валун сбоку закрывает одну ось и оставляет открытой соседнюю.
        if (Spec49.LieAroundObstacles)
        {
            for (var step = 1; step <= 3; step++)
            {
                for (var sign = 1; sign >= -1; sign -= 2)
                {
                    if (step == 3 && sign < 0)
                    {
                        continue; // +180 и −180 — один и тот же курс
                    }

                    var heading = Wrap360(baseHeading + sign * step * 60f);
                    if (TryHeading(world, npc, center, heading, out placement))
                    {
                        return placement;
                    }
                }
            }
        }

        // 3) Гекс забит целиком (тесный лагерь, четвёртое тело) — ложимся ровно
        //    так, как до §113: лучше лечь неудачно, чем не лечь вовсе.
        var fallbackSlot = FirstFreeSlot(TakenMask(world, npc, center, Lateral(baseHeading)));
        return new Placement(
            center + Lateral(baseHeading) * (fallbackSlot * BerthSpacing),
            baseHeading, fallbackSlot, clear: false);
    }

    /// <summary>
    /// Физический радиус вещи — то, во что телу нельзя лечь. 0 = вещь не
    /// мешает (лежащий на земле инструмент, кокос, куча волокна).
    /// </summary>
    internal static float SolidRadius(WorldState world, WorldObjectState worldObject)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
        {
            return 0f;
        }

        // Строящийся сайт меряется по ТОМУ, ЧЕМ СТАНЕТ — ровно как в
        // SetObstacleBlocking: на площадке уже лежат брёвна и палки будущей
        // кровати, и лечь поперёк них так же нельзя, как поперёк кровати.
        if (!string.IsNullOrEmpty(worldObject.BuildProduct) &&
            world.Content.ObjectDefinitions.TryGetValue(worldObject.BuildProduct, out var product))
        {
            definition = product;
        }

        if (definition.SolidRadius > 0f)
        {
            return definition.SolidRadius;
        }

        if (!definition.Tags.Contains("Obstacle"))
        {
            return 0f;
        }

        // Вещь объявлена твёрдой, но габарита не назвала (валун и пальма
        // закрывают только свой узел). Пол не даёт телу лечь В неё.
        var floor = HexSpatialMath.HexRadius * Spec49.LieSolidRadiusFloorFactor;
        return definition.ObstacleRadius > floor ? definition.ObstacleRadius : floor;
    }

    // Мировая точка вещи — её якорный узел (по нему её и рисуют).
    internal static bool TryAnchor(WorldState world, WorldObjectState worldObject, out Float2 position)
    {
        if (worldObject.Junctions.Count > 0 &&
            world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchor))
        {
            position = anchor.WorldPosition;
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>
    /// Влезает ли тело центром в <paramref name="spot"/> при курсе
    /// <paramref name="forward"/>, ничего не задевая.
    /// </summary>
    internal static bool BodyClear(
        WorldState world, NPCState npc, TileCoord tile, Float2 spot, Float2 forward, Float2 lateral)
    {
        // Свой гекс и шесть соседних: крупная вещь (кровать 1.39 wu) стоит на
        // соседнем гексе, а свешивается на этот.
        for (var i = -1; i < HexDirection.All.Length; i++)
        {
            var coord = i < 0
                ? tile
                : new TileCoord(tile.Q + HexDirection.All[i].DQ, tile.R + HexDirection.All[i].DR);
            if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var objects))
            {
                continue;
            }

            foreach (var objectId in objects)
            {
                if (!world.Entities.Objects.TryGetValue(objectId, out var worldObject))
                {
                    continue;
                }

                var radius = SolidRadius(world, worldObject);
                if (radius <= 0f || !TryAnchor(world, worldObject, out var anchor))
                {
                    continue;
                }

                if (Overlaps(anchor, radius, spot, forward, lateral))
                {
                    return false;
                }
            }
        }

        // Уже лежащие соседки. Шеренга разводит их по местам, но при ПОВОРОТЕ
        // (шаг 2 в Solve) места считаются по другой оси, и без этой проверки
        // повёрнутое тело легло бы поперёк лежащей.
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (!IsBerthNeighbour(world, npc, other))
            {
                continue;
            }

            if (Overlaps(other.Position, BodyHalfWidth, spot, forward, lateral))
            {
                return false;
            }
        }

        return true;
    }

    // Тело — прямоугольник (длина вдоль курса, ширина поперёк), вещь — диск.
    // Диск огрубляется до квадрата в системе координат тела: пара миллиметров
    // запаса по углам дешевле, чем точная задача о ближайшей точке.
    private static bool Overlaps(
        Float2 point, float radius, Float2 spot, Float2 forward, Float2 lateral)
    {
        var d = point - spot;
        var along = System.MathF.Abs(d.X * forward.X + d.Y * forward.Y);
        var side = System.MathF.Abs(d.X * lateral.X + d.Y * lateral.Y);
        return along <= BodyHalfLength + radius && side <= BodyHalfWidth + radius;
    }

    private static bool TryHeading(
        WorldState world, NPCState npc, Float2 center, float heading, out Placement placement)
    {
        var lateral = Lateral(heading);
        var forward = Forward(heading);
        var taken = TakenMask(world, npc, center, lateral);
        var half = Spec49.SleepBerthHalfSpan;

        for (var step = 0; step <= half; step++)
        {
            for (var sign = 1; sign >= -1; sign -= 2)
            {
                if (step == 0 && sign < 0)
                {
                    continue; // центральное место — одно
                }

                var slot = step * sign;
                if ((taken & (1 << (slot + half))) != 0)
                {
                    continue;
                }

                var spot = center + lateral * (slot * BerthSpacing);
                if (Spec49.LieAroundObstacles &&
                    !BodyClear(world, npc, npc.Tile, spot, forward, lateral))
                {
                    continue;
                }

                placement = new Placement(spot, heading, slot, clear: true);
                return true;
            }
        }

        placement = default;
        return false;
    }

    // Занятые места читаются из ФАКТИЧЕСКИХ позиций лежащих (проекция на общую
    // поперечную ось), поэтому индекс места нигде не хранится и не сериализуется.
    private static int TakenMask(WorldState world, NPCState npc, Float2 center, Float2 lateral)
    {
        var half = Spec49.SleepBerthHalfSpan;
        var taken = 0;
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (!IsBerthNeighbour(world, npc, other))
            {
                continue;
            }

            var offset = other.Position - center;
            var slot = (int)System.MathF.Round(
                (offset.X * lateral.X + offset.Y * lateral.Y) / BerthSpacing);
            if (slot >= -half && slot <= half)
            {
                taken |= 1 << (slot + half);
            }
        }

        return taken;
    }

    // Порядок мест: центр, +1, −1, … ±HalfSpan. Полная шеренга — снова центр
    // (стопкой, как до §29G r3), а не место, свешенное за кромку гекса.
    private static int FirstFreeSlot(int taken)
    {
        var half = Spec49.SleepBerthHalfSpan;
        for (var step = 0; step <= half; step++)
        {
            if ((taken & (1 << (step + half))) == 0)
            {
                return step;
            }

            if (step > 0 && (taken & (1 << (half - step))) == 0)
            {
                return -step;
            }
        }

        return 0;
    }

    // Курс шеренги принадлежит первой легшей (наименьший EntityId, чтобы ответ
    // не зависел от порядка обхода и переигрывался из сейва один в один).
    private static NPCState FindLead(WorldState world, NPCState npc)
    {
        NPCState lead = null;
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (!IsBerthNeighbour(world, npc, other))
            {
                continue;
            }

            if (lead is null || other.Id.Value < lead.Id.Value)
            {
                lead = other;
            }
        }

        return lead;
    }

    // Тело, уже лежащее на этом гексе: спящая, вырубившаяся, в коме, рыдающая
    // (§110), притворившаяся мёртвой (§105.14) или безногая. Трупы — объекты,
    // не NPC, и живут по своему якорю (§60.2a).
    internal static bool IsBerthNeighbour(WorldState world, NPCState npc, NPCState other) =>
        !other.Id.Equals(npc.Id) &&
        other.Health > 0f &&
        other.Tile.Equals(npc.Tile) &&
        other.IsLyingDown(world.Tick);

    private static Float2 Forward(float headingDegrees)
    {
        var radians = headingDegrees * (System.MathF.PI / 180f);
        return new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
    }

    private static Float2 Lateral(float headingDegrees)
    {
        var forward = Forward(headingDegrees);
        return new Float2(-forward.Y, forward.X); // курс + 90°
    }

    // Ближайшее кратное 60° — одна из шести осей гекса. Тело, лежащее вдоль
    // оси, вписано в гекс; лежащее поперёк угла — нет.
    internal static float SnapToHexAxis(float degrees) =>
        System.MathF.Round(Wrap360(degrees) / 60f) % 6f * 60f;

    private static float Wrap360(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }
}

}
