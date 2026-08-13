using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §135: оторванная конечность — добыча. Зверь берёт её в зубы, бросает бой,
/// отходит за пределы своей зоны агра, доедает и потом полдня сыт.
/// <para>
/// ⭐ Что здесь стережётся в первую очередь: конечность существует в ОДНОМ
/// месте. Пока она в зубах, объекта <c>body.limb_severed</c> в мире нет — иначе
/// её можно было бы подобрать с земли ровно тогда, когда игрок смотрит, как её
/// уносят. Обратная сторона того же правила — сейв: состояние живёт на
/// <c>MobState</c>, и без записи в блоб загрузка уничтожала бы ногу насовсем.
/// </para>
/// </summary>
[NonParallelizable]
public sealed class MobLimbPrizeTests
{
    [Test]
    public void TakingTheLimbEndsTheFightAndClearsItFromTheGround()
    {
        var world = TestWorld.CreateWorld();
        var npc = OutdoorNpc(world);
        var dog = SpawnDogNextTo(world, npc);
        dog.TargetNpc = npc.Id;
        dog.Status = MobStatus.Fighting;
        dog.AttackLandsAtTick = world.Tick + 2;

        AmputateSystemHelpers.Sever(world, npc, BodyPart.LegL);
        Assert.That(LimbObjects(world), Has.Length.EqualTo(1), "§50.4 роняет конечность");

        MobLimbPrize.TryTake(world, dog, npc, BodyPart.LegL);

        Assert.Multiple(() =>
        {
            Assert.That(LimbObjects(world), Is.Empty,
                "в зубах и на земле одновременно она быть не может");
            Assert.That(dog.CarriedLimbPart, Is.EqualTo(nameof(BodyPart.LegL)));
            Assert.That(dog.CarriedLimbOwner, Is.EqualTo(npc.Id));
            Assert.That(dog.TargetNpc, Is.Null, "бой окончен");
            Assert.That(dog.Status, Is.EqualTo(MobStatus.Roaming));
            Assert.That(dog.AttackLandsAtTick, Is.Zero,
                "замах на быстром слое иначе приземлил бы ещё один укус");
        });
    }

