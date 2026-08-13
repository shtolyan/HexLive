using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// Spec §111.13 — КОЛЬЦО СТАНЦИЙ вокруг лежащего тела.
//
// §111.9 отвечал на «где стоит тот, кто с ней работает» ОДНОЙ точкой у ног, и
// это было верно ровно до тех пор, пока подходил один человек. Заявки на
// лежащую независимы по построению: помощь §53 держит PendingAidFrom, обыск
// §111 — PendingLootedBy, друг друга они не проверяют (и не должны — §111.8
// делает из встречи лекаря с лутером драку). Плюс протезы §118, разговор со
// спящей и плачущей §110, ручные команды игрока. Все они звали один и тот же
// снап без единой проверки занятости, и двое разных людей вставали в
// байт-в-байт одну позицию с одним курсом на всю сцену.
//
// Здесь живёт только ГЕОМЕТРИЯ станций и проверка их пригодности. Кто какую
// держит — вопрос заявок, он решается слоем выше.
internal static class LyingStations
{
    /// <summary>Приоритетная станция: та самая точка у ног из §111.9 r3.</summary>
    internal const int FeetSlot = 0;

    internal const int Count = 5;

    // Шаг суб-сетки. Смещения боковых станций выводятся ИЗ него, а не из
    // литералов и не из padding'а ClaimLyingFootprint: там эти числа — побочный
    // продукт раздутия брони «на обход», и перетюнить его никто не мешает.
    private static float SubStep =>
        HexSpatialMath.HexRadius / HexPointLayout.BoundaryRadius;

    private static float SideAlong => SubStep * HexSpatialMath.Sqrt3 * 0.5f;

    private static float SideLateral => SubStep * 0.5f;

    private readonly struct Station
    {
        internal Station(float alongFactor, float lateralFactor)
        {
            AlongFactor = alongFactor;
            LateralFactor = lateralFactor;
        }

        /// <summary>Множитель к SideAlong; у станции ног читается особо.</summary>
        internal float AlongFactor { get; }

        internal float LateralFactor { get; }
    }

    // Порядок = приоритет. Первая свободная по списку и достаётся пришедшему,
    // каким бы делом он ни занимался, — «ноги первому» получается само.
    private static readonly Station[] Slots =
    {
        new Station(0f, 0f),    // 0 — ноги, особый случай: +BodyHalfLength
        new Station(1f, +1f),   // 1 — бедро +
        new Station(1f, -1f),   // 2 — бедро −
        new Station(-1f, +1f),  // 3 — плечо +
        new Station(-1f, -1f),  // 4 — плечо −
    };

    internal static bool IsSlot(int slot) => slot >= 0 && slot < Count;

    /// <summary>Смещение станции В СИСТЕМЕ ТЕЛА: (вдоль forward, вбок).</summary>
    internal static (float Along, float Lateral) LocalOffset(int slot)
    {
        if (!IsSlot(slot))
        {
            return (0f, 0f);
        }

        if (slot == FeetSlot)
        {
            return (LyingSpot.BodyHalfLength, 0f);
        }

        var station = Slots[slot];
        return (station.AlongFactor * SideAlong, station.LateralFactor * SideLateral);
    }

    /// <summary>
    /// Станция — СМЕЩЕНИЕ, а не узел сетки. На земле тело стоит на узле с
    /// гекс-осевым курсом, и все пять точек совпадают с узлами; в кровати тело
    /// не выровнено вовсе, а ползущая §50 ещё и движется. Поэтому геометрия
    /// считается в системе тела и удерживается снапом — ровно как §111.9
    /// удерживал ноги.
    /// </summary>
    internal static Float2 Point(NPCState body, int slot)
    {
        var (along, lateral) = LocalOffset(slot);
        var forward = Forward(body.RotationDegrees);
        var side = Lateral(forward);
        return body.Position + forward * along + side * lateral;
    }

