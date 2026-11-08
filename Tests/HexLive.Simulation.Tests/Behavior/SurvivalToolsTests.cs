using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class SurvivalToolsTests
{
    [Test]
    public void BuilderWithoutAReachableHammerBidsAndCompletesItsPhysicalCraft()
    {
        var engine = TestWorld.CreateEngine(); engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Mind.ManualControl = false;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Needs.Hunger = npc.Needs.Thirst = 0;
        npc.Needs.Energy = npc.Needs.Stamina = npc.Needs.Social = 1;
        npc.Inventory.Items.Clear();
        foreach (var id in new[] {ContentIds.Stick,ContentIds.Stone,ContentIds.Rope}) npc.Inventory.Items.Add(new ItemInstance(id));
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite, npc.Fragment, npc.Tile, npc.CurrentJunction.Value);
        site.BuildProduct = ContentIds.BedBasic; site.Owner = npc.Id;
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject { Id=site.Id,DefinitionId=site.DefinitionId,Tile=site.Tile,IsReachable=true });
        new DecisionSystem().Run(world);
        Assert.That(npc.Mind.LastScores.Single(s => s.Goal == GoalType.CraftHammer).FinalScore, Is.GreaterThan(0));
        npc.Mind.CurrentGoal = GoalType.CraftHammer;
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.Steps.Last().Type, Is.EqualTo(PlanStepType.CraftInPlace));
        var execution = new ExecutionSystem();
        for(var i=0;i<300 && !npc.Inventory.Items.Any(x=>x.DefinitionId==GearCatalog.Hammer);i++)
        { world.Tick++; execution.Run(world); }
        Assert.That(npc.Inventory.Items.Any(x=>x.DefinitionId==GearCatalog.Hammer) ||
            world.Entities.Objects.Values.Any(x=>x.DefinitionId==GearCatalog.Hammer && !x.IsCraftProject), Is.True);
    }

    [Test]
    public void SawTiersApplyToTheSameWorkWithoutGrantingChopping()
    {
        GearCatalog.ResetToDefaults();
        Assert.That(GearCatalog.For(GearCatalog.Saw).HarvestSpeedMult, Is.EqualTo(3f));
        Assert.That(GearCatalog.For(ContentIds.CrudeSaw).HarvestSpeedMult, Is.EqualTo(1.5f));
        Assert.That(GearCatalog.For(ContentIds.CrudeSaw).Capabilities, Is.EqualTo(GearCapability.Saw));
        Assert.That(RecipeCatalog.ByOutput.ContainsKey(GearCatalog.Saw), Is.False);
        Assert.That(RecipeCatalog.ByOutput.ContainsKey(ContentIds.CrudeSaw), Is.True);
    }

    [TestCase(GearCatalog.Lighter)]
    [TestCase(ContentIds.Firestarter)]
    public void LastSuccessfulIgnitionUsesUpTheSelectedPhysicalTool(string id)
    {
        GearCatalog.ResetToDefaults();
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var tool = new ItemInstance(id) { Durability = PortableFireMath.IgnitionWear };
        var spare = new ItemInstance(id);
        npc.Inventory.Items.Add(tool);
        npc.Inventory.Items.Add(spare);
        npc.Execution.ActionItemsBound = true;
        npc.Execution.ActionTool = tool;
        PortableFireMath.WearIgniter(world, npc);
        Assert.That(npc.Inventory.Items.Any(i => ReferenceEquals(i, tool)), Is.False);
        Assert.That(spare.Durability, Is.EqualTo(1f));
    }

    [Test]
    public void CarriedTorchGivesWayToWorkAndActuallyBurnsOut()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Execution.CurrentInteraction = null;
        var torch = new ItemInstance(ContentIds.Torch) { Durability = 0.00001f };
        npc.Inventory.Items.Add(torch);
        Assert.That(ActionItemResolver.HeldId(world, npc), Is.EqualTo(ContentIds.Torch));
        new FireSystem().Run(world);
        Assert.That(npc.Inventory.Items.Any(i => ReferenceEquals(i, torch)), Is.False);
        Assert.That(ActionItemResolver.HeldId(world, npc), Is.Empty);
    }


}
}
