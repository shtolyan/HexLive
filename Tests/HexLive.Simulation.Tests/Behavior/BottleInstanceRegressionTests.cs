using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§52 / bug #355: bottle contents belong to physical items.</summary>
public sealed class BottleInstanceRegressionTests
{
    [TestCase(0.41f, 0.41f)]
    [TestCase(0.42f, 0.42f)]
    [TestCase(0.005f, 0.005f)]
    [TestCase(1.5f, 1f)]
    [TestCase(-0.1f, 0f)]
    public void VisualFillPreservesCollectorPercentages(float fraction, float expected)
    {
        Assert.That(BottleVisualMath.Fill(fraction, true), Is.EqualTo(expected).Within(1e-6));
        Assert.That(BottleVisualMath.Fill(fraction * SimBalance.BottleCapacity, false),
            Is.EqualTo(expected).Within(1e-6));
    }

    [Test]
    public void VisualAppearanceDoesNotLaunderRawWaterAndSurvivesDrinking()
    {
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle, WaterKind.Raw, 2);
        bottle.LastAddedWaterKind = WaterKind.Coconut;
        Assert.That(BottleVisualMath.Appearance(bottle), Is.EqualTo(WaterKind.Coconut));
        Assert.That(BottleInventoryMath.ConsumeOne(bottle, out var consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(WaterKind.Raw));
        Assert.That(bottle.WaterKind, Is.EqualTo(WaterKind.Raw));
        Assert.That(bottle.LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
        Assert.That(BottleInventoryMath.Add(bottle, WaterKind.Rain, 1), Is.Zero);
        Assert.That(bottle.LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
        BottleInventoryMath.ConsumeOne(bottle, out _);
        Assert.That(BottleVisualMath.Appearance(bottle), Is.EqualTo(WaterKind.None));
        Assert.That(bottle.LastAddedWaterKind, Is.EqualTo(WaterKind.None));
    }

    [TestCase(77, WaterKind.Raw)]
    [TestCase(78, WaterKind.Raw)]
    [TestCase(79, WaterKind.Coconut)]
    public void AppearanceSurvivesCurrentSaveAndMigratesLegacy(int version, WaterKind expected)
    {
        var world = TestWorld.CreateWorld(41701);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle, WaterKind.Raw, 3);
        bottle.LastAddedWaterKind = WaterKind.Coconut;
        npc.Inventory.Items.Add(bottle);
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked);
        var ground = WorldObjectMutations.SpawnObject(world, ContentIds.Bottle,
            anchor.Fragment, anchor.Tiles[0], anchor.Id);
        ground.ResourceAmount = 4.2f;
        ground.WaterKind = WaterKind.Raw;
        ground.LastAddedWaterKind = WaterKind.Coconut;
        var loaded = RoundTrip(world, version);
        Assert.That(Girl(loaded).Inventory.Items[0].LastAddedWaterKind, Is.EqualTo(expected));
        Assert.That(Girl(loaded).Inventory.Items[0].WaterKind, Is.EqualTo(WaterKind.Raw));
        Assert.That(loaded.Entities.Objects[ground.Id].LastAddedWaterKind, Is.EqualTo(expected));
        Assert.That(loaded.Entities.Objects[ground.Id].ResourceAmount, Is.EqualTo(4.2f));
    }

    private static NPCState Girl(WorldState world) =>
        world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();

