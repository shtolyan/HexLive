using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;

namespace HexLive.Simulation.Runtime
{

// Spec §135: оторванная конечность — добыча. Зверь берёт её в зубы, бросает
// бой, уходит за пределы своей зоны агра, доедает и потом полдня сыт.
//
// Состояние живёт на MobState (несёт → отходит → ест → сыт), а не на объекте
// мира: пока конечность в зубах, объекта `body.limb_severed` в мире НЕТ — его
// забрали с земли тем же тиком, что и уронили. Так конечность не может быть
// одновременно и в пасти, и на земле под ногами у колонистки, и вид не обязан
// уметь двигать объекты мира (он их только создаёт на месте, §50.5).
public static class MobLimbPrize
{
    // §50.4 роняет ровно этот объект; мы его отсюда и забираем.
    private const string SeveredLimbObjectId = "body.limb_severed";

    /// <summary>Это лежащая оторванная конечность (§50.4)?</summary>
    public static bool IsSeveredLimb(WorldObjectState worldObject) =>
        worldObject is not null && worldObject.DefinitionId == SeveredLimbObjectId;

    public static bool IsSated(WorldState world, MobState mob) =>
        Spec135.Enabled && world.Tick < mob.SatedUntilTick;

    /// <summary>
    /// Конечность появилась в мире — занести в индекс §135. Звать ОБЯЗАТЕЛЬНО
    /// из каждого места, где она рождается: зверь ищет падаль только по этому
    /// списку, и незанесённая нога для него не существует.
    /// </summary>
    public static void Register(WorldState world, ObjectId limb)
    {
        var index = world.Caches.SeveredLimbs;
        if (!index.Contains(limb))
        {
            index.Add(limb);
        }
    }

    /// <summary>
    /// Один проход по объектам на загруженный мир: сейв несёт конечности как
    /// обычные объекты, а индекс — производная, и её надо восстановить.
    /// </summary>
    private static void EnsureIndex(WorldState world)
    {
        if (world.Caches.SeveredLimbsIndexed)
        {
            return;
        }

        world.Caches.SeveredLimbsIndexed = true;
        world.Caches.SeveredLimbs.Clear();
        foreach (var worldObject in world.Entities.Objects.Values)
        {
            if (IsSeveredLimb(worldObject))
            {
                world.Caches.SeveredLimbs.Add(worldObject.Id);
            }
        }
    }

    /// <summary>
    /// §135: ближайшая лежащая конечность в радиусе чутья зверя — или ничего.
    /// <para>
    /// ⭐ Цена в обычной игре — одно сравнение с нулём: конечностей на острове
    /// нет почти всегда. Перебор запускается только когда они есть, и тогда их
    /// единицы. Заодно чистит из индекса всё, чего в мире уже нет.
    /// </para>
    /// </summary>
    public static bool TryFindNearby(
        WorldState world, MobState mob, out WorldObjectState prize)
    {
        prize = null;
        if (!Spec135.Enabled)
        {
            return false;
        }

        EnsureIndex(world);
        var index = world.Caches.SeveredLimbs;
        if (index.Count == 0)
        {
            return false;
        }

        var senseRadius = MobCatalog.For(mob.MobId).AggroRadiusTiles + Spec135.ScentRadiusBonusTiles;
        var best = int.MaxValue;
        for (var i = index.Count - 1; i >= 0; i--)
        {
            if (!world.Entities.Objects.TryGetValue(index[i], out var limb) ||
                !IsSeveredLimb(limb))
            {
                index.RemoveAt(i); // сгнила, съедена или уже в чьих-то зубах
                continue;
            }

            var distance = HexSpatialMath.HexDistance(mob.Tile, limb.Tile);
            if (distance <= senseRadius && distance < best)
            {
                best = distance;
                prize = limb;
            }
        }

        return prize is not null;
    }

    /// <summary>
    /// Зверь дошёл до лежащей конечности и взял её в зубы. Дальше — та же
    /// программа, что и после отрыва в бою: отход, еда, сытость.
    /// </summary>
    public static void TakeFromGround(WorldState world, MobState mob, WorldObjectState limb)
    {
        if (mob.IsCarryingLimb || !WorldObjectMutations.DespawnObject(world, limb.Id))
        {
            return;
        }

        mob.CarriedLimbOwner = limb.CurrentUser;
        mob.CarriedLimbPart = limb.Variant;
        mob.LimbTakenAtTick = world.Tick;
        mob.LimbEatenAtTick = 0;
        mob.PrizeObjectId = 0;
        mob.TargetNpc = null;
        mob.Status = MobStatus.Roaming;
        mob.AttackLandsAtTick = 0;

        Trace.EmitSystem(world, "MobTookLimb",
            $"Dog={mob.Id} picked up {limb.Variant} from the ground " +
            $"at Tile={mob.Tile.Q},{mob.Tile.R}");
    }

