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
/// §133: одежда персонализирована. Владение живёт в двух зеркальных полях —
/// <see cref="ItemInstance.OwnerId"/> у надетой вещи и
/// <see cref="WorldObjectState.Owner"/> у лежащей, — и обязано переживать и
/// сейв, и путь «сняла → подняла».
/// </summary>
public sealed class ClothingOwnershipTests
{
    private static WorldState RoundTrip(WorldState world)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(12345);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        return loaded;
    }

    [Test]
    public void OwnerSurvivesTheSaveForWornAndCarriedItems()
    {
        var world = TestWorld.CreateWorld(12345);
        var npc = world.Entities.Npcs.Values.First();
        // §52.9 / баг #124: загрузка чинит тело, на котором две вещи дерутся за
        // один (слой, слот) — проигравшая уходит в рюкзак. Девушка выходит на
        // берег уже в лифчике, так что «надеть ВТОРОЙ поверх» проверяло бы не
        // владение, а живучесть невозможного состояния. Тест про OwnerId, а не
        // про гардероб: раздеваем, потом надеваем.
        npc.WornItems.Clear();
        var worn = new ItemInstance("underwear.bra_riot") { OwnerId = npc.Id.Value };
        npc.WornItems.Add(worn);
        npc.Inventory.Items.Add(new ItemInstance("underwear.thong_anarchy") { OwnerId = npc.Id.Value });

        var loaded = RoundTrip(world);
        var reloaded = loaded.Entities.Npcs[npc.Id];

        Assert.That(reloaded.WornItems.First(i => i.DefinitionId == worn.DefinitionId).OwnerId,
            Is.EqualTo(npc.Id.Value), "Надетая вещь потеряла хозяйку при загрузке.");
        Assert.That(reloaded.Inventory.Items
                .First(i => i.DefinitionId == "underwear.thong_anarchy").OwnerId,
            Is.EqualTo(npc.Id.Value), "Носимая вещь потеряла хозяйку при загрузке.");
    }

    /// <summary>
    /// ⭐ Ради этого всё и делалось: снятая вещь помнит хозяйку и лёжа на земле,
    /// иначе подруга наденет её, ни у кого не спросив.
    /// </summary>
    [Test]
    public void DoffedGarmentKeepsItsOwnerOnTheGround()
    {
        var world = TestWorld.CreateWorld(12345);
        var npc = world.Entities.Npcs.Values.First();
        var garment = new ItemInstance("underwear.bra_riot") { OwnerId = npc.Id.Value };
        npc.WornItems.Add(garment);
        npc.WornItems.Remove(garment);

        var dropped = ExecutionSystem.DropItemAtFeet(world, npc, garment);

        Assert.That(dropped, Is.Not.Null, "Вещь некуда было положить — тест бессмысленен.");
        Assert.That(dropped.Owner, Is.EqualTo(npc.Id),
            "Лежащая вещь стала ничейной — владение не пережило снятие.");
    }

    [Test]
    public void TakingKeepsAFriendsGarmentHersButClaimsAnOutsidersAndOwnerlessOnes()
    {
        var world = TestWorld.CreateWorld(12345);
        var colonists = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        Assert.That(colonists.Length, Is.EqualTo(2), "Нужны две колонистки.");
        var taker = colonists[0];
        var friend = colonists[1];

        var ownerless = new WorldObjectState { DefinitionId = "underwear.bra_riot" };
        Assert.That(ClothingOwnership.ResolveOnTake(world, taker, ownerless),
            Is.EqualTo(taker.Id.Value), "Ничейная вещь не досталась взявшей.");

        var friends = new WorldObjectState { DefinitionId = "underwear.bra_riot", Owner = friend.Id };
        Assert.That(ClothingOwnership.ResolveOnTake(world, taker, friends),
            Is.EqualTo(friend.Id.Value), "Одолженное у подруги сменило хозяйку — это присвоение, а не заём.");
        Assert.That(ClothingOwnership.FellowOwner(world, taker, friends), Is.EqualTo(friend),
            "Вещь подруги не опознана как чужая — разрешение спрашивать будет не у кого.");

        var outsider = world.Entities.Npcs.Values.FirstOrDefault(n => n.Faction != Faction.Colony);
        if (outsider != null)
        {
            var loot = new WorldObjectState { DefinitionId = "underwear.bra_riot", Owner = outsider.Id };
            Assert.That(ClothingOwnership.ResolveOnTake(world, taker, loot),
                Is.EqualTo(taker.Id.Value), "Трофей с чужака не сменил владельца.");
            Assert.That(ClothingOwnership.FellowOwner(world, taker, loot), Is.Null,
                "У чужака собираются спрашивать разрешение.");
        }
    }

    [Test]
    public void DeadOwnersGarmentBecomesOwnerless()
    {
        var world = TestWorld.CreateWorld(12345);
        var npc = world.Entities.Npcs.Values.First();
        var dropped = ExecutionSystem.DropItemAtFeet(
            world, npc, new ItemInstance("underwear.bra_riot") { OwnerId = npc.Id.Value });
        Assert.That(dropped, Is.Not.Null);

        npc.Health = 0f;
        Assert.That(ClothingOwnership.ResolveOnTake(world, npc, dropped),
            Is.EqualTo(npc.Id.Value));

        var other = world.Entities.Npcs.Values.First(n => n.Id != npc.Id);
        Assert.That(ClothingOwnership.FellowOwner(world, other, dropped), Is.Null,
            "У покойной всё ещё спрашивают разрешение.");
    }
}

}
