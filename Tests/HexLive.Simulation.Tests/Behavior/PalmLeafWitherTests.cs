using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// #469 supersedes #338: old leaves dry and remain usable instead of disappearing.
/// </summary>
public sealed class PalmLeafWitherTests
{
    [Test]
    public void UnclaimedLeafWithersAndFreshOneStays()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.Tick=SimBalance.PalmLeafWitherTicks+100;
        foreach(var npc in world.Entities.Npcs.Values) npc.Mind.ManualControl=true;

        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0);
        var fragment = world.Entities.Npcs.Values.First().Fragment;
        var old = HexLive.Simulation.Core.WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmLeaf, fragment, anchor.Tiles[0], anchor.Id);
        old.LeafDryingStartTick = world.Tick-SimBalance.PalmLeafWitherTicks;
        var fresh = HexLive.Simulation.Core.WorldObjectMutations.SpawnObject(
            world, ContentIds.PalmLeaf, fragment, anchor.Tiles[0], anchor.Id);
        fresh.LeafDryingStartTick = world.Tick;

        // прогнать за границу медленного слоя, чтобы система точно сработала
        for (var t = 0; t < 120; t++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(old.Id), Is.True,
                "Сухой лист остаётся ресурсом и топливом.");
            Assert.That(HexLive.Simulation.Runtime.LeafDryingMath.Fraction(world.Tick,old.LeafDryingStartTick),Is.EqualTo(1f));
            Assert.That(world.Entities.Objects.ContainsKey(fresh.Id), Is.True,
                "Свежий лист вянуть не должен.");
        });
    }
}
