using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
// No direct Execution/Plan/claim writes:
// admission, walking, interaction and cancellation go through canonical APIs.
public sealed class HouseDemolitionUndoInFlightTests
{
    [Test]
    public void UndoAfterTeardownHasStartedPreservesPaidWallAndRetiresTheOldClaims()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        foreach (var person in world.Entities.Npcs.Values.Where(person => PlayerAuthority.IsPermanentlyOwned(world, person)))
            Assert.That(engine.ApplyManualCommand(new SetManualControlCommand(person.Id, true)).Accepted, Is.True);
        engine.Step();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony && PlayerAuthority.IsPermanentlyOwned(world, n));
        npc.Needs.Hunger = 0f; npc.Needs.Thirst = 0f; npc.Needs.Stamina = 1f;
        npc.Inventory.Items.Clear(); npc.Inventory.Items.Add(new ItemInstance("tool.axe_stone"));
        var hut = TestWorld.StartHut(world);
        var wall = BuildingRules.ArchitectureObjects(world, hut).First(o => o.DefinitionId == "architecture.wall.wood");
        var id = wall.Id; var savedState = wall.ArchitectureElements[0].Clone();
        var blocked = wall.BlockedJunctions.ToArray();
        Assert.That(blocked, Is.Not.Empty);
        Assert.That(BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var original, out _), Is.True);
        var changed = original.Clone();
        changed.Elements.RemoveAll(e => BlueprintBuildingPlan.SlotKey(e) == savedState.SlotKey);
        var removal = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(changed, false)));
        Assert.That(removal.Accepted, Is.True, removal.Reason);
        Assert.That(FreeArchitectureRules.IsDemolitionSite(wall), Is.True);
        var start = engine.ApplyManualCommand(new InteractCommand(npc.Id, id, InteractionType.Build));
        Assert.That(start.Accepted, Is.True, start.Reason);
        for (var tick = 0; tick < 1200 && npc.Execution.Status != ExecutionStatus.InProgress; tick++) engine.Step();
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress), "Must reach actual interaction, not just enqueue the order.");
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Build));
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(id));
        Assert.That(wall.IsOccupied, Is.True); Assert.That(wall.CurrentUser, Is.EqualTo(npc.Id));
        Assert.That(npc.Execution.EndTick, Is.GreaterThan(world.Tick));
        var midpoint = npc.Execution.StartTick + (npc.Execution.EndTick - npc.Execution.StartTick) / 2;
        while (world.Tick < midpoint) engine.Step();
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress), "Undo after real work has progressed, before commit.");
        var oldEndTick = npc.Execution.EndTick;
        var groundBefore = world.Entities.Objects.Values.Where(o => BuildSiteMath.AllMaterials.Contains(o.DefinitionId) && o.Junctions.Any(wall.Junctions.Contains)).Select(o => o.Id).ToHashSet();
        var undo = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(original, false)));
        Assert.That(undo.Accepted, Is.True, undo.Reason);
        Assert.That(FreeArchitectureRules.IsDemolitionSite(wall), Is.False);
        while (world.Tick <= oldEndTick + 12) engine.Step();
        Assert.That(world.Entities.Objects[id], Is.SameAs(wall));
        var state = wall.ArchitectureElements[0];
        Assert.That(state.DeliveredSticks, Is.EqualTo(savedState.DeliveredSticks));
        Assert.That(state.DeliveredBoards, Is.EqualTo(savedState.DeliveredBoards));
        Assert.That(state.DeliveredRope, Is.EqualTo(savedState.DeliveredRope));
        Assert.That(state.DeliveredLeaves, Is.EqualTo(savedState.DeliveredLeaves));
        Assert.That(state.WorkDone, Is.EqualTo(savedState.WorkDone));
        Assert.That(wall.BlockedJunctions, Is.EquivalentTo(blocked));
        Assert.That(blocked.All(j => world.Junctions.Items[j].Blocked), Is.True);
        Assert.That(world.Entities.Objects.Values.Where(o => BuildSiteMath.AllMaterials.Contains(o.DefinitionId) && o.Junctions.Any(wall.Junctions.Contains)).Select(o => o.Id), Is.EquivalentTo(groundBefore), "Cancelled teardown must not issue salvage.");
        Assert.That(wall.IsOccupied, Is.False); Assert.That(wall.CurrentUser, Is.Null);
        Assert.That(world.Reservations.Junctions.Values.Any(r => r.Owner == npc.Id), Is.False, "The old teardown approach claim must retire.");
        Assert.That(npc.Execution.Status, Is.Not.EqualTo(ExecutionStatus.InProgress));
        Assert.That(npc.Plan.TargetObjectId == id && npc.Plan.Status == PlanStatus.Active, Is.False);
    }
}
