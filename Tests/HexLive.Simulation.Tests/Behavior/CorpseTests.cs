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
/// §28.15C v3: она умирает — и остаётся лежать, одетая, до конца игры.
///
/// <para>
/// До этого смерть была исчезновением: сущность удалялась, гардероб и карманы
/// сыпались кучей под ноги, объект-труп истлевал за двое суток. Через два дня
/// от человека не оставалось ничего. Здесь проверяется, что каждое звено новой
/// цепочки на месте — и, главное, что оно переживает ПЕРЕЗАГРУЗКУ: тело,
/// которого нет в сейве, это ровно то старое поведение, только медленнее.
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
    /// Якорь для «подойти и что-то сделать» — и он больше не гниёт. Раньше
    /// ResourceAmount тикал вниз до нуля, и CorpseSystem убирал тело через
    /// двое суток вместе со всем, что на нём.
    /// </summary>
    [Test]
    public void TheAnchorStaysAndDoesNotRot()
    {
        var (engine, deadId) = Kill();
        var world = engine.World;

        var anchor = world.Entities.Objects.Values.FirstOrDefault(
            o => o.DefinitionId == ContentIds.CorpseNpc && o.CurrentUser == deadId);
        Assert.That(anchor, Is.Not.Null, "Объект-якорь corpse.npc не появился.");

        // Заведомо дольше прежнего срока жизни трупа (4800 тиков).
        for (var i = 0; i < 600; i++)
        {
            engine.Step();
        }

        Assert.That(world.Entities.Objects.ContainsKey(anchor.Id), Is.True,
            "Якорь исчез — CorpseSystem снова гноит человеческие тела.");
        Assert.That(world.Entities.Corpses.ContainsKey(deadId), Is.True,
            "Тело исчезло само собой. Убрать его может только нож (§56).");
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
