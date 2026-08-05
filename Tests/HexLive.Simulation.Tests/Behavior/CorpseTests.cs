using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §28.15C v4: двое суток она лежит сама, затем остаются скелет и мешок.
///
/// <para>
/// До этого смерть была исчезновением: сущность удалялась, гардероб и карманы
/// сыпались кучей под ноги. Теперь тяжёлый NPCState живёт ровно двое игровых суток,
/// а затем все вещи переезжают в Contents одного лёгкого world-object.
/// </para>
/// </summary>
public sealed class CorpseTests
{
    /// <summary>Убить первую попавшуюся и дать движку прогнать смерть.</summary>
    private static (SimulationEngine engine, EntityId deadId) Kill()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var victim = world.Entities.Npcs.Values.First();

        // Чтобы было что обирать, независимо от того, с чем её посеял мир.
        victim.Inventory.Items.Add(new ItemInstance(ContentIds.Stone));
        victim.Health = 0f;

        // Подметание мёртвых живёт в MobSystem, на своём слое тиков.
        for (var i = 0; i < 8 && world.Entities.Npcs.ContainsKey(victim.Id); i++)
        {
            engine.Step();
        }

        return (engine, victim.Id);
    }

    [Test]
    public void DeathMovesTheBodyOutOfTheRosterButNotOutOfTheWorld()
    {
        var (engine, deadId) = Kill();
        var world = engine.World;

        Assert.That(world.Entities.Npcs.ContainsKey(deadId), Is.False,
            "Мёртвая осталась в живом ростере — её будет тикать каждая система.");
        Assert.That(world.Entities.Corpses.ContainsKey(deadId), Is.True,
            "Тела нет в реестре трупов: смерть снова стала исчезновением.");
    }

    /// <summary>
    /// ⭐ Ради этого всё и делалось: вещи остаются НА НЕЙ, а не под ней.
    /// </summary>
    [Test]
    public void HerThingsStayOnTheBody()
    {
        var (engine, deadId) = Kill();
        var body = engine.World.Entities.Corpses[deadId];

        Assert.That(body.Inventory.Items, Is.Not.Empty,
            "Карманы опустели — смерть снова вываливает вещи на землю.");
    }

    /// <summary>
    /// До границы двух суток тело и вещи остаются на самой покойной.
    /// </summary>
    [Test]
    public void TheBodyStaysUntilTwoFullDaysHaveElapsed()
    {
        var (engine, deadId) = Kill();
        var world = engine.World;

        var anchor = world.Entities.Objects.Values.FirstOrDefault(
            o => o.DefinitionId == ContentIds.CorpseNpc && o.CurrentUser == deadId);
        Assert.That(anchor, Is.Not.Null, "Объект-якорь corpse.npc не появился.");

        world.Tick = anchor.SpawnTick + CorpseSystem.HumanCorpseLifetimeTicks - 1;
        new CorpseSystem().Run(world);

        Assert.That(world.Entities.Objects.ContainsKey(anchor.Id), Is.True,
            "Якорь исчез раньше полных двух игровых суток.");
        Assert.That(world.Entities.Corpses.ContainsKey(deadId), Is.True,
            "NPCState исчез раньше границы двух суток.");
    }

    [Test]
    public void TwoDaysReplaceTheBodyWithOneSkeletonAndLootBag()
    {
        var (engine, deadId) = Kill();
        var world = engine.World;
        var body = world.Entities.Corpses[deadId];
        body.WornItems.Add(new ItemInstance(ContentIds.Rope));
        var pocketIds = body.Inventory.Items.Select(i => i.DefinitionId).ToArray();
        var anchor = world.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.CorpseNpc && o.CurrentUser == deadId);
        var junction = anchor.Junctions[0];
        var rotation = body.RotationDegrees;

        world.Tick = anchor.SpawnTick + CorpseSystem.HumanCorpseLifetimeTicks;
        new CorpseSystem().Run(world);

        Assert.That(world.Entities.Corpses.ContainsKey(deadId), Is.False,
            "Тяжёлый NPCState остался в мире после двух суток.");
        var remains = world.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.HumanRemains && o.CurrentUser == deadId);
        Assert.That(remains.Junctions[0], Is.EqualTo(junction),
            "Скелет переехал с места смерти.");
        Assert.That(remains.RotationDegrees, Is.EqualTo(rotation),
            "Скелет не повторяет курс позы тела.");
        Assert.That(remains.Variant, Is.EqualTo((body.DeathAnimVariant & 1).ToString()),
            "Вариант спрайта не привязан к позе падения.");
        Assert.That(remains.Contents.Take(pocketIds.Length).Select(i => i.DefinitionId),
            Is.EqualTo(pocketIds), "Карманы должны идти в мешке раньше одежды.");
        Assert.That(remains.Contents.Last().DefinitionId, Is.EqualTo(ContentIds.Rope));
    }

    [Test]
    public void LootBagSurvivesSaveAndReloadAsOneWorldObject()
    {
        var (engine, deadId) = Kill();
        var world = engine.World;
        var anchor = world.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.CorpseNpc && o.CurrentUser == deadId);
        world.Tick = anchor.SpawnTick + CorpseSystem.HumanCorpseLifetimeTicks;
        new CorpseSystem().Run(world);
        var before = world.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.HumanRemains && o.CurrentUser == deadId);

        var blob = new MemoryStream();
        using (var w = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, w);
        }

        blob.Position = 0;
        var reloaded = TestWorld.CreateWorld();
        using (var r = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(reloaded, r);
        }

        var after = reloaded.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.HumanRemains && o.CurrentUser == deadId);
        Assert.That(after.Contents.Select(i => i.DefinitionId),
            Is.EqualTo(before.Contents.Select(i => i.DefinitionId)),
            "Мешок потерял содержимое после загрузки.");
        Assert.That(reloaded.Entities.Objects.Values.Count(
            o => o.DefinitionId == ContentIds.HumanRemains && o.CurrentUser == deadId), Is.EqualTo(1),
            "Вместо одного мешка появилось несколько world-object'ов.");
    }

    [Test]
    public void SkeletonLootComesFromTheBagOneItemAtATime()
    {
        var (engine, deadId) = Kill();
        var world = engine.World;
        var anchor = world.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.CorpseNpc && o.CurrentUser == deadId);
        world.Tick = anchor.SpawnTick + CorpseSystem.HumanCorpseLifetimeTicks;
        new CorpseSystem().Run(world);
        var remains = world.Entities.Objects.Values.Single(
            o => o.DefinitionId == ContentIds.HumanRemains && o.CurrentUser == deadId);
        var count = remains.Contents.Count;

        var spoil = CorpseMath.NextSpoil(world, remains, out var source);
        Assert.That(spoil, Is.Not.Null);
        Assert.That(source, Is.EqualTo(CorpseMath.SpoilSource.Bag));
        Assert.That(CorpseMath.TakeSpoil(world, remains, spoil, source), Is.True);
        Assert.That(remains.Contents.Count, Is.EqualTo(count - 1),
            "Один такт лута должен забирать из мешка ровно одну вещь.");
    }

    /// <summary>
    /// Загрузка обязана вернуть ТУ ЖЕ женщину: там же, в той же одежде, с теми
    /// же карманами и в той же позе. Поза — это DeathAnimVariant: без него
    /// тело лежало бы по-новому после каждой перезагрузки.
    /// </summary>
    [Test]
    public void TheBodySurvivesASaveAndReload()
    {
        var (engine, deadId) = Kill();
        var before = engine.World.Entities.Corpses[deadId];

        var blob = new MemoryStream();
        using (var w = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(engine.World, w);
        }

        blob.Position = 0;
        var reloaded = TestWorld.CreateWorld();
        using (var r = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(reloaded, r);
        }

        Assert.That(reloaded.Entities.Corpses.ContainsKey(deadId), Is.True,
            "После загрузки мёртвых на острове не осталось — сейв их не несёт.");

        var after = reloaded.Entities.Corpses[deadId];
        Assert.That(after.Tile, Is.EqualTo(before.Tile), "Тело переехало на другой гекс.");
        Assert.That(after.DeathAnimVariant, Is.EqualTo(before.DeathAnimVariant),
            "Поза падения не сохранилась — после каждой загрузки она лежала бы иначе.");
        Assert.That(after.Inventory.Items.Select(i => i.DefinitionId),
            Is.EquivalentTo(before.Inventory.Items.Select(i => i.DefinitionId)),
            "Карманы не пережили перезагрузку.");
        Assert.That(after.WornItems.Select(i => i.DefinitionId),
            Is.EquivalentTo(before.WornItems.Select(i => i.DefinitionId)),
            "Одежда не пережила перезагрузку — тело загрузилось голым.");
    }

    /// <summary>
    /// §28.15F: обирают по ОДНОЙ вещи, карманы раньше одежды. Порядок держит
    /// ёмкость: снятая куртка забрала бы с собой слоты под то, что в ней лежит.
    /// </summary>
    [Test]
    public void PocketsComeOffBeforeClothes()
    {
        var (engine, deadId) = Kill();
        var body = engine.World.Entities.Corpses[deadId];
        body.WornItems.Add(new ItemInstance(ContentIds.Stone));

        var pocketCount = body.Inventory.Items.Count;
        Assert.That(pocketCount, Is.GreaterThan(0), "Нечего проверять: карманы пусты.");

        var spoil = CorpseMath.NextSpoil(body, out var fromPockets);
        Assert.That(fromPockets, Is.True, "Полезли за одеждой, не опустошив карманы.");
        Assert.That(CorpseMath.TakeSpoil(body, spoil, fromPockets), Is.True);
        Assert.That(body.Inventory.Items.Count, Is.EqualTo(pocketCount - 1),
            "Взяли не одну вещь за раз.");
    }

    [Test]
    public void AnEmptyBodyIsNoLongerWorthTheTrip()
    {
        var (engine, deadId) = Kill();
        var body = engine.World.Entities.Corpses[deadId];

        body.Inventory.Items.Clear();
        body.WornItems.Clear();

        Assert.That(CorpseMath.HasSpoils(body), Is.False,
            "Пустое тело всё ещё зовёт обирать себя — цель будет выигрывать " +
            "аукцион и возвращаться ни с чем, проход за проходом.");
    }
}

}