    /// <summary>
    /// Укус только что оторвал <paramref name="part"/> у жертвы: зверь хватает
    /// конечность с земли. Вызывается СРАЗУ после урона — конечность уже
    /// лежит объектом (§50.4), так что берём её, а не спавним вторую.
    /// </summary>
    public static void TryTake(WorldState world, MobState mob, NPCState victim, BodyPart part)
    {
        if (!Spec135.Enabled || mob.IsCarryingLimb || mob.Health <= 0f)
        {
            return;
        }

        ObjectId? prize = null;
        foreach (var worldObject in world.Entities.Objects.Values)
        {
            if (!IsSeveredLimb(worldObject) ||
                worldObject.CurrentUser != victim.Id ||
                worldObject.Variant != part.ToString())
            {
                continue;
            }

            prize = worldObject.Id;
            break;
        }

        // Конечность не нашлась (упасть было некуда — §50.4 требует джанкшена):
        // хватать нечего, сцена не начинается.
        if (prize is not { } prizeId || !WorldObjectMutations.DespawnObject(world, prizeId))
        {
            return;
        }

        mob.CarriedLimbOwner = victim.Id;
        mob.CarriedLimbPart = part.ToString();
        mob.LimbTakenAtTick = world.Tick;
        mob.LimbEatenAtTick = 0;

        // Бой окончен ЗДЕСЬ, а не следующим средним проходом: замах на быстром
        // слое приземлился бы ещё один укус в уже брошенную жертву.
        mob.TargetNpc = null;
        mob.Status = MobStatus.Roaming;
        mob.AttackLandsAtTick = 0;
        mob.ChaseStallSinceTick = 0;

        Trace.EmitSystem(world, "MobTookLimb",
            $"Dog={mob.Id} took {part} of NPC{victim.Id.Value} " +
            $"at Tile={mob.Tile.Q},{mob.Tile.R}");
    }

    /// <summary>
    /// Ближайшая живая колонистка в гексах (int.MaxValue — на острове никого).
    /// </summary>
    public static int NearestNpcDistance(WorldState world, MobState mob)
    {
        var best = int.MaxValue;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f)
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(mob.Tile, npc.Tile);
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// Насколько далеко надо утащить: своя зона агра плюс запас, иначе встав
    /// он снова видит колонию как цель и сцена читается как «отбежал».
    /// </summary>
    public static int SafeDistanceTiles(MobState mob) =>
        MobCatalog.For(mob.MobId).AggroRadiusTiles + Spec135.RetreatMarginTiles;

    public static bool IsClearOfColony(WorldState world, MobState mob) =>
        NearestNpcDistance(world, mob) >= SafeDistanceTiles(mob);

    /// <summary>
    /// Отход окончен — зверь встал и принялся за еду. Идемпотентно.
    /// </summary>
    public static void Settle(WorldState world, MobState mob, string reason)
    {
        if (mob.LimbEatenAtTick != 0)
        {
            return;
        }

        mob.LimbEatenAtTick = world.Tick + Spec135.EatTicks;
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "MobSettledWithLimb",
                $"Dog={mob.Id} stops with {mob.CarriedLimbPart} ({reason}) " +
                $"at Tile={mob.Tile.Q},{mob.Tile.R} until tick {mob.LimbEatenAtTick}");
        }
    }

    /// <summary>
    /// Доел: конечность исчезает совсем (её уже нет в мире), зверь сыт.
    /// </summary>
    public static void Finish(WorldState world, MobState mob)
    {
        var part = mob.CarriedLimbPart;
        Clear(mob);
        mob.SatedUntilTick = world.Tick + Spec135.SatedTicks;
        Trace.EmitSystem(world, "MobAteLimb",
            $"Dog={mob.Id} ate {part} — sated until tick {mob.SatedUntilTick}");
    }

    /// <summary>
    /// Зверя убили с добычей в зубах — конечность возвращается в мир там, где
    /// он упал. Иначе она исчезала бы вместе с ним, а игрок видел, как её
    /// уносят: пропажа читалась бы багом.
    /// </summary>
    public static void DropAtDeath(WorldState world, MobState mob)
    {
        if (!mob.IsCarryingLimb)
        {
            return;
        }

        if (mob.CarriedLimbOwner is { } owner &&
            world.Entities.Npcs.TryGetValue(owner, out var victim))
        {
            var limb = WorldObjectMutations.SpawnObject(
                world, SeveredLimbObjectId, victim.Fragment, mob.Tile, mob.Junction);
            limb.CurrentUser = owner;
            limb.Variant = mob.CarriedLimbPart;
            // 360 = «поворот задан и равен нулю» (§50.4: ноль — легаси-сентинел
            // «не задан»). Поза павшего зверя нам не поза конечности, так что
            // вид соберёт её из референса — свежей она уже не считается.
            limb.RotationDegrees = 360f;
            limb.ResourceAmount = Spec50.SeveredLimbDecayTicks;
            Register(world, limb.Id);
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "MobDroppedLimb",
                    $"Dog={mob.Id} dropped {mob.CarriedLimbPart} at " +
                    $"Tile={mob.Tile.Q},{mob.Tile.R}");
            }
        }

        Clear(mob);
    }

    private static void Clear(MobState mob)
    {
        mob.CarriedLimbOwner = null;
        mob.CarriedLimbPart = string.Empty;
        mob.LimbTakenAtTick = 0;
        mob.LimbEatenAtTick = 0;
    }
}

}
