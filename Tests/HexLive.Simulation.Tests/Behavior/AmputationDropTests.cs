using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class AmputationDropTests
{
    [TestCase(0f, 360f)]
    [TestCase(90f, 90f)]
    [TestCase(-30f, 330f)]
    [TestCase(390f, 30f)]
    public void SeveredLimbPersistsOwnersHexAnchorAndFacing(
        float actorFacing,
        float storedFacing)
    {
        var world = TestWorld.CreateWorld(50050 + (int)actorFacing);
        var npc = world.Entities.Npcs.Values.First();
        var junction = npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
        npc.CurrentJunction = junction;
        npc.RotationDegrees = actorFacing;
        world.Tick = 73;

        AmputateSystemHelpers.Sever(world, npc, BodyPart.LegL);

        var limb = world.Entities.Objects.Values.Single(
            item => item.DefinitionId == "body.limb_severed");
        Assert.Multiple(() =>
        {
            Assert.That(limb.CurrentUser, Is.EqualTo(npc.Id));
            Assert.That(limb.Variant, Is.EqualTo(nameof(BodyPart.LegL)));
            Assert.That(limb.Tile, Is.EqualTo(npc.Tile));
            Assert.That(limb.Junctions, Is.EqualTo(new[] { junction }));
            Assert.That(limb.RotationDegrees, Is.EqualTo(storedFacing).Within(0.0001f));
            Assert.That(limb.SpawnTick, Is.EqualTo(world.Tick));
            Assert.That(limb.ResourceAmount, Is.EqualTo(Spec50.SeveredLimbDecayTicks));
        });
    }
}

}
