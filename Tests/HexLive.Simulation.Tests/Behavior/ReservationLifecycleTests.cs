using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class ReservationLifecycleTests
{
    private static WorldObjectState Spawn(WorldState world, string definition)
    {
        var npc = world.Entities.Npcs.Values.First();
        var anchor = world.Tiles.Items[npc.Tile].Junctions[0];
        return WorldObjectMutations.SpawnObject(world, definition, npc.Fragment, npc.Tile, anchor);
    }

    private static void Plan(NPCState npc, ObjectId target)
    {
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.TargetObjectId = target;
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Interact, TargetObject = target });
    }

    [TestCase(ContentIds.BedBasic)]
    [TestCase(ContentIds.CoconutPierced)]
    public void OrphanedClaimClearsWithoutRemovingPersistentOwnership(string definition)
    {
        var world = TestWorld.CreateWorld();
        var obj = Spawn(world, definition);
        var owner = world.Entities.Npcs.Values.First();
        obj.Owner = owner.Id;
        obj.CurrentUser = owner.Id;
        obj.IsOccupied = true;
        owner.Plan.Status = PlanStatus.Failed;
        owner.Execution.Status = ExecutionStatus.Completed;
        new ObjectReservationSystem().Run(world);
        Assert.That(obj.IsOccupied, Is.False);
        Assert.That(obj.CurrentUser, Is.Null);
        Assert.That(obj.Owner, Is.EqualTo(owner.Id));
    }

    [Test]
    public void StaleInProgressExecutionCannotKeepFailedCoconutPlan()
    {
        var world = TestWorld.CreateWorld();
        var obj = Spawn(world, ContentIds.CoconutPierced);
        var npc = world.Entities.Npcs.Values.First();
        obj.CurrentUser = npc.Id;
        obj.IsOccupied = true;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.TargetObject = obj.Id;
        npc.Execution.CurrentInteraction = InteractionType.Drink;
        npc.Plan.Status = PlanStatus.Failed;
        new ObjectReservationSystem().Run(world);
        Assert.That(obj.CurrentUser, Is.Null);
    }

    [Test]
    public void LongApproachAndCraftInputsKeepClaimsUntilPlanAborts()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var source = Spawn(world, ContentIds.Coconut);
        var piece = Spawn(world, ContentIds.Fiber);
        var project = Spawn(world, ContentIds.Rope);
        Plan(npc, source.Id);
        foreach (var obj in new[] { source, piece, project })
        {
            obj.CurrentUser = npc.Id;
            obj.IsOccupied = true;
        }
        world.Tick += 100000; // no arbitrary lease expiry during a live approach
        new ObjectReservationSystem().Run(world);
        Assert.That(source.IsOccupied, Is.True);

        piece.CurrentUser = npc.Id;
        project.CurrentUser = npc.Id;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CraftLayout.Add(piece.Id);
        npc.Execution.CraftProjectId = project.Id;
        new ObjectReservationSystem().Run(world);
        Assert.That(piece.IsOccupied && project.IsOccupied, Is.True);

        npc.Execution.Status = ExecutionStatus.Failed;
        Assert.That(PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "test"), Is.True);
        Assert.That(new[] { source, piece, project }.All(obj => !obj.IsOccupied && obj.CurrentUser == null), Is.True);
    }

    [Test]
    public void RescueReservesBedBeforePickupAndReleasesWhenRescueAbandoned()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Take(2).ToArray();
        var carrier = pair[0];
        var patient = pair[1];
        var bed = Spawn(world, ContentIds.BedBasic);
        Plan(carrier, bed.Id);
        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.RescueDestinationObjectId = bed.Id;
        bed.IsOccupied = true;
        bed.CurrentUser = patient.Id;
        new ObjectReservationSystem().Run(world);
        Assert.That(bed.CurrentUser, Is.EqualTo(patient.Id));
        carrier.Plan.Status = PlanStatus.Invalid;
        new ObjectReservationSystem().Run(world);
        Assert.That(bed.CurrentUser, Is.Null);
        Plan(carrier, bed.Id);
        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.TargetAgentId = patient.Id;
        bed.CurrentUser = patient.Id;
        bed.IsOccupied = true;
        PlanInterruption.TryAbort(world, carrier, InterruptionCause.PlayerCommand, "test rescue abort");
        Assert.That(bed.CurrentUser, Is.Null, "Abort releases patient-owned destination immediately");
    }

    [Test]
    public void EmergencySleepKeepsBedWithoutPlanButCarriedSleeperDoesNot()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var bed = Spawn(world, ContentIds.BedBasic);
        npc.Plan.Status = PlanStatus.None;
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Sleep;
        npc.Execution.TargetObject = bed.Id;
        bed.CurrentUser = npc.Id;
        bed.IsOccupied = true;
        new ObjectReservationSystem().Run(world);
        Assert.That(bed.IsOccupied, Is.True);
        npc.CarriedByNpcId = new EntityId(987654);
        new ObjectReservationSystem().Run(world);
        Assert.That(bed.CurrentUser, Is.Null);
    }

    [Test]
    public void DeadBodyProvenanceSurvivesOrphanCleanupAndInterruptedMourning()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var corpse = Spawn(world, ContentIds.CorpseNpc);
        var deceased = new EntityId(987654);
        corpse.CurrentUser = deceased;
        corpse.IsOccupied = true;
        new ObjectReservationSystem().Run(world);
        Assert.That(corpse.CurrentUser, Is.EqualTo(deceased));
        Assert.That(corpse.IsOccupied, Is.False);
        Plan(npc, corpse.Id);
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.TargetObject = corpse.Id;
        corpse.IsOccupied = true;
        new ObjectReservationSystem().Run(world);
        Assert.That(corpse.IsOccupied, Is.True);
        PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "test");
        Assert.That(corpse.IsOccupied, Is.False);
        Assert.That(corpse.CurrentUser, Is.EqualTo(deceased));
    }

    [Test]
    public void LoadedOrphansAreIndexedAndDespawnedReferencesCannotReenterIndex()
    {
        var world = TestWorld.CreateWorld();
        var old = Spawn(world, ContentIds.CoconutPierced);
        old.CurrentUser = new EntityId(987654); // even the half-written pair is repaired
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        stream.Position = 0;
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(world, reader);
        var loaded = world.Entities.Objects[old.Id];
        new ObjectReservationSystem().Run(world);
        Assert.That(loaded.CurrentUser, Is.Null);
        old.IsOccupied = true;
        Assert.That(world.Entities.ObjectReservations.Contains(old), Is.False);
        WorldObjectMutations.DespawnObject(world, loaded.Id);
        loaded.CurrentUser = new EntityId(123456);
        Assert.That(world.Entities.ObjectReservations.Contains(loaded), Is.False);
    }

    [Test]
    public void ThousandsOfFreeObjectsDoNotEnterWatchdogWorksetAndEitherSetterOrderWorks()
    {
        var world = TestWorld.CreateWorld();
        var baseline = world.Entities.ObjectReservations.Count;
        for (var i = 0; i < 2000; i++) Spawn(world, ContentIds.Coconut);
        Assert.That(world.Entities.ObjectReservations.Count, Is.EqualTo(baseline));
        var obj = Spawn(world, ContentIds.CoconutPierced);
        obj.IsOccupied = true;
        obj.CurrentUser = new EntityId(987654);
        new ObjectReservationSystem().Run(world);
        Assert.That(obj.CurrentUser, Is.Null);
        obj.CurrentUser = new EntityId(987654);
        obj.IsOccupied = false;
        new ObjectReservationSystem().Run(world);
        Assert.That(obj.CurrentUser, Is.Null);
    }
}

}
