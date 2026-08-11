using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §46 v4: ночная стая ГОСТИТ и уходит; остров держит только MaxDogs жителей.
/// <para>
/// ⭐ Ловушка, ради которой тесты существуют: раньше в <c>MobSystem.Run</c> два
/// спавнера стояли рядом, и потолок читал только один — амбиентный. Ветка
/// рейда звала <c>TrySpawnDog</c> <c>RaidPackSize</c> раз мимо потолка, а уйти
/// моб мог ТОЛЬКО смертью, и список мобов сериализуется. Получался храповик:
/// у игрока накопилось 14 собак при потолке 2, и каждая стоила и тика
/// симуляции, и своего скина в кадре. Отказ был молчаливым — ни исключения,
/// ни лога, просто мир тяжелеет с каждым часом игры.
/// </para>
/// </summary>
[NonParallelizable]
public sealed class RaidGuestTests
{
    [Test]
    public void ResidentsOverTheCapBecomeGuests()
    {
        var world = TestWorld.CreateWorld();
        var mobs = new MobSystem();

        // Дождаться настоящего рейда можно только подгонкой сида, поэтому
        // проверяется сам инвариант: жителей не больше потолка, а всё сверх
        // получило срок ухода — и срок этот в будущем, иначе стая исчезла бы
        // в тот же тик, в который пришла.
        SpawnDogs(world, WildlifeBalance.MaxDogs + 4);
        mobs.Run(world);

        var residents = world.Mobs.Count(m => m.LeavesAtTick == 0);
        Assert.That(residents, Is.LessThanOrEqualTo(WildlifeBalance.MaxDogs),
            "жителей сверх потолка остаться не должно");
        Assert.That(world.Mobs.Where(m => m.LeavesAtTick != 0).Select(m => m.LeavesAtTick),
            Is.All.GreaterThan(world.Tick));
    }

    [Test]
    public void GuestLeavesOnlyWhenNobodyIsWatching()
    {
        var world = TestWorld.CreateWorld();
        var mobs = new MobSystem();
        var perception = new PerceptionSystem();

        // ⭐ Не на тике 0: LeavesAtTick = 0 означает ЖИТЕЛЯ, так что срок,
        // упавший в ноль, молча превратил бы гостя обратно в жителя. В игре
        // рейд возможен только с цикла 2 (тик ≥ 4800), но тест обязан стоять
        // на правдоподобном тике, а не ловить это совпадение.
        world.Tick = 5000;

        var watcher = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        watcher.Attributes.Perception = 0.5f; // радиус 5 гексов

        var guest = SpawnDogs(world, 1).Single();
        guest.LeavesAtTick = world.Tick; // срок вышел прямо сейчас
        PlaceAt(guest, NearestJunctionTo(world, watcher.Tile));

        perception.Run(world);
        mobs.Run(world);
        Assert.That(world.Mobs, Does.Contain(guest),
            "на глазах у колонистки гость испаряться не должен");

        // Первый прогон поставил её вплотную к колонистке, и RunDog перевёл её
        // в ПОГОНЮ; гость в погоне не уходит намеренно. Возвращаем спокойное
        // состояние — это и есть «стая отстала и побрела прочь».
        PlaceAt(guest, FarthestJunctionFrom(world, watcher.Tile));
        guest.Status = MobStatus.Roaming;
        guest.TargetNpc = null;
        perception.Run(world);
        mobs.Run(world);
        Assert.That(world.Mobs, Does.Not.Contain(guest),
            "вне поля зрения гость уходит");
    }

    [Test]
    public void BackstopLeavesEvenUnderWatch()
    {
        var world = TestWorld.CreateWorld();
        var mobs = new MobSystem();
        var perception = new PerceptionSystem();

        world.Tick = 5000; // см. коммент про тик 0 выше

        var watcher = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        watcher.Attributes.Perception = 1f; // максимальный радиус

        var guest = SpawnDogs(world, 1).Single();
        PlaceAt(guest, NearestJunctionTo(world, watcher.Tile));
        // Срок вышел давно: предохранитель обязан увести гостя даже под
        // взглядом, иначе колония, вставшая лагерем на нём, сделала бы его
        // вечным — то есть вернула бы ровно тот баг, который мы чиним.
        guest.LeavesAtTick = world.Tick - WildlifeBalance.RaidDepartureBackstopTicks;

        perception.Run(world);
        mobs.Run(world);
        Assert.That(world.Mobs, Does.Not.Contain(guest));
    }

    [Test]
    public void GuestStaysAGuestAcrossSave()
    {
        var world = TestWorld.CreateWorld();
        var guest = SpawnDogs(world, 1).Single();
        guest.LeavesAtTick = world.Tick + 777;

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

        var restored = loaded.Mobs.Single(m => m.Id == guest.Id);
        Assert.That(restored.LeavesAtTick, Is.EqualTo(guest.LeavesAtTick),
            "без записи в сейв каждая загрузка производила бы гостей в жители — " +
            "это и есть исходный храповик");
    }

    private static MobState[] SpawnDogs(WorldState world, int count)
    {
        var junctions = FreeJunctions(world).Take(count).ToArray();
        Assert.That(junctions.Length, Is.EqualTo(count), "мало свободных узлов для теста");

        var spawned = new MobState[count];
        for (var i = 0; i < count; i++)
        {
            var dog = new MobState
            {
                Id = world.NextMobId++,
                MobId = Content.MobIds.Dog,
                Health = Content.MobCatalog.For(Content.MobIds.Dog).MaxHealth,
            };
            PlaceAt(dog, junctions[i]);
            world.Mobs.Add(dog);
            spawned[i] = dog;
        }

        return spawned;
    }

    private static void PlaceAt(MobState dog, Junction junction)
    {
        dog.Junction = junction.Id;
        dog.Tile = junction.Tiles[0];
        dog.Position = junction.WorldPosition;
        dog.TargetPosition = junction.WorldPosition;
        dog.GlideAnchor = junction.WorldPosition;
    }

    private static System.Collections.Generic.IEnumerable<Junction> FreeJunctions(WorldState world) =>
        world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0)
            .OrderBy(j => j.Id.Value);

    private static Junction NearestJunctionTo(WorldState world, Common.TileCoord tile) =>
        FreeJunctions(world)
            .OrderBy(j => HexSpatialMath.HexDistance(j.Tiles[0], tile))
            .First();

    private static Junction FarthestJunctionFrom(WorldState world, Common.TileCoord tile) =>
        FreeJunctions(world)
            .OrderByDescending(j => HexSpatialMath.HexDistance(j.Tiles[0], tile))
            .First();
}

}