    [Test]
    public void HydrationAidShowsTheFirstDrinkablePhysicalBottle()
    {
        var world = TestWorld.CreateWorld(41704);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle, WaterKind.Rain, 7);
        bottle.LastAddedWaterKind = WaterKind.Coconut;
        npc.Inventory.Items.Add(bottle);
        npc.Execution.CurrentInteraction = InteractionType.HydrateOther;
        npc.Execution.TargetInventoryItem = null;
        var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(n => n.Id.Equals(npc.Id));
        Assert.That(snapshot.HeldItemId, Is.EqualTo(ContentIds.Bottle));
        Assert.That(snapshot.HeldBottleFill, Is.EqualTo(0.7f).Within(1e-6));
        Assert.That(snapshot.HeldBottleAppearance, Is.EqualTo(WaterKind.Coconut));
    }

    [Test]
    public void PlayerDropKeepsBottleAppearanceAndExactAmount()
    {
        var world = TestWorld.CreateWorld(41703);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        PlaceAtOpenJunction(world, npc);
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle, WaterKind.Raw, 4);
        bottle.LastAddedWaterKind = WaterKind.Coconut;
        npc.Inventory.Items.Add(bottle);
        var previousIds = world.Entities.Objects.Keys.ToHashSet();
        Assert.That(PlayerInventoryCommandExecutor.TryApply(world, npc,
            new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.Bottle),
            InventoryAction.Drop, out var reason), Is.True, reason);
        var ground = world.Entities.Objects.Values.Single(o =>
            !previousIds.Contains(o.Id) && o.DefinitionId == ContentIds.Bottle);
        Assert.That(ground.ResourceAmount, Is.EqualTo(4f));
        Assert.That(ground.WaterKind, Is.EqualTo(WaterKind.Raw));
        Assert.That(ground.LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
    }

    [Test]
    public void NestedBottleSaveAndDeltaPreserveVisualOnlyChange()
    {
        var world = TestWorld.CreateWorld(41702);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle, WaterKind.Raw, 5);
        bottle.LastAddedWaterKind = WaterKind.Coconut;
        npc.Inventory.Items.Add(bottle);
        npc.Execution.CurrentInteraction = InteractionType.Drink;
        npc.Execution.TargetInventoryItem = bottle;
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked);
        var container = WorldObjectMutations.SpawnObject(world, ContentIds.Bottle,
            anchor.Fragment, anchor.Tiles[0], anchor.Id);
        var nested = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(nested, WaterKind.Raw, 2);
        nested.LastAddedWaterKind = WaterKind.Coconut;
        container.Contents.Add(nested);
        var loaded = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        Assert.That(loaded.Entities.Objects[container.Id].Contents[0].LastAddedWaterKind,
            Is.EqualTo(WaterKind.Coconut));

        var baseline = WorldSnapshotExporter.Export(world);
        var mirror = RoundTrip(baseline);
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(baseline, false);
        var oldTick = mirror.Tick;
        world.Tick++;
        bottle.LastAddedWaterKind = WaterKind.Rain;
        nested.LastAddedWaterKind = WaterKind.Rain;
        using var stream = new MemoryStream(encoder.Encode(WorldSnapshotExporter.Export(world), false));
        using var reader = new BinaryReader(stream);
        SnapshotDeltaReader.Apply(reader, mirror, oldTick);
        var received = mirror.Npcs.Single(n => n.Id.Equals(npc.Id));
        Assert.That(received.HeldBottleFill, Is.EqualTo(0.5f));
        Assert.That(received.HeldBottleAppearance, Is.EqualTo(WaterKind.Rain));
        Assert.That(received.InventoryContainers.SelectMany(c => c.Slots)
            .First(s => s.ItemDefinitionId == ContentIds.Bottle).LastAddedWaterKind,
            Is.EqualTo(WaterKind.Rain));
        Assert.That(mirror.Objects.Single(o => o.Id.Equals(container.Id)).Contents[0].LastAddedWaterKind,
            Is.EqualTo(WaterKind.Rain));
        Assert.That(bottle.WaterKind, Is.EqualTo(WaterKind.Raw));
    }

    [Test]
    public void MultipleBottlesCanCarryDifferentLiquidsAndCharges()
    {
        var npc = Girl(TestWorld.CreateWorld(35501));
        npc.Inventory.Items.Clear();
        var raw = new ItemInstance(ContentIds.Bottle);
        var rain = new ItemInstance(ContentIds.Bottle);
        npc.Inventory.Items.Add(raw);
        npc.Inventory.Items.Add(rain);

        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 3);
        BottleInventoryMath.SetContents(rain, WaterKind.Rain, 8);

        Assert.Multiple(() =>
        {
            Assert.That(BottleInventoryMath.Charges(raw), Is.EqualTo(3));
            Assert.That(raw.WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(rain), Is.EqualTo(8));
            Assert.That(rain.WaterKind, Is.EqualTo(WaterKind.Rain));
        });
    }

    [Test]
    public void HydrationTheftMovesOneSipBetweenMatchingPhysicalBottles()
    {
        var world = TestWorld.CreateWorld(35508);
        var people = world.Entities.Npcs.Values.OrderBy(npc => npc.Id.Value).Take(2).ToArray();
        var abuser = people[0];
        var mark = people[1];
        abuser.Inventory.Items.Clear();
        mark.Inventory.Items.Clear();
        abuser.Inventory.Capacity = 2;

        var destination = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(destination, WaterKind.Raw, 1);
        abuser.Inventory.Items.Add(destination);
        abuser.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        var source = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(source, WaterKind.Raw, 2);
        var untouched = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(untouched, WaterKind.Rain, 3);
        mark.Inventory.Items.Add(source);
        mark.Inventory.Items.Add(untouched);

        var moved = AbuseMath.TryTake(
            world, abuser, mark, AidKind.Hydrate, out var taken);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True,
                "A full pack can still accept a sip into its existing bottle.");
            Assert.That(taken, Is.EqualTo("bottle.water"));
            Assert.That(BottleInventoryMath.Charges(destination), Is.EqualTo(2));
            Assert.That(BottleInventoryMath.Charges(source), Is.EqualTo(1));
            Assert.That(source.WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(untouched), Is.EqualTo(3));
            Assert.That(untouched.WaterKind, Is.EqualTo(WaterKind.Rain));
        });
    }

    [Test]
    public void WholeBottleTheftRemovesTheExactFilledInstance()
    {
        var world = TestWorld.CreateWorld(35510);
        var people = world.Entities.Npcs.Values.OrderBy(npc => npc.Id.Value).Take(2).ToArray();
        var abuser = people[0];
        var mark = people[1];
        abuser.Inventory.Items.Clear();
        mark.Inventory.Items.Clear();
        abuser.Inventory.Capacity = 8;

        var emptyFirst = new ItemInstance(ContentIds.Bottle);
        var filledSecond = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(filledSecond, WaterKind.Boiled, 6);
        mark.Inventory.Items.Add(emptyFirst);
        mark.Inventory.Items.Add(filledSecond);

        var moved = AbuseMath.TryTake(
            world, abuser, mark, AidKind.Hydrate, out var taken);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(taken, Is.EqualTo(ContentIds.Bottle));
            Assert.That(mark.Inventory.Items.Count, Is.EqualTo(1));
            Assert.That(mark.Inventory.Items[0], Is.SameAs(emptyFirst),
                "Definition equality must not remove the empty first bottle.");
            Assert.That(abuser.Inventory.Items.Single(), Is.SameAs(filledSecond));
            Assert.That(BottleInventoryMath.Charges(filledSecond), Is.EqualTo(6));
            Assert.That(filledSecond.WaterKind, Is.EqualTo(WaterKind.Boiled));
        });
    }

    [TestCase(false, 4f)]
    [TestCase(true, 1f)]
    [TestCase(true, 0.42f)]
    public void GroundBottleCanBePickedUpAlongsideExistingBottle(bool parked, float amount)
    {
        var engine = TestWorld.CreateEngine(35502);
        var world = engine.World;
        var npc = Girl(world);
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var coconut = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(coconut, WaterKind.Coconut, 2);
        npc.Inventory.Items.Add(coconut);

        PlaceAtOpenJunction(world, npc);
        var neighbor = SpatialQueries.GetPassableNeighbors(
            world, npc.CurrentJunction!.Value).First();
        var tile = world.Junctions.Items[neighbor].Tiles[0];
        var ground = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, npc.Fragment, tile, neighbor);
        ground.WaterKind = WaterKind.Rain;
        ground.LastAddedWaterKind = WaterKind.Coconut;
        ground.ResourceAmount = amount;
        if (parked) WorldObjectMutations.SpawnObject(
            world, ContentIds.WaterCollector, npc.Fragment, tile, neighbor);

        var admission = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, ground.Id, InteractionType.PickUp));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            admission.Reason);
        for (var i = 0; i < 1000 && world.Entities.Objects.ContainsKey(ground.Id); i++)
        {
            engine.Step();
        }

        var bottles = npc.Inventory.Items.Where(BottleInventoryMath.IsBottle).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(ground.Id), Is.False);
            Assert.That(bottles, Has.Length.EqualTo(2));
            Assert.That(bottles.Single(b => b.WaterKind == WaterKind.Coconut).ResourceAmount,
                Is.EqualTo(2f));
            Assert.That(bottles.Single(b => b.WaterKind == WaterKind.Rain).ResourceAmount,
                Is.EqualTo(parked ? amount * SimBalance.BottleCapacity : amount).Within(1e-5));
            Assert.That(bottles.Single(b => b.WaterKind == WaterKind.Rain).LastAddedWaterKind,
                Is.EqualTo(WaterKind.Coconut));
        });
    }

    [TestCase(4.2f)]
    [TestCase(1.2f)]
    public void ConsumingAndAddingOneSipPreservesTheFractionalRemainder(float amount)
    {
        var world = TestWorld.CreateWorld(63401);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle,WaterKind.Raw,amount);
        bottle.LastAddedWaterKind = WaterKind.Coconut;
        npc.Inventory.Items.Add(bottle);
        Assert.That(BottleInventoryMath.ConsumeOne(bottle,out _),Is.True);
        Assert.That(bottle.ResourceAmount,Is.EqualTo(amount-1f).Within(1e-5));
        Assert.That(bottle.WaterKind,Is.EqualTo(WaterKind.Raw));
        Assert.That(bottle.LastAddedWaterKind,Is.EqualTo(WaterKind.Coconut));
        Assert.That(BottleInventoryMath.FirstEmpty(npc),Is.Null);
        Assert.That(BottleInventoryMath.Add(bottle,WaterKind.Raw,1),Is.EqualTo(1));
        Assert.That(bottle.ResourceAmount,Is.EqualTo(amount).Within(1e-5));
    }

    [Test]
    public void MixedSourceTransfersItsVisibleLiquidAndItsSafetyIndependently_Bug417()
    {
        var destination = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(destination, WaterKind.Coconut, 1.25f);
        var source = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(source, WaterKind.Raw, .42f);
        source.LastAddedWaterKind = WaterKind.Rain;

        Assert.That(VesselTransferMath.ConsolidateBottleFromSources(destination, new[] { source }),
            Is.EqualTo(.42f).Within(1e-5f));
        Assert.That(BottleVisualMath.Fill(destination),
            Is.EqualTo(1.67f / WaterVesselCatalog.Capacity(ContentIds.Bottle)).Within(1e-5f));
        Assert.That(BottleVisualMath.Appearance(destination), Is.EqualTo(WaterKind.Rain));
        Assert.That(destination.WaterKind, Is.EqualTo(WaterKind.Raw));
        Assert.That(source.LastAddedWaterKind, Is.EqualTo(WaterKind.None));
    }

    [Test]
    public void FractionalBottleRoomCannotOverdrawOrLaunderWater()
    {
        var world = TestWorld.CreateWorld(63402);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        var bottle = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(bottle,WaterKind.Raw,.2f);
        var coconut = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4f };
        npc.Inventory.Items.AddRange(new[]{bottle,coconut});
        Assert.That(VesselTransferMath.FillBottleFromCoconuts(npc,bottle),Is.Zero);
        Assert.That(bottle.ResourceAmount,Is.EqualTo(.2f));
        Assert.That(bottle.WaterKind,Is.EqualTo(WaterKind.Raw));
        Assert.That(coconut.ResourceAmount,Is.EqualTo(4f));
        BottleInventoryMath.SetContents(bottle,WaterKind.Coconut,9.2f);
        Assert.That(BottleInventoryMath.Add(bottle,WaterKind.Coconut,1),Is.Zero);
        Assert.That(VesselTransferMath.FillBottleFromCoconuts(npc,bottle),Is.Zero);
        Assert.That(bottle.ResourceAmount,Is.EqualTo(9.2f));
        Assert.That(coconut.ResourceAmount,Is.EqualTo(4f));
    }

    [TestCase(false, ContentIds.Bottle, ContentIds.Bottle, .42f)]
    [TestCase(true, ContentIds.Bottle, ContentIds.Bottle, .42f)]
    [TestCase(false, ContentIds.LeatherFlask, ContentIds.LeatherFlask, .42f)]
    [TestCase(true, ContentIds.LeatherFlask, ContentIds.LeatherFlask, .42f)]
    [TestCase(true, ContentIds.Bottle, ContentIds.LeatherFlask, 1f)]
    [TestCase(true, ContentIds.LeatherFlask, ContentIds.Bottle, 1f)]
    public void TakeVesselKeepsTheExactCollectorQuantity(bool pourIntoOwnBottle, string sourceId, string targetId, float fraction)
    {
        var engine = TestWorld.CreateEngine(63403);
        var world = engine.World;
        var npc = Girl(world);
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        if (pourIntoOwnBottle) npc.Inventory.Items.Add(new ItemInstance(targetId));
        PlaceAtOpenJunction(world,npc);
        var neighbor = SpatialQueries.GetPassableNeighbors(world,npc.CurrentJunction.Value).First();
        var tile = world.Junctions.Items[neighbor].Tiles[0];
        var collector = WorldObjectMutations.SpawnObject(world,ContentIds.WaterCollector,npc.Fragment,tile,neighbor);
        var vessel = WorldObjectMutations.SpawnObject(world,sourceId,npc.Fragment,tile,neighbor);
        vessel.ResourceAmount = fraction;
        vessel.WaterKind = WaterKind.Rain;
        vessel.LastAddedWaterKind = WaterKind.Coconut;
        vessel.Owner = npc.Id;
        var admission = ManualCommandExecutor.Apply(world,new InteractCommand(npc.Id,collector.Id,InteractionType.TakeVessel));
        Assert.That(admission.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),admission.Reason);
        var resultId = pourIntoOwnBottle ? targetId : sourceId;
        for (var i=0;i<1000 && !npc.Inventory.Items.Any(item => item.DefinitionId==resultId && item.ResourceAmount > 0f);i++) engine.Step();
        var result = npc.Inventory.Items.Single(item => item.DefinitionId==resultId);
        var initialAmount = fraction * WaterVesselCatalog.Capacity(sourceId);
        var expected = System.Math.Min(initialAmount, WaterVesselCatalog.Capacity(resultId));
        Assert.That(result.ResourceAmount,Is.EqualTo(expected).Within(1e-5));
        Assert.That(result.WaterKind,Is.EqualTo(WaterKind.Rain));
        Assert.That(result.LastAddedWaterKind,Is.EqualTo(WaterKind.Coconut));
        if(pourIntoOwnBottle) Assert.That(WaterCollectorMath.AmountIn(vessel),Is.EqualTo(initialAmount-expected).Within(1e-5));
        else Assert.That(world.Entities.Objects.ContainsKey(vessel.Id),Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CollectorChoreRecoversLooseEmptyBottleAndKeepsPersonalVessel(bool startsWithPersonalVessel)
    {
        var world = TestWorld.CreateWorld(63403);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var personal = new ItemInstance(ContentIds.LeatherFlask);
        if (startsWithPersonalVessel) npc.Inventory.Items.Add(personal);
        PlaceAtOpenJunction(world, npc);
        var anchor = npc.CurrentJunction.Value;
        var neighbor = SpatialQueries.GetPassableNeighbors(world, anchor).First();
        var collector = WorldObjectMutations.SpawnObject(world, ContentIds.WaterCollector,
            npc.Fragment, world.Junctions.Items[neighbor].Tiles[0], neighbor);
        var loose = WorldObjectMutations.SpawnObject(world, ContentIds.Bottle, npc.Fragment, npc.Tile, anchor);
        var planning = new PlanningSystem();
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        npc.Perception.Objects.Clear();
        foreach (var obj in new[] { collector, loose })
        {
            var perceived = new PerceivedObject
            {
                Id = obj.Id, DefinitionId = obj.DefinitionId, Tile = obj.Tile, IsReachable = true
            };
            perceived.AvailableInteractions.AddRange(world.Content.ObjectDefinitions[obj.DefinitionId]
                .Interactions.Select(interaction => interaction.Type));
            npc.Perception.Objects.Add(perceived);
        }

        Assert.That(DecisionSystem.FindStowableCollector(npc, world)?.Id, Is.EqualTo(collector.Id));
        npc.Mind.CurrentGoal = GoalType.StowBottle;
        planning.Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(loose.Id));
        for (var i = 0; i < 250 && world.Entities.Objects.ContainsKey(loose.Id); i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(loose.Id), Is.False);

        npc.Mind.CurrentGoal = GoalType.StowBottle;
        planning.Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(collector.Id));
        for (var i = 0; i < 250 && WaterCollectorMath.FindVessel(world, collector) == null; i++) engine.Step();
        Assert.That(WaterCollectorMath.FindVessel(world, collector)?.DefinitionId, Is.EqualTo(ContentIds.Bottle));
        Assert.That(npc.Inventory.Items.Any(item => ReferenceEquals(item, personal)), Is.EqualTo(startsWithPersonalVessel));
    }

    [Test]
    public void CorpseSpoilRemovesTheSelectedBottleByReference()
    {
        var world = TestWorld.CreateWorld(35515);
        var npc = Girl(world);
        var anchor = world.Junctions.Items.Values.First(junction => !junction.Blocked);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        var emptyFirst = new ItemInstance(ContentIds.Bottle);
        var selectedFilled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(selectedFilled, WaterKind.Rain, 6);
        remains.Contents.Add(emptyFirst);
        remains.Contents.Add(selectedFilled);

        Assert.That(CorpseMath.TakeSpoil(
            world, remains, selectedFilled, CorpseMath.SpoilSource.Bag), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(remains.Contents.Single(), Is.SameAs(emptyFirst));
            Assert.That(BottleInventoryMath.Charges(selectedFilled), Is.EqualTo(6));
            Assert.That(selectedFilled.WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(npc.Inventory.Items.Any(item =>
                ReferenceEquals(item, selectedFilled)), Is.False,
                "TakeSpoil only detaches; the caller owns the later destination move.");
        });
    }

    [Test]
    public void CurrentSavePreservesEveryBottleAndLegacyV67MigratesFirstBottle()
    {
        var world = TestWorld.CreateWorld(35503);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        var raw = new ItemInstance(ContentIds.Bottle);
        var boiled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 3);
        BottleInventoryMath.SetContents(boiled, WaterKind.Boiled, 7);
        npc.Inventory.Items.Add(raw);
        npc.Inventory.Items.Add(boiled);
        var anchor = world.Junctions.Items.Values.First(junction => !junction.Blocked);
        var ground = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        ground.WaterKind = WaterKind.Coconut;
        ground.ResourceAmount = 9f;

        var current = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        var currentBottles = Girl(current).Inventory.Items
            .Where(BottleInventoryMath.IsBottle).ToArray();
        var currentGround = current.Entities.Objects[ground.Id];
        Assert.Multiple(() =>
        {
            Assert.That(currentBottles, Has.Length.EqualTo(2));
            Assert.That(currentBottles[0].WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(currentBottles[0]), Is.EqualTo(3));
            Assert.That(currentBottles[1].WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(BottleInventoryMath.Charges(currentBottles[1]), Is.EqualTo(7));
            Assert.That(currentGround.WaterKind, Is.EqualTo(WaterKind.Coconut));
            Assert.That(currentGround.ResourceAmount, Is.EqualTo(9f));
        });

        npc.Inventory.Items.RemoveAt(1);
        var legacy = RoundTrip(world, 67);
        var migrated = BottleInventoryMath.FirstBottle(Girl(legacy));
        Assert.Multiple(() =>
        {
            Assert.That(migrated, Is.Not.Null);
            Assert.That(migrated.WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(migrated), Is.EqualTo(3));
        });
    }

    [Test]
    public void ExportAndWireKeepPerSlotAndGroundBottleWater()
    {
        var world = TestWorld.CreateWorld(35504);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var raw = new ItemInstance(ContentIds.Bottle);
        var rain = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 1);
        raw.LastAddedWaterKind = WaterKind.Coconut;
        BottleInventoryMath.SetContents(rain, WaterKind.Rain, 6);
        npc.Inventory.Items.Add(raw);
        npc.Inventory.Items.Add(rain);

        var anchor = world.Junctions.Items.Values.First(junction => !junction.Blocked);
        var ground = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        ground.WaterKind = WaterKind.Boiled;
        ground.LastAddedWaterKind = WaterKind.Rain;
        ground.ResourceAmount = 5f;

        var sent = WorldSnapshotExporter.Export(world);
        var received = RoundTrip(sent);
        var npcSnapshot = received.Npcs.Single(n => n.Id.Equals(npc.Id));
        var bottleSlots = npcSnapshot.InventoryContainers
            .SelectMany(c => c.Slots)
            .Where(s => s.ItemDefinitionId == ContentIds.Bottle)
            .OrderBy(s => s.SourceIndex).ToArray();
        var objectSnapshot = received.Objects.Single(o => o.Id.Equals(ground.Id));

        Assert.Multiple(() =>
        {
            Assert.That(bottleSlots, Has.Length.EqualTo(2));
            Assert.That(bottleSlots[0].WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(bottleSlots[0].LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
            Assert.That(bottleSlots[0].ResourceAmount, Is.EqualTo(1f));
            Assert.That(bottleSlots[1].WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(bottleSlots[1].ResourceAmount, Is.EqualTo(6f));
            Assert.That(objectSnapshot.WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(objectSnapshot.LastAddedWaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(objectSnapshot.ResourceAmount, Is.EqualTo(5f));
        });
    }

    [Test]
    public void LeatherFlaskSaveWireAndInventoryMetadataKeepItsOwnCapacityAndIdentity()
    {
        var world=TestWorld.CreateWorld(65011);var npc=Girl(world);
        npc.Inventory.Items.Clear();
        var flask=new ItemInstance(ContentIds.LeatherFlask) { Durability=.63f, Wetness=.2f };
        BottleInventoryMath.SetContents(flask,WaterKind.Raw,7.25f);flask.LastAddedWaterKind=WaterKind.Coconut;
        npc.Inventory.Items.Add(flask);
        var plastic=new ItemInstance(ContentIds.Bottle);BottleInventoryMath.SetContents(plastic,WaterKind.Rain,9);
        npc.Inventory.Items.Add(plastic);
        var loaded=RoundTrip(world,WorldSaveSerializer.BlobVersion);var saved=loaded.Entities.Npcs[npc.Id];
        Assert.That(saved.Inventory.Items.Select(i=>i.DefinitionId),Is.EqualTo(new[]{ContentIds.LeatherFlask,ContentIds.Bottle}));
        var restored=saved.Inventory.Items[0];Assert.That(restored.ResourceAmount,Is.EqualTo(7.25f));
        Assert.That(restored.WaterKind,Is.EqualTo(WaterKind.Raw));Assert.That(restored.LastAddedWaterKind,Is.EqualTo(WaterKind.Coconut));
        Assert.That(restored.Durability,Is.EqualTo(.63f));
        var snapshot=RoundTrip(WorldSnapshotExporter.Export(loaded));var actor=snapshot.Npcs.Single(n=>n.Id==npc.Id);
        var encoded=actor.InventoryWater.Single(row=>row.Split('\t')[1]==ContentIds.LeatherFlask).Split('\t');
        Assert.That(encoded[3],Is.EqualTo("8"));
        Assert.That(ManualMenuTargets.CanDrinkFromPack(actor,_=>false),Is.True);
        Assert.That(BottleVisualMath.Fill(restored),Is.EqualTo(7.25f/8).Within(1e-5));
    }

    [Test]
    public void ParkedLeatherFlaskKeepsItsIdAndRainCollectionConservesPhysicalRate()
    {
        var engine=TestWorld.CreateEngine(65012);var world=engine.World;var npc=Girl(world);
        npc.Mind.ManualControl=true;npc.Needs.Hunger=npc.Needs.Thirst=0;
        npc.Inventory.Items.Clear();npc.Inventory.Items.Add(new ItemInstance(ContentIds.LeatherFlask){Durability=.42f});
        PlaceAtOpenJunction(world,npc);
        var anchor=SpatialQueries.GetPassableNeighbors(world,npc.CurrentJunction.Value).First();
        var tile=world.Junctions.Items[anchor].Tiles[0];
        var collector=WorldObjectMutations.SpawnObject(world,ContentIds.WaterCollector,npc.Fragment,tile,anchor);
        var admission=ManualCommandExecutor.Apply(world,new InteractCommand(npc.Id,collector.Id,InteractionType.PlaceVessel));
        Assert.That(admission.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),admission.Reason);
        for(int i=0;i<300&&WaterCollectorMath.FindVessel(world,collector)==null;i++)engine.Step();
        var flask=WaterCollectorMath.FindVessel(world,collector);Assert.That(flask,Is.Not.Null);
        Assert.That(flask.DefinitionId,Is.EqualTo(ContentIds.LeatherFlask));
        Assert.That(flask.Durability,Is.EqualTo(.42f));Assert.That(npc.Inventory.Items.Any(i=>i.DefinitionId==ContentIds.LeatherFlask),Is.False);
        var other=world.Tiles.Items[tile].Junctions.First(j=>j!=anchor&&SpatialQueries.IsJunctionFree(world,j));
        var control=WorldObjectMutations.SpawnObject(world,ContentIds.WaterCollector,npc.Fragment,tile,other);
        var bottle=WorldObjectMutations.SpawnObject(world,ContentIds.Bottle,npc.Fragment,tile,other);
        bottle.ResourceAmount=0;bottle.WaterKind=WaterKind.None;
        var before=WaterCollectorMath.AmountIn(flask);world.Environment.IsRaining=true;world.Tick+=world.SlowIntervalTicks;
        new WaterCollectorSystem().Run(world);
        var delta=WaterCollectorMath.AmountIn(flask)-before;
        Assert.That(delta,Is.GreaterThan(0));
        Assert.That(delta,Is.EqualTo(WaterCollectorMath.AmountIn(bottle)).Within(1e-5),"same rain catches same physical quantity");
        Assert.That(flask.ResourceAmount,Is.GreaterThan(bottle.ResourceAmount),"smaller flask fills a greater fraction");
    }

    private static WorldState RoundTrip(WorldState source, int version)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WorldSaveSerializer.WriteAtVersion(source, writer, version);
        }
        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(source.Seed);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }

    private static WorldSnapshot RoundTrip(WorldSnapshot source)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WorldSnapshotCodec.Write(source, writer, includeDebugDetails: false);
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        var result = new WorldSnapshot();
        WorldSnapshotCodec.Read(reader, result);
        return result;
    }

    private static void PlaceAtOpenJunction(WorldState world, NPCState npc)
    {
        var destination = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && SpatialQueries.IsJunctionFree(world, junction.Id) &&
            junction.Neighbors.Any(neighbor =>
                SpatialQueries.IsJunctionFree(world, neighbor)));

        if (npc.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, npc.Id);
        }

        var oldTile = npc.Tile;
        npc.CurrentJunction = destination.Id;
        npc.Position = destination.WorldPosition;
        npc.Tile = destination.Tiles[0];
        npc.Fragment = destination.Fragment;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, destination.Id, npc.Id);
    }
}

}