    [Test]
    public void CarrierRetreatsOutOfAggroRangeThenEatsAndGoesSated()
    {
        var world = TestWorld.CreateWorld();
        var mobs = new MobSystem();
        var npc = OutdoorNpc(world);
        var dog = SpawnDogNextTo(world, npc);

        AmputateSystemHelpers.Sever(world, npc, BodyPart.LegR);
        MobLimbPrize.TryTake(world, dog, npc, BodyPart.LegR);
        var safeDistance = MobLimbPrize.SafeDistanceTiles(dog);

        // Отход. Предохранитель §135.2 обязан закрыть даже тот случай, когда
        // уйти некуда, поэтому ждём его срок, а не «пока не отойдёт».
        for (var i = 0; i < Spec135.RetreatGiveUpTicks && dog.LimbEatenAtTick == 0; i++)
        {
            world.Tick++;
            mobs.Run(world);
        }

        Assert.That(dog.LimbEatenAtTick, Is.Not.Zero, "зверь обязан встать и приняться за еду");
        Assert.That(dog.IsCarryingLimb, Is.True, "ест он ещё не съеденное");

        // Ест стоя: за десяток тиков с места не двигается.
        var standing = dog.Junction;
        for (var i = 0; i < 10; i++)
        {
            world.Tick++;
            mobs.Run(world);
        }

        Assert.That(dog.Junction, Is.EqualTo(standing), "с добычей в зубах он стоит");

        world.Tick = dog.LimbEatenAtTick;
        mobs.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(dog.IsCarryingLimb, Is.False, "доел — конечность исчезла совсем");
            Assert.That(LimbObjects(world), Is.Empty, "и обратно в мир не вернулась");
            Assert.That(dog.SatedUntilTick, Is.EqualTo(world.Tick + Spec135.SatedTicks));
            Assert.That(MobLimbPrize.IsSated(world, dog), Is.True);
        });

        // Сытый не наводится: ставим его вплотную и убеждаемся, что цели нет.
        PlaceAt(dog, NearestFreeJunctionTo(world, npc.Tile, dog));
        world.Tick++;
        mobs.Run(world);
        Assert.That(dog.TargetNpc, Is.Null,
            $"сытый зверь не охотится (безопасная дистанция была {safeDistance} гексов)");

        // …а когда сытость проходит — снова охотится, иначе это не сытость,
        // а выключенный зверь.
        world.Tick = dog.SatedUntilTick;
        PlaceAt(dog, NearestFreeJunctionTo(world, npc.Tile, dog));
        mobs.Run(world);
        Assert.That(dog.TargetNpc, Is.Not.Null,
            $"сытость обязана проходить: вмире={world.Mobs.Contains(dog)} " +
            $"dist={HexSpatialMath.HexDistance(dog.Tile, npc.Tile)} hp={npc.Health} " +
            $"hunt={dog.NextHuntAllowedTick} tick={world.Tick} leaves={dog.LeavesAtTick} " +
            $"npcs={world.Entities.Npcs.Count}");
    }

    /// <summary>
    /// §135: драка не обязательна. Нашёл лежащую ногу — съел и успокоился.
    /// Здесь же стережётся ЦЕНА: пока падали в мире нет, поиск обязан быть
    /// сравнением счётчика с нулём, а не обходом объектов мира.
    /// </summary>
    [Test]
    public void LyingLimbIsScavengedWithoutAnyFight()
    {
        var world = TestWorld.CreateWorld();
        var mobs = new MobSystem();
        var npc = world.Entities.Npcs.Values.First();

        // Пусто — значит и индекс пуст: это и есть «ничего не стоит».
        Assert.That(world.Caches.SeveredLimbs, Is.Empty);

        var spot = FarOutdoorJunctions(world, 2);
        var limb = WorldObjectMutations.SpawnObject(
            world, "body.limb_severed", npc.Fragment, spot[0].Tiles[0], spot[0].Id);
        limb.CurrentUser = npc.Id;
        limb.Variant = nameof(BodyPart.LegL);
        MobLimbPrize.Register(world, limb.Id);

        var dog = new MobState
        {
            Id = world.NextMobId++,
            MobId = Content.MobIds.Dog,
            Health = Content.MobCatalog.For(Content.MobIds.Dog).MaxHealth,
        };
        PlaceAt(dog, spot[1]);
        world.Mobs.Add(dog);

        for (var i = 0; i < 40 && !dog.IsCarryingLimb; i++)
        {
            world.Tick++;
            mobs.Run(world);
        }

        Assert.Multiple(() =>
        {
            Assert.That(dog.IsCarryingLimb, Is.True, "лежащую ногу зверь обязан подобрать");
            Assert.That(dog.CarriedLimbPart, Is.EqualTo(nameof(BodyPart.LegL)));
            Assert.That(LimbObjects(world), Is.Empty, "с земли она при этом исчезает");
        });
    }

    [Test]
    public void KilledCarrierDropsTheLimbWhereItFell()
    {
        var world = TestWorld.CreateWorld();
        var mobs = new MobSystem();
        var npc = OutdoorNpc(world);
        var dog = SpawnDogNextTo(world, npc);

        AmputateSystemHelpers.Sever(world, npc, BodyPart.ArmL);
        MobLimbPrize.TryTake(world, dog, npc, BodyPart.ArmL);
        Assert.That(LimbObjects(world), Is.Empty);

        dog.Health = 0f;
        mobs.Run(world);

        var limb = LimbObjects(world).Single();
        Assert.Multiple(() =>
        {
            Assert.That(limb.Variant, Is.EqualTo(nameof(BodyPart.ArmL)));
            Assert.That(limb.CurrentUser, Is.EqualTo(npc.Id));
            Assert.That(limb.Tile, Is.EqualTo(dog.Tile),
                "игрок видел, как её уносят — пропажа читалась бы багом");
        });
    }

    [Test]
    public void PrizeSurvivesSave()
    {
        var world = TestWorld.CreateWorld();
        var npc = OutdoorNpc(world);
        var dog = SpawnDogNextTo(world, npc);

        AmputateSystemHelpers.Sever(world, npc, BodyPart.LegL);
        MobLimbPrize.TryTake(world, dog, npc, BodyPart.LegL);
        dog.LimbEatenAtTick = world.Tick + 500;
        dog.SatedUntilTick = world.Tick + 900;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var restored = loaded.Mobs.Single(m => m.Id == dog.Id);
        Assert.Multiple(() =>
        {
            Assert.That(restored.CarriedLimbPart, Is.EqualTo(nameof(BodyPart.LegL)));
            Assert.That(restored.CarriedLimbOwner, Is.EqualTo(npc.Id));
            Assert.That(restored.LimbTakenAtTick, Is.EqualTo(dog.LimbTakenAtTick));
            Assert.That(restored.LimbEatenAtTick, Is.EqualTo(dog.LimbEatenAtTick));
            Assert.That(restored.SatedUntilTick, Is.EqualTo(dog.SatedUntilTick));
        });
    }

    // ⭐ Колонистка ПОД КРЫШЕЙ для зверя — санктуарий (§29C.4A): он на неё не
    // наводится вообще, и проверка «сытый не охотится» стала бы пустой.
    private static NPCState OutdoorNpc(WorldState world)
    {
        // На нулевом тике вся колония стоит ПОД КРЫШЕЙ, так что выводим её во
        // двор руками — иначе зверь не наводится ни разу и тест меряет пустоту.
        var npc = world.Entities.Npcs.Values.First(n => n.Health > 0f);
        var outdoor = world.Junctions.Items.Values
            .Where(j => !j.Blocked && !j.Door && j.Tiles.Count > 0 &&
                        !IsIndoor(world, j.Tiles[0]) &&
                        !SpatialQueries.IsAllWaterJunction(world, j.Id))
            .OrderBy(j => HexSpatialMath.HexDistance(j.Tiles[0], npc.Tile))
            .ThenBy(j => j.Id.Value)
            .First();
        npc.CurrentJunction = outdoor.Id;
        npc.Tile = outdoor.Tiles[0];
        npc.Position = outdoor.WorldPosition;
        return npc;
    }

    // Два соседних уличных узла ПОДАЛЬШЕ от колонии: падаль и зверь должны
    // встретиться без того, чтобы он по дороге навёлся на живую цель.
    private static Junction[] FarOutdoorJunctions(WorldState world, int count) =>
        world.Junctions.Items.Values
            .Where(j => !j.Blocked && !j.Door && j.Tiles.Count > 0 &&
                        !IsIndoor(world, j.Tiles[0]) &&
                        !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
                        world.Entities.Npcs.Values.All(n =>
                            HexSpatialMath.HexDistance(j.Tiles[0], n.Tile) > 6))
            .OrderBy(j => j.Id.Value)
            .Take(count)
            .ToArray();

    private static bool IsIndoor(WorldState world, TileCoord tile) =>
        world.Tiles.Items.TryGetValue(tile, out var t) && t.Flags.HasFlag(TileFlags.Indoor);

    private static WorldObjectState[] LimbObjects(WorldState world) =>
        world.Entities.Objects.Values
            .Where(o => o.DefinitionId == "body.limb_severed")
            .ToArray();

    private static MobState SpawnDogNextTo(WorldState world, NPCState npc)
    {
        var dog = new MobState
        {
            Id = world.NextMobId++,
            MobId = Content.MobIds.Dog,
            Health = Content.MobCatalog.For(Content.MobIds.Dog).MaxHealth,
        };
        PlaceAt(dog, NearestFreeJunctionTo(world, npc.Tile, dog));
        world.Mobs.Add(dog);
        return dog;
    }

    private static void PlaceAt(MobState dog, Junction junction)
    {
        dog.Junction = junction.Id;
        dog.Tile = junction.Tiles[0];
        dog.Position = junction.WorldPosition;
        dog.TargetPosition = junction.WorldPosition;
        dog.GlideAnchor = junction.WorldPosition;
    }

    private static Junction NearestFreeJunctionTo(WorldState world, TileCoord tile, MobState self) =>
        world.Junctions.Items.Values
            .Where(j => !j.Blocked && !j.Door && j.Tiles.Count > 0 &&
                        !IsIndoor(world, j.Tiles[0]) &&
                        !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
                        !world.Entities.Npcs.Values.Any(n => n.CurrentJunction is { } c && c.Equals(j.Id)) &&
                        !world.Mobs.Any(m => m != self && m.Junction.Equals(j.Id)))
            .OrderBy(j => HexSpatialMath.HexDistance(j.Tiles[0], tile))
            .ThenBy(j => j.Id.Value)
            .First();
}

}
