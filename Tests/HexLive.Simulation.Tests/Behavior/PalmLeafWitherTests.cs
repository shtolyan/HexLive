using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// Bug #338: неподобранный пальмовый лист вянет (схема SpawnTick, как порча
/// мяса §54.17). Замер на сервере: 10 653 лежалых листа из 15 577 объектов
/// мира — тик подорожал с 7 до 43 мс, и мир копил их вечно.
/// </summary>
public sealed class PalmLeafWitherTests
{
    [Test]
    public void UnclaimedLeafWithersAndFreshOneStays()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;

        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
        var fragment = world.Entities.Npcs.Values.First().Fragment;
        var old = HexLive.Simulation.Core.WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmLeaf, fragment, anchor.Tiles[0], anchor.Id);
        old.SpawnTick = 1;
        var fresh = HexLive.Simulation.Core.WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmLeaf, fragment, anchor.Tiles[0], anchor.Id);
        fresh.SpawnTick = world.Tick + 1;

        // прогнать за границу медленного слоя, чтобы система точно сработала
        for (var t = 0; t < 120; t++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(old.Id), Is.False,
                "Лист, пролежавший дольше PalmLeafWitherTicks, обязан завянуть.");
            Assert.That(world.Entities.Objects.ContainsKey(fresh.Id), Is.True,
                "Свежий лист вянуть не должен.");
        });
    }
}