    /// <summary>Голова — противоположный конец тела (§111.9 r3: ноги по +Forward).</summary>
    internal static Float2 HeadPoint(NPCState body) =>
        body.Position - Forward(body.RotationDegrees) * LyingSpot.BodyHalfLength;

    /// <summary>
    /// ⭐ Курс с ЛЮБОЙ станции — на голову. Не таблица, а одна формула: смотреть
    /// лежащей в лицо, а не в бок и не в ноги, — единственный курс, который
    /// читается как участие в человеке, а не в теле. У станции ног формула даёт
    /// в точности Rotation + 180°, потому что ноги, центр и голова коллинеарны,
    /// так что §111.9 r3 — её частный случай, а не исключение.
    /// </summary>
    internal static float Heading(NPCState body, int slot)
    {
        var delta = HeadPoint(body) - Point(body, slot);
        if (delta.X * delta.X + delta.Y * delta.Y < 1e-8f)
        {
            return Wrap360(body.RotationDegrees + 180f);
        }

        return Wrap360(HexSpatialMath.AngleDegrees(HexSpatialMath.Normalize(delta)));
    }

    /// <summary>
    /// Насколько далеко от ТЕЛА позволено стоять, работая с этой станции.
    /// Обобщает §111.9 r2: помощь плюс вылет самой станции. У боковых получается
    /// строго строже, чем у ног, — и это правильно, они ближе.
    /// </summary>
    internal static float Reach(int slot)
    {
        var (along, lateral) = LocalOffset(slot);
        return InteractionReach.Aid +
            System.MathF.Sqrt(along * along + lateral * lateral);
    }

    /// <summary>Поставить актёра на станцию и развернуть на голову.</summary>
    internal static void Align(NPCState actor, NPCState body, int slot)
    {
        var heading = Heading(body, slot);
        actor.Position = Point(body, slot);
        actor.RotationDegrees = heading;
        actor.Movement.DesiredRotationDegrees = heading;
        actor.Movement.DesiredDirection = Forward(heading);
    }

    /// <summary>
    /// Годится ли станция физически: она не должна оказаться внутри ЧУЖОГО
    /// лежащего тела, трупа или твёрдой вещи. Та же мерка, что у укладки
    /// §113.2. У кровати, придвинутой к стене, так остаются две-три станции из
    /// пяти — это честный ответ, а не сбой.
    /// </summary>
    internal static bool IsUsable(WorldState world, NPCState body, int slot)
    {
        if (!IsSlot(slot))
        {
            return false;
        }

        var point = Point(body, slot);

        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Equals(body.Id) || other.Health <= 0f ||
                !other.IsLyingDown(world.Tick))
            {
                continue;
            }

            if (LyingSpot.ContainsBodyPoint(other, point))
            {
                return false;
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            if (corpse.Id.Equals(body.Id) || corpse.IsBeingCarried)
            {
                continue;
            }

            if (LyingSpot.ContainsBodyPoint(corpse, point))
            {
                return false;
            }
        }

