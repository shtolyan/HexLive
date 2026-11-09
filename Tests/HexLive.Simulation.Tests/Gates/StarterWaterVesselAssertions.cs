using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

internal static class StarterWaterVesselAssertions
{
    internal static void HasOneEmptyLeatherFlask(NPCState npc, string scenario)
    {
        var label = scenario + ": NPC" + npc.Id.Value;
        var flasks = npc.Inventory.Items.Where(item => item.DefinitionId == ContentIds.LeatherFlask).ToArray();
        Assert.That(flasks, Has.Length.EqualTo(1), label + " — одна личная кожаная фляга");
        var flask = flasks[0];
        Assert.Multiple(() => {
            Assert.That(BottleInventoryMath.Capacity(flask), Is.EqualTo(8), label + " — физическая вместимость два кокоса");
            Assert.That(flask.ResourceAmount, Is.Zero, label + " — пустой физический экземпляр");
            Assert.That(flask.WaterKind, Is.EqualTo(WaterKind.None), label + " — без вида воды");
            Assert.That(BottleInventoryMath.Charges(flask), Is.Zero, label);
            Assert.That(npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.Bottle), Is.False,
                label + " — новые стартеры не получают пластиковую бутылку");
        });
    }
}
