using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
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

    [Test]
    public void DroppingPartOfStackMovesExactlyRequestedPhysicalInstances()
    {
        var engine = TestWorld.CreateEngine(39701);
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && n.Health > 0f);
        npc.Inventory.Items.Clear();
        const string id = "resource.test_drop_stack";
        world.Content.ObjectDefinitions[id] = new ObjectDefinition
            { Id = id, DisplayName = id };
        var stack = Enumerable.Range(0, 5)
            .Select(i => new ItemInstance(id)
            {
                Durability = 0.51f + i * 0.01f,
                Wetness = 0.11f + i * 0.01f,
                OwnerId = npc.Id.Value
            })
            .ToArray();
        npc.Inventory.Items.AddRange(stack);
        var beforeObjects = world.Entities.Objects.Keys.ToHashSet();

        var result = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, id),
            InventoryAction.Drop,
            count: 3));

        var dropped = world.Entities.Objects.Values
            .Where(o => !beforeObjects.Contains(o.Id) && o.DefinitionId == id)
            .OrderBy(o => o.Durability)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True, result.Reason);
            Assert.That(npc.Inventory.Items, Is.EqualTo(stack.Skip(3)));
            Assert.That(dropped, Has.Length.EqualTo(3));
            Assert.That(dropped.Select(o => o.Durability),
                Is.EqualTo(stack.Take(3).Select(i => i.Durability)));
            Assert.That(dropped.Select(o => o.Wetness),
                Is.EqualTo(stack.Take(3).Select(i => i.Wetness)));
            Assert.That(dropped.All(o => o.Owner == npc.Id), Is.True);
        });
    }

    [TestCase(0)]
    [TestCase(6)]
    [TestCase(21)]
    public void InvalidStackDropCountIsRejectedWithoutMutation(int count)
    {
        var engine = TestWorld.CreateEngine(39702 + count);
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && n.Health > 0f);
        npc.Inventory.Items.Clear();
        const string id = "resource.test_drop_count";
        world.Content.ObjectDefinitions[id] = new ObjectDefinition
            { Id = id, DisplayName = id };
        var items = Enumerable.Range(0, 5).Select(_ => new ItemInstance(id)).ToArray();
        npc.Inventory.Items.AddRange(items);
        var beforeObjects = world.Entities.Objects.Keys.ToArray();

        var result = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, id),
            InventoryAction.Drop,
            count));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo("InvalidCount"));
            Assert.That(npc.Inventory.Items, Is.EqualTo(items));
            Assert.That(world.Entities.Objects.Keys, Is.EquivalentTo(beforeObjects));
        });
    }

    [Test]
    public void InvalidCountCannotHideAStaleReference()
    {
        var engine = TestWorld.CreateEngine(39703);
        var npc = engine.World.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance("resource.stone"));

        var result = engine.ApplyManualCommand(new ManageInventoryCommand(
            npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, "resource.stick"),
            InventoryAction.Drop,
            count: 0));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo("StaleItem"));
            Assert.That(npc.Inventory.Items.Single().DefinitionId,
                Is.EqualTo("resource.stone"));
        });
    }

    [Test]
    public void FailedSecondDropRollsBackWorldInventoryClaimsAndFullEventRing()
    {
        var world = TestWorld.CreateWorld(39704);
        var npc = world.Entities.Npcs.Values.First(n =>
            n.Faction == Faction.Colony && n.Health > 0f);
        npc.Inventory.Items.Clear();
        const string id = "resource.test_drop_obstacle";
        var definition = new ObjectDefinition { Id = id, DisplayName = id };
        definition.Tags.Add("Obstacle");
        world.Content.ObjectDefinitions[id] = definition;
        var items = new[]
        {
            new ItemInstance(id) { Durability = 0.61f },
            new ItemInstance(id) { Durability = 0.62f }
        };
        npc.Inventory.Items.AddRange(items);

        var sole = world.Tiles.Items[npc.Tile].Junctions
            .Where(j => npc.CurrentJunction is null || j != npc.CurrentJunction.Value)
            .First(j => SpatialQueries.IsJunctionPassable(world, j) &&
                        SpatialQueries.IsJunctionFree(world, j));
        npc.CurrentJunction = null;
        foreach (var junction in world.Junctions.Items.Values)
            WorldTopology.SetBlocked(world, junction, junction.Id != sole);

        world.Events.Capacity = 2;
        world.Events.Clear();
        world.Events.Add(new SimulationEvent { Tick = 10, Type = "ExistingA" });
        world.Events.Add(new SimulationEvent { Tick = 11, Type = "ExistingB" });
        var eventsBefore = world.Events.Items.ToArray();
        var highestSeqBefore = world.Events.HighestSeq;
        var objectIdsBefore = world.Entities.Objects.Keys.ToArray();
        var reservationKeysBefore = world.Reservations.Junctions.Keys.ToArray();
        var objectClaimsBefore = world.Entities.ObjectReservations.Count;
        var nextObjectIdBefore = world.NextRuntimeObjectId;

        var accepted = PlayerInventoryCommandExecutor.TryApply(
            world, npc,
            new InventoryItemRef(InventoryItemSource.Carried, 0, id),
            InventoryAction.Drop,
            out var reason,
            count: 2);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(reason, Is.EqualTo("NoDropSpot"));
            Assert.That(npc.Inventory.Items, Is.EqualTo(items));
            Assert.That(world.Entities.Objects.Keys, Is.EquivalentTo(objectIdsBefore));
            Assert.That(world.Reservations.Junctions.Keys,
                Is.EquivalentTo(reservationKeysBefore));
            Assert.That(world.Entities.ObjectReservations.Count,
                Is.EqualTo(objectClaimsBefore));
            Assert.That(world.Junctions.Items[sole].Blocked, Is.False);
            Assert.That(world.Events.Items, Is.EqualTo(eventsBefore));
            Assert.That(world.Events.HighestSeq, Is.EqualTo(highestSeqBefore));
            Assert.That(world.NextRuntimeObjectId, Is.EqualTo(nextObjectIdBefore + 1),
                "Rolled-back ids stay consumed so a later object cannot reuse them.");
        });
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