        // Вещи ищутся по своему и шести соседним гексам — станция дальше 0.66 wu
        // от центра тела не уезжает, так что этого охвата хватает с запасом.
        for (var i = -1; i < HexDirection.All.Length; i++)
        {
            var coord = i < 0
                ? body.Tile
                : new TileCoord(
                    body.Tile.Q + HexDirection.All[i].DQ,
                    body.Tile.R + HexDirection.All[i].DR);
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

                var radius = LyingSpot.SolidRadius(world, worldObject);
                if (radius <= 0f || !LyingSpot.TryAnchor(world, worldObject, out var anchor))
                {
                    continue;
                }

                var delta = anchor - point;
                if (delta.X * delta.X + delta.Y * delta.Y < radius * radius)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Какую станцию этого тела актёр держит прямо сейчас; null — никакую.
    /// </summary>
    internal static int? Held(WorldState world, NPCState actor, NPCState body)
    {
        if (actor.Execution.LyingStationTargetId is not { } targetId ||
            !targetId.Equals(body.Id) ||
            !IsSlot(actor.Execution.LyingStationSlot) ||
            !IsClaimAlive(world, actor, body))
        {
            return null;
        }

        return actor.Execution.LyingStationSlot;
    }

    /// <summary>
    /// ⭐ Занятость ВЫВОДИТСЯ, а не хранится на пациентке. Пятое поле-заявка
    /// («кто держит слот k») было бы повторением ошибки, про которую в §111 уже
    /// написано словами: забытый клейм делает тело занятым навсегда. Обход
    /// актёров с предикатом живости самолечится — ровно как самолечение
    /// просроченного PendingAidFrom в решении.
    ///
    /// Мерка — членство во множестве, а не первый встречный в обходе, поэтому
    /// порядок словаря (он ломается после первой смерти) на ответ не влияет.
    /// </summary>
    internal static bool IsFree(WorldState world, NPCState body, int slot, NPCState forActor)
    {
        if (!IsSlot(slot))
        {
            return false;
        }

        foreach (var other in world.Entities.Npcs.Values)
        {
            if (forActor is not null && other.Id.Equals(forActor.Id))
            {
                continue;
            }

            if (Held(world, other, body) == slot)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Занять свободную станцию. Идемпотентно: уже занятая этим же актёром
    /// возвращается как есть — сменившийся вид помощи (§53.7) не гоняет его
    /// вокруг тела. Порядок перебора фиксирован, поэтому «ноги достаются первому
    /// пришедшему» получается само, без единой строки про виды действий.
    /// Свободных нет — ОТКАЗ: ни разделения точки у ног, ни ожидания на месте
    /// (ожидание рядом с занятым телом — это в точности сигнатура застоя §102).
    /// </summary>
    internal static bool TryClaim(WorldState world, NPCState actor, NPCState body, out int slot)
    {
        if (Held(world, actor, body) is { } already)
        {
            slot = already;
            return true;
        }

        for (var candidate = 0; candidate < Count; candidate++)
        {
            if (!IsFree(world, body, candidate, actor) ||
                !IsUsable(world, body, candidate))
            {
                continue;
            }

            actor.Execution.LyingStationTargetId = body.Id;
            actor.Execution.LyingStationSlot = candidate;
            slot = candidate;
            return true;
        }

        slot = -1;
        return false;
    }

    /// <summary>
    /// Станция, с которой актёр работает по этому телу СЕЙЧАС. Не держит ничего
    /// (сцена началась мимо планировщика, старый сейв) — значит ноги: это и
    /// приоритетная станция, и ровно прежнее поведение §111.9.
    /// </summary>
    internal static int SlotFor(WorldState world, NPCState actor, NPCState body) =>
        Held(world, actor, body) ?? FeetSlot;

    internal static void ReleaseStation(NPCState actor)
    {
        actor.Execution.LyingStationTargetId = null;
        actor.Execution.LyingStationSlot = -1;
    }

    // Страховка, а не основной механизм: штатно станция освобождается на выходе
    // из сцены. Но выходов много (аборт плана, пробуждение, смерть, подъём на
    // руки), и один забытый навсегда отнимал бы место у тела.
    private static bool IsClaimAlive(WorldState world, NPCState actor, NPCState body)
    {
        if (actor.Health <= 0f || actor.IsBeingCarried ||
            !body.IsLyingDown(world.Tick))
        {
            return false;
        }

        var planned = actor.Plan.TargetAgentId is { } planTarget &&
            planTarget.Equals(body.Id);
        return planned || actor.Execution.Status == ExecutionStatus.InProgress;
    }

    private static Float2 Forward(float headingDegrees)
    {
        var radians = headingDegrees * (System.MathF.PI / 180f);
        return new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
    }

    private static Float2 Lateral(Float2 forward) => new Float2(-forward.Y, forward.X);

    private static float Wrap360(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }
}

}
