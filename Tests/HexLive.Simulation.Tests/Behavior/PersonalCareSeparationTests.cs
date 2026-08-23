using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class PersonalCareSeparationTests
{
    private static WorldState SettledWorld()
    {
        var engine = TestWorld.CreateEngine(1104);
        for (var i = 0; i < 4; i++) engine.Step();
        return engine.World;
    }

    [Test]
    public void BathPlanNeverTurnsDirtyWornClothesIntoLaundry()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        npc.WornItems.Clear();
        var garment = new ItemInstance("underwear.bra_riot") { Dirtiness = 0.8f };
        npc.WornItems.Add(garment);
        npc.Needs.Hygiene = 0f;
        npc.Mind.CurrentGoal = GoalType.Bathe;
        npc.Plan.Status = PlanStatus.None;

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.PersonalCarePhase, Is.EqualTo(PersonalCarePhase.Bathing));
            Assert.That(npc.Plan.Steps.Any(step => step.Type == PlanStepType.WashClothes), Is.False);
            Assert.That(garment.Dirtiness, Is.EqualTo(0.8f));
        });
    }

    [Test]
    public void LaundryPrioritizesWornThenInventoryAndUsesTenPercentThreshold()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        var existingGarment = npc.WornItems.First(item =>
            world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) &&
            def.Layer is not null);
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        var worn = existingGarment;
        worn.Dirtiness = 0.10f;
        var carried = new ItemInstance(worn.DefinitionId) { Dirtiness = 0.9f };
        npc.WornItems.Add(worn);
        npc.Inventory.Items.Add(carried);
        npc.Mind.CurrentGoal = GoalType.WashClothes;
        npc.Plan.Status = PlanStatus.None;

        Assert.Multiple(() =>
        {
            Assert.That(SimBalance.WashClothesNeedThreshold, Is.EqualTo(0.10f));
            Assert.That(worn.Dirtiness, Is.GreaterThanOrEqualTo(SimBalance.WashClothesNeedThreshold));
            Assert.That(world.Content.ObjectDefinitions[worn.DefinitionId].Layer, Is.Not.Null);
        });

        new PlanningSystem().Run(world);
        var wash = npc.Plan.Steps.Single(step => step.Type == PlanStepType.WashClothes);
        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.TargetItemDefinitionId, Is.EqualTo(worn.DefinitionId));
            Assert.That(wash.LaundryFromInventory, Is.False);
        });

        worn.Dirtiness = 0.099f;
        npc.Plan.Steps.Clear();
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);
        wash = npc.Plan.Steps.Single(step => step.Type == PlanStepType.WashClothes);
        Assert.Multiple(() =>
        {
            Assert.That(npc.Plan.TargetItemDefinitionId, Is.EqualTo(carried.DefinitionId));
            Assert.That(wash.LaundryFromInventory, Is.True);
        });
    }

    [Test]
    public void LaundryBatchReturnsEveryPieceToItsOrigin()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(candidate =>
            candidate.Faction == Faction.Colony &&
            HygieneMath.FindReachableBathShore(world, candidate) is not null);
        var worn = npc.WornItems.First(item =>
            world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var def) &&
            def.Layer is not null);
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        worn.Dirtiness = 0.7f;
        var carried = new ItemInstance(worn.DefinitionId) { Bloodiness = 0.8f };
        npc.WornItems.Add(worn);
        npc.Inventory.Items.Add(carried);
        npc.Mind.CurrentGoal = GoalType.WashClothes;
        npc.Plan.Status = PlanStatus.None;

        new PlanningSystem().Run(world);
        var washIndex = npc.Plan.Steps.FindIndex(step => step.Type == PlanStepType.WashClothes);
        var wash = npc.Plan.Steps[washIndex];
        var shore = world.Junctions.Items[wash.TargetJunction!.Value];
        npc.CurrentJunction = shore.Id;
        npc.Tile = HygieneMath.DryStandTile(world, shore)!.Value;
        npc.Position = shore.WorldPosition;
        npc.Plan.CurrentStepIndex = washIndex;
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);

        var execution = new ExecutionSystem();
        execution.Run(world); // worn: begin doff
        world.Tick = npc.Execution.EndTick;
        execution.Run(world); // worn: begin wash
        world.Tick = npc.Execution.EndTick;
        execution.Run(world); // worn: restore, select inventory
        execution.Run(world); // inventory: begin wash immediately
        world.Tick = npc.Execution.EndTick;
        execution.Run(world); // inventory: restore and finish batch

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(worn));
            Assert.That(npc.Inventory.Items.Single(), Is.SameAs(carried));
            Assert.That(worn.Dirtiness, Is.Zero);
            Assert.That(carried.Bloodiness, Is.Zero);
            Assert.That(npc.Execution.HeldGarment, Is.Null);
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
        });
    }
}

}
