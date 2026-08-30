using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

// Bug #312: пять гексов от якоря ЧУЖОГО лагеря — приватная территория; взять
// там чужую вещь — кража. Своё/союзное — не кража нигде.
public sealed class TheftMathTests
{
    [Test]
    public void ForeignPrivateGroundIsTheft_OwnGearIsNot()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var tile = npc.Tile;
        var junction = world.Tiles.Items[tile].Junctions.First();

        world.FactionHomes[Faction.Outsiders] = tile; // чужой лагерь прямо здесь
        var loot = WorldObjectMutations.SpawnObject(
            world, "resource.stick", npc.Fragment, tile, junction);

        Assert.Multiple(() =>
        {
            Assert.That(TheftMath.IsForeignPrivateGround(world, npc.Faction, tile), Is.True);
            Assert.That(TheftMath.IsTheft(world, npc, loot), Is.True,
                "Чужая палка в пяти гексах от чужого якоря — кража.");

            // Вещь, чьим владельцем записана союзница, — не кража даже там.
            loot.Owner = npc.Id;
            Assert.That(TheftMath.IsTheft(world, npc, loot), Is.False,
                "Своё забрать — не кража нигде.");

            // Свой лагерь приватной территорией для своих не считается.
            world.FactionHomes.Clear();
            world.FactionHomes[npc.Faction] = tile;
            loot.Owner = null;
            Assert.That(TheftMath.IsTheft(world, npc, loot), Is.False);
        });
    }

    [Test]
    public void WitnessOfOwnerCampLosesAffinityToThief()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList();
        Assume.That(npcs.Count, Is.GreaterThanOrEqualTo(2));
        var thief = npcs[0];
        var witness = npcs[1];
        witness.Faction = Faction.Outsiders;
        witness.Perception.Agents.Add(new HexLive.Simulation.AI.PerceivedAgent
        {
            Id = thief.Id
        });

        var tile = thief.Tile;
        var junction = world.Tiles.Items[tile].Junctions.First();
        world.FactionHomes[Faction.Outsiders] = tile;
        var loot = WorldObjectMutations.SpawnObject(
            world, "resource.stick", thief.Fragment, tile, junction);

        TheftMath.OnStolen(world, thief, loot);

        var relation = witness.Social.GetOrCreate(thief.Id);
        Assert.That(relation.Affinity, Is.LessThan(0f),
            "Свидетельница чужого лагеря, видящая воровку, теряет отношение.");
    }
}

}
