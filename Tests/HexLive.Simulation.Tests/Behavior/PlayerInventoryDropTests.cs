using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§123.5: explicit disposal frees cargo instead of being capacity-gated.</summary>
public sealed class PlayerInventoryDropTests
{
    [Test]
    public void DroppingWornPocketGarmentCarriesItsOverflowToTheGround()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && n.Health > 0f);
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();

        const string garmentId = "test.player_drop.pocket_garment";
        world.Content.ObjectDefinitions[garmentId] = new ObjectDefinition
        {
            Id = garmentId,
            DisplayName = garmentId,
            Layer = WearLayer.Wear,
            InventoryCapacity = 2
        };
        var garment = new ItemInstance(garmentId);
        npc.WornItems.Add(garment);
        EquipmentMath.RecalculateCapacity(world, npc);

        var originalCargo = new ItemInstance[npc.Inventory.Capacity];
        for (var i = 0; i < originalCargo.Length; i++)
        {
            var id = "test.player_drop.cargo." + i;
            world.Content.ObjectDefinitions[id] = new ObjectDefinition
                { Id = id, DisplayName = id };
            originalCargo[i] = new ItemInstance(id);
            npc.Inventory.Items.Add(originalCargo[i]);
        }

        var reference = new InventoryItemRef(
            InventoryItemSource.Worn, 0, garmentId);
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(npc.Inventory.Capacity));
            Assert.That(PlayerInventoryMath.FitsAfter(
                world, npc, reference, InventoryAction.Drop), Is.True,
                "An explicit drop must not be rejected by the capacity it removes.");
        });

        engine.Commands.Enqueue(new ManageInventoryCommand(
            npc.Id, reference, InventoryAction.Drop));
        Step(engine, 12);

        var dropped = world.Entities.Objects.Values.SingleOrDefault(obj =>
            obj.DefinitionId == garmentId);
        Assert.Multiple(() =>
        {
            Assert.That(dropped, Is.Not.Null);
            Assert.That(npc.WornItems.Any(item => ReferenceEquals(item, garment)), Is.False);
            Assert.That(npc.Inventory.UsedSlots, Is.LessThanOrEqualTo(npc.Inventory.Capacity));
            Assert.That(dropped!.Contents, Has.Count.EqualTo(2),
                "The two cells lost with the garment must travel inside it.");
            Assert.That(originalCargo.Count(item =>
                npc.Inventory.Items.Any(candidate => ReferenceEquals(candidate, item))),
                Is.EqualTo(originalCargo.Length - 2));
            Assert.That(world.Events.Items.Any(e =>
                e.Type == "ManualOrderRejected" && e.EntityId == npc.Id.Value &&
                e.Message.Contains("Order=Inventory") &&
                e.Message.Contains("Reason=InsufficientSpace")), Is.False);
        });
    }

    [Test]
    public void DroppingCarriedItemCanRepairAnAlreadyOverflowingPack()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        EquipmentMath.RecalculateCapacity(world, npc);

        for (var i = 0; i <= npc.Inventory.Capacity; i++)
        {
            var id = "test.player_drop.overflow." + i;
            world.Content.ObjectDefinitions[id] = new ObjectDefinition
                { Id = id, DisplayName = id };
            npc.Inventory.Items.Add(new ItemInstance(id));
        }

        Assert.That(npc.Inventory.UsedSlots, Is.GreaterThan(npc.Inventory.Capacity));
        Assert.That(PlayerInventoryMath.FitsAfter(
            world, npc,
            new InventoryItemRef(InventoryItemSource.Carried, 0,
                npc.Inventory.Items[0].DefinitionId),
            InventoryAction.Drop), Is.True,
            "A drop may improve an invalid legacy layout even if one click does not fix it all.");
    }

    private static void Step(SimulationEngine engine, int count)
    {
        for (var i = 0; i < count; i++)
        {
            engine.Step();
        }
    }
}

}
