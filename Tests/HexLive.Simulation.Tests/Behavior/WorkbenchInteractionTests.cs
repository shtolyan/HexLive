using System.Linq;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class WorkbenchInteractionTests
{
    [TestCase(ContentIds.WoodenArm)]
    [TestCase(ContentIds.WoodenLeg)]
    public void FinishedTabletopOutput_IsReachableAndManuallyTakeable(string output)
    {
        var (engine, npc, station, project) = Fixture(output);
        var world = engine.World;
        Assert.That(InteractionReach.CheckObjectStart(world, npc, project,
            world.Content.ObjectDefinitions[output].ObstacleRadius), Is.True,
            "The table's authored working point must reach its own finished output.");
        var admission = ManualCommandExecutor.Apply(world,
            new InteractCommand(npc.Id, project.Id, InteractionType.PickUp));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        for (var i = 0; i < 400 && world.Entities.Objects.ContainsKey(project.Id); i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.False);
        Assert.That(npc.Inventory.Items.Count(item => item.DefinitionId == output), Is.EqualTo(1));
        var repeated = ManualCommandExecutor.Apply(world,
            new InteractCommand(npc.Id, project.Id, InteractionType.PickUp));
        Assert.That(repeated.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(npc.Inventory.Items.Count(item => item.DefinitionId == output), Is.EqualTo(1));
    }

    [Test]
    public void UnfinishedOutput_CannotBeTaken()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        project.CraftWorkDone = 400;
        var result = ManualCommandExecutor.Apply(engine.World,
            new InteractCommand(npc.Id, project.Id, InteractionType.PickUp));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(engine.World.Entities.Objects.ContainsKey(project.Id), Is.True);
        Assert.That(npc.Inventory.Items, Is.Empty);
    }

    [Test]
    public void FinalVisualFrame_CannotInterruptItsOwnFinishingWorker()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        project.CraftWorkDone = project.CraftWorkRequired - Spec119.CraftCycleWork;
        var outputsBefore = world.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.WoodenLeg);
        Assert.That(CraftProjectMath.TryBeginCycle(world, npc, GoalType.CraftWoodenLeg,
            station, out var started), Is.True);
        Assert.That(started.Id, Is.EqualTo(project.Id));
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.StartTick = world.Tick;
        npc.Execution.EndTick = world.Tick + Spec119.CraftCycleWork;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.Goal = GoalType.CraftWoodenLeg;
        world.Tick = npc.Execution.EndTick;
        CraftProjectMath.UpdateCycleProgress(world, npc);
        Assert.That(project.CraftWorkDone, Is.EqualTo(project.CraftWorkRequired));
        var result = ManualCommandExecutor.Apply(engine.World,
            new InteractCommand(npc.Id, project.Id, InteractionType.PickUp));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(project.IsOccupied, Is.True);
        Assert.That(project.CurrentUser, Is.EqualTo(npc.Id));
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
        Assert.That(npc.Execution.CraftProjectId, Is.EqualTo(project.Id));
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Assert.That(engine.World.Entities.Objects.ContainsKey(project.Id), Is.True);
        Assert.That(npc.Inventory.Items, Is.Empty);
        Assert.That(CraftProjectMath.CompleteCycle(world, npc, GoalType.CraftWoodenLeg), Is.True);
        Assert.That(world.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.WoodenLeg), Is.EqualTo(outputsBefore));
        Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.True);
        Assert.That(project.IsOccupied, Is.False);
        Assert.That(project.Contents, Is.Empty);
    }

    [Test]
    public void FullPack_RetainsOutput_ThenCanTakeAfterSpaceIsFreed()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        EquipmentMath.RecalculateCapacity(world, npc);
        for (var i = 0; i < npc.Inventory.Capacity; i++)
        {
            var id = "test.workbench_filler." + i;
            world.Content.ObjectDefinitions[id] = new ObjectDefinition { Id = id, DisplayName = id };
            npc.Inventory.Items.Add(new ItemInstance(id));
        }
        ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, project.Id, InteractionType.PickUp));
        for (var i = 0; i < 120; i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.True);
        Assert.That(npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.WoodenLeg), Is.False);
        npc.Inventory.Items.Clear();
        ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, project.Id, InteractionType.PickUp));
        for (var i = 0; i < 400 && world.Entities.Objects.ContainsKey(project.Id); i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.False);
        Assert.That(npc.Inventory.Items.Count(item => item.DefinitionId == ContentIds.WoodenLeg), Is.EqualTo(1));
    }

    [Test]
    public void UnrelatedOrMovedSupport_DoesNotGrantReachThroughFurniture()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        project.CraftStationObjectId = null;
        Assert.That(InteractionReach.CheckObjectStart(world, npc, project, 0f), Is.False);
        project.CraftStationObjectId = station.Id;
        station.Junctions[0] = station.CraftJunction.Value;
        Assert.That(CraftProjectMath.TryGetSupport(world, project, out _), Is.False);
        Assert.That(InteractionReach.CheckObjectStart(world, npc, project, 0f), Is.False);
    }

    [Test]
    public void BlockedWorkPoint_DoesNotGrantSupportedPickup()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        WorldObjectMutations.SpawnObject(world, ContentIds.Workbench,
            npc.Fragment, npc.Tile, station.CraftJunction.Value);
        Assert.That(InteractionReach.CheckObjectStart(world, npc, project, 0f), Is.False);
    }

    [Test]
    public void ResumePinsExactProject_AndNeverPaysANewBillWhenItDisappears()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        project.CraftWorkDone = 400;
        var alternate = WorldObjectMutations.SpawnObject(world, project.DefinitionId,
            project.Fragment, project.Tile, project.Junctions[0]);
        alternate.CraftWorkRequired = project.CraftWorkRequired;
        alternate.CraftStationObjectId = station.Id;
        var result = ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, alternate.Id,
            InteractionType.Craft, CraftProjectMath.ResumeInteractionId));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), result.Reason);
        Assert.That(npc.Plan.CraftProjectTargetId, Is.EqualTo(alternate.Id));
        Assert.That(CraftProjectMath.TryBeginCycle(world, npc, GoalType.CraftWoodenLeg,
            station, out var resumed), Is.True);
        Assert.That(resumed.Id, Is.EqualTo(alternate.Id));
        CraftProjectMath.ReleaseWorker(world, npc);
        WorldObjectMutations.DespawnObject(world, alternate.Id);
        for (var i = 0; i < 3; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Board));
        for (var i = 0; i < 2; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Rope));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Hide));
        Assert.That(CraftProjectMath.TryBeginCycle(world, npc, GoalType.CraftWoodenLeg,
            station, out _), Is.False);
        Assert.That(npc.Inventory.Items.Count, Is.EqualTo(6));
        Assert.That(project.CraftWorkDone, Is.EqualTo(400));
    }

    [Test]
    public void PausedProject_RemembersWorkerAndExactResumeAcrossSave()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        project.CraftWorkDone = 400;
        ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, project.Id,
            InteractionType.Craft, CraftProjectMath.ResumeInteractionId));
        Assert.That(CraftProjectMath.TryBeginCycle(world, npc, GoalType.CraftWoodenLeg,
            station, out _), Is.True);
        CraftProjectMath.ReleaseWorker(world, npc);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(bytes)) WorldSaveSerializer.Read(restored, reader);
        var saved = restored.Entities.Objects[project.Id];
        Assert.That(saved.CraftLastWorkerId, Is.EqualTo(npc.Id));
        Assert.That(saved.CurrentUser, Is.Null);
        Assert.That(saved.CraftWorkDone, Is.EqualTo(400));
        Assert.That(restored.Entities.Npcs[npc.Id].Plan.CraftProjectTargetId, Is.EqualTo(project.Id));
    }

    [Test]
    public void OlderSaveReadsProjectWithoutInventingLastWorker()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        project.CraftWorkDone = 400;
        project.CraftLastWorkerId = npc.Id;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(engine.World, writer, 79);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(engine.World.Seed);
        using (var reader = new BinaryReader(bytes)) WorldSaveSerializer.Read(restored, reader);
        Assert.That(restored.Entities.Objects[project.Id].CraftLastWorkerId, Is.Null);
        Assert.That(restored.Entities.Objects[project.Id].CraftWorkDone, Is.EqualTo(400));
    }

    [Test]
    public void SnapshotDeltas_PreserveProgressAndCurrentThenLastWorker()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        project.CraftWorkDone = 400;
        Assert.That(CraftProjectMath.TryBeginCycle(world, npc, GoalType.CraftWoodenLeg,
            station, out _), Is.True);
        var encoder = new SnapshotDeltaEncoder();
        var mirror = new WorldSnapshot();
        using (var r = new BinaryReader(new MemoryStream(encoder.Encode(WorldSnapshotExporter.Export(world), false))))
            SnapshotDeltaReader.Apply(r, mirror, -1);
        Assert.That(mirror.Objects.Single(o => o.Id == project.Id).CraftCurrentWorkerId, Is.EqualTo(npc.Id.Value));
        var previousTick = world.Tick;
        world.Tick++;
        CraftProjectMath.ReleaseWorker(world, npc);
        project.CraftWorkDone = 424;
        using (var r = new BinaryReader(new MemoryStream(encoder.Encode(WorldSnapshotExporter.Export(world), false))))
            SnapshotDeltaReader.Apply(r, mirror, previousTick);
        var paused = mirror.Objects.Single(o => o.Id == project.Id);
        Assert.That(paused.CraftCurrentWorkerId, Is.Null);
        Assert.That(paused.CraftLastWorkerId, Is.EqualTo(npc.Id.Value));
        Assert.That(paused.CraftWorkDone, Is.EqualTo(424));
        Assert.That(paused.CraftActive, Is.False);
    }

    [Test]
    public void FinishedOutput_OccupiesTableUntilTaken_AndDropsWhenSupportIsDestroyed()
    {
        var (engine, npc, station, project) = Fixture(ContentIds.WoodenLeg);
        var world = engine.World;
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Board));
        Assert.That(CraftProjectMath.CanBeginCycle(world, npc, GoalType.CraftWoodenLeg, station), Is.False);
        WorldObjectMutations.DespawnObject(world, station.Id);
        CraftProjectMath.CancelOrphanedProjects(world);
        Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.True);
        Assert.That(project.CraftStationObjectId, Is.Null);
        Assert.That(project.IsCraftProject, Is.False);
    }

    private static (SimulationEngine Engine, NPCState Npc, WorldObjectState Station, WorldObjectState Project)
        Fixture(string output)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var anchor = npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
        foreach (var obj in world.Entities.Objects.Values.Where(o => o.Tile.Equals(npc.Tile)).ToArray())
            WorldObjectMutations.DespawnObject(world, obj.Id);
        var station = WorldObjectMutations.SpawnObject(world, ContentIds.Workbench,
            npc.Fragment, npc.Tile, anchor);
        station.RotationDegrees = StructurePlacement.QuantizeHexYaw(0);
        station.CraftJunction = StructurePlacement.WorkbenchJunction(world, station.Tile,
            anchor, station.RotationDegrees);
        Assert.That(station.CraftJunction.HasValue, Is.True);
        npc.CurrentJunction = station.CraftJunction.Value;
        npc.Position = world.Junctions.Items[npc.CurrentJunction.Value].WorldPosition;
        npc.Inventory.Items.Clear();
        npc.Needs.Hunger = npc.Needs.Thirst = 0;
        foreach (var other in world.Entities.Npcs.Values) other.Mind.ManualControl = true;
        var project = WorldObjectMutations.SpawnObject(world, output, npc.Fragment,
            station.Tile, anchor);
        project.CraftWorkRequired = project.CraftWorkDone = 12000;
        project.CraftStationObjectId = station.Id;

        return (engine, npc, station, project);
    }
}
