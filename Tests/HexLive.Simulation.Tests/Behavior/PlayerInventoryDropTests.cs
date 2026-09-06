using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§123.5: explicit disposal frees cargo instead of being capacity-gated.</summary>
public sealed class PlayerInventoryDropTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void WearingSmallerGarmentWithFullInventoryKeepsEveryPhysicalItem(bool manual)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Mind.ManualControl = manual;
        npc.Mind.OutfitLocked = false;
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        const string oldId = "test.player_wear.large";
        const string newId = "test.player_wear.small";
        foreach (var id in new[] { oldId, newId })
        {
            var definition = new ObjectDefinition
            {
                Id = id, DisplayName = id, Layer = WearLayer.Wear,
                InventoryCapacity = id == oldId ? 3 : 0
            };
            definition.Covers.Add(BodyPart.Torso);
            world.Content.ObjectDefinitions[id] = definition;
        }
        var oldGarment = new ItemInstance(oldId) { Dirtiness = 0.4f, OwnerId = npc.Id.Value };
        var replacement = new ItemInstance(newId) { Dirtiness = 0.2f };
        npc.WornItems.Add(oldGarment);
        EquipmentMath.RecalculateCapacity(world, npc);
        npc.Inventory.Items.Add(replacement);
        while (npc.Inventory.UsedSlots < npc.Inventory.Capacity)
        {
            var id = "test.player_wear.cargo." + npc.Inventory.Items.Count;
            world.Content.ObjectDefinitions[id] = new ObjectDefinition { Id = id, DisplayName = id };
            npc.Inventory.Items.Add(new ItemInstance(id));
        }
        var cargo = npc.Inventory.Items.Where(i => !ReferenceEquals(i, replacement)).ToArray();
        var goal = npc.Mind.CurrentGoal;
        var steps = npc.Plan.Steps.ToArray();
        Assert.That(PlayerInventoryCommandExecutor.TryApply(world, npc,
            new InventoryItemRef(InventoryItemSource.Carried, 0, newId),
            InventoryAction.Wear, out var reason), Is.True, reason);
        var dropped = world.Entities.Objects.Values.Single(o => o.DefinitionId == oldId);
        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(replacement));
            Assert.That(dropped.Dirtiness, Is.EqualTo(oldGarment.Dirtiness));
            Assert.That(dropped.Owner, Is.EqualTo(npc.Id));
            Assert.That(npc.Inventory.UsedSlots, Is.LessThanOrEqualTo(npc.Inventory.Capacity));
            Assert.That(cargo.All(item => npc.Inventory.Items.Any(i => ReferenceEquals(i, item)) ||
                dropped.Contents.Any(i => ReferenceEquals(i, item))), Is.True);
            Assert.That(npc.Inventory.Items.Count + dropped.Contents.Count, Is.EqualTo(cargo.Length));
            Assert.That(npc.Mind.ManualControl, Is.EqualTo(manual));
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(goal));
            Assert.That(npc.Plan.Steps, Is.EqualTo(steps));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroundDressPermissionDistinguishesExplicitOrderFromAutonomousChoice(bool manual)
    {
        var world = TestWorld.CreateWorld();
        var colony = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        var npc = colony[0];
        npc.Mind.ManualControl = manual;
        npc.Mind.OutfitLocked = false;
        npc.Plan.Goal = manual ? GoalType.PlayerOrder : GoalType.Dress;
        const string id = "test.player_wear.borrowed";
        var definition = new ObjectDefinition { Id = id, DisplayName = id, Layer = WearLayer.Wear };
        definition.Covers.Add(BodyPart.Torso);
        world.Content.ObjectDefinitions[id] = definition;
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked && j.Fragment == npc.Fragment);
        var garment = WorldObjectMutations.SpawnObject(world, id, anchor.Fragment, anchor.Tiles[0],
            anchor.Id);
        garment.Owner = colony[1].Id;
        var dressed = ExecutionSystem.CompleteDress(world, npc, garment, definition,
            new InteractionDefinition { Type = InteractionType.Dress }, "");
        Assert.That(dressed, Is.EqualTo(manual));
        Assert.That(world.Entities.Objects.ContainsKey(garment.Id), Is.EqualTo(!manual));
        Assert.That(npc.WornItems.Any(i => i.DefinitionId == id), Is.EqualTo(manual));
    }

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
