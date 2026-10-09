using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class ManualCannibalismTests
{
    private static (SimulationEngine engine, NPCState actor, NPCState victim) Fixture()
    {
        var engine = TestWorld.CreateEngine();
        var pair = engine.World.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        foreach (var npc in engine.World.Entities.Npcs.Values)
        {
            npc.Mind.ManualControl = true;
            npc.Needs.Hunger = npc.Needs.Thirst = 0;
        }
        engine.Step();
        pair[0].Inventory.Items.Add(new ItemInstance("tool.knife"));
        pair[1].Mind.FaintedUntilTick = 100000;
        return (engine, pair[0], pair[1]);
    }

    private static void Until(SimulationEngine engine, Func<bool> done)
    {
        for (var i = 0; i < 2000 && !done(); i++) engine.Step();
        Assert.That(done(), Is.True, string.Join("\n", engine.World.Entities.Npcs.Values.Select(n => $"NPC{n.Id} goal={n.Mind.CurrentGoal} plan={n.Plan.Status} target={n.Plan.TargetObjectId} step={n.Plan.CurrentStepIndex} exec={n.Execution.Status}/{n.Execution.CurrentInteraction} movement={n.Movement.Status} items={string.Join(",",n.Inventory.Items)}")) + "\n" + string.Join("\n",engine.World.Events.Items.Where(e=>e.Type.Contains("Failed") || e.Type.Contains("Blocked") || e.Type.Contains("Aborted") || e.Type.Contains("Manual")).TakeLast(15).Select(e=>e.Type)));
    }

    [Test]
    public void ManualButcheryAtZeroHungerCreatesPartsAndPreservesBelongings()
    {
        var (engine, actor, victim) = Fixture();
        var world = engine.World;
        victim.Faction = Faction.Outsiders;
        victim.Body.Severed.Add(BodyPart.ArmL);
        victim.Inventory.Items.Add(new ItemInstance(ContentIds.Stone) { Durability=.231f, Dirtiness=.42f });
        victim.Body.Condition(BodyPart.ArmL).Prosthetic = new ProstheticState { DefinitionId=ContentIds.WoodenArm, Condition=50, MaxCondition=100 };
        var admission = ManualCommandExecutor.Apply(world, new ButcherPersonCommand(actor.Id, victim.Id));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        Until(engine, () => world.Events.Items.Any(e => e.Type == "Butchered"));
        var parts = world.Entities.Objects.Values.Select(o => o.DefinitionId)
            .Concat(actor.Inventory.Items.Select(i => i.DefinitionId)).Where(HumanMeatCatalog.IsHumanPart).ToArray();
        Assert.That(parts.Count(id => id == HumanMeatCatalog.ArmRaw), Is.EqualTo(1));
        Assert.That(parts.Count(id => id == HumanMeatCatalog.LegRaw), Is.EqualTo(2));
        Assert.That(parts.Count(id => id == HumanMeatCatalog.TorsoRaw), Is.EqualTo(1));
        Assert.That(world.Entities.Npcs.ContainsKey(victim.Id), Is.False);
        Assert.That(world.Entities.Corpses.ContainsKey(victim.Id), Is.False);
        Assert.That(ManualCommandExecutor.Apply(world, new ButcherPersonCommand(actor.Id, victim.Id)).Reason,
            Is.EqualTo("TargetGone"));
        Assert.That(world.Entities.Objects.Values.Any(o=>o.DefinitionId==ContentIds.Stone && o.Durability==.231f && o.Dirtiness==.42f) ||
            actor.Inventory.Items.Any(i=>i.DefinitionId==ContentIds.Stone && i.Durability==.231f && i.Dirtiness==.42f), Is.True);
        Assert.That(world.Entities.Objects.Values.Count(o=>o.DefinitionId==ContentIds.WoodenArm && o.Durability==.5f) +
            actor.Inventory.Items.Count(i=>i.DefinitionId==ContentIds.WoodenArm && i.Durability==.5f), Is.EqualTo(1));
    }

    [Test]
    public void WakingDuringApproachCancelsWithoutKillingOrYield()
    {
        var (engine, actor, victim) = Fixture();
        Assert.That(ManualCommandExecutor.Apply(engine.World, new ButcherPersonCommand(actor.Id, victim.Id)).Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        victim.Mind.FaintedUntilTick = 0;
        victim.Mind.ComaCause = ComaCause.None;
        for (var i=0;i<80;i++) engine.Step();
        Assert.That(victim.Health, Is.GreaterThan(0));
        Assert.That(engine.World.Events.Items.Any(e=>e.Type=="Butchered"), Is.False);
        Assert.That(actor.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.PlayerOrder));
    }

    [Test]
    public void PreyContinuesIntoButcheryWhenNamedVictimFalls()
    {
        var (engine, actor, victim) = Fixture();
        victim.Mind.FaintedUntilTick = 0;
        Assert.That(ManualCommandExecutor.Apply(engine.World, new PreyPersonCommand(actor.Id, victim.Id)).Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        victim.Mind.FaintedUntilTick = 100000;
        Until(engine, () => engine.World.Events.Items.Any(e => e.Type == "Butchered"));
        Assert.That(actor.Mind.ManualAttackNpcId, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FullManualPathButcherPickUpCookTakeAndEat(bool alreadyDead)
    {
        var (engine, actor, victim) = Fixture();
        var world = engine.World;
        if (alreadyDead) { victim.Health=0; MobSystem.RemoveDeadNpc(world,victim.Id); }
        actor.Inventory.Items.Clear();
        actor.Inventory.Items.Add(new ItemInstance("tool.knife"));
        Assert.That(ManualCommandExecutor.Apply(world, new ButcherPersonCommand(actor.Id, victim.Id)).Status,
            Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        Until(engine, () => world.Events.Items.Any(e=>e.Type=="Butchered"));
        var raw = HumanMeatCatalog.ArmRaw;
        var arm = world.Entities.Objects.Values.FirstOrDefault(o=>o.DefinitionId==raw);
        if (arm != null)
        {
            var pickup = ManualCommandExecutor.Apply(world, new InteractCommand(actor.Id, arm.Id, InteractionType.PickUp));
            Assert.That(pickup.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), pickup.Reason);
            Until(engine, ()=>actor.Inventory.Items.Any(i=>i.DefinitionId==raw));
        }
        Assert.That(actor.Inventory.Items.Any(i=>i.DefinitionId==raw), Is.True);
        var fire = world.Entities.Objects.Values.First(o=>o.DefinitionId==ContentIds.Campfire);
        fire.Contents.Clear();
        for(var i=0;i<SimBalance.CampfireBillSticks;i++) fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        for(var i=0;i<SimBalance.CampfireBillRope;i++) fire.Contents.Add(new ItemInstance(ContentIds.Rope));
        fire.ResourceAmount=100000;
        // Preserve a real picked-up part while eliminating unrelated edible supplies.
        var selected = actor.Inventory.Items.First(i=>i.DefinitionId==raw);
        actor.Inventory.Items.Clear(); actor.Inventory.Items.Add(selected);
        var cook = ManualCommandExecutor.Apply(world, new CraftItemCommand(actor.Id, GoalType.CookMeat));
        Assert.That(cook.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), cook.Reason);
        Until(engine, ()=>fire.Contents.Any(i=>i.DefinitionId==HumanMeatCatalog.ArmCooked));
        var take = ManualCommandExecutor.Apply(world, new InteractCommand(actor.Id, fire.Id, InteractionType.PickUp));
        Assert.That(take.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), take.Reason);
        Until(engine, ()=>actor.Inventory.Items.Any(i=>i.DefinitionId==HumanMeatCatalog.ArmCooked));
        actor.Needs.Hunger=.8f;
        var eat=ManualCommandExecutor.Apply(world,new SelfActionCommand(actor.Id,SelfActionKind.EatFromPack));
        Assert.That(eat.Status,Is.EqualTo(ManualCommandAdmissionStatus.Accepted),eat.Reason);
        Until(engine,()=>!actor.Inventory.Items.Any(i=>i.DefinitionId==HumanMeatCatalog.ArmCooked));
        Assert.That(actor.Needs.Hunger,Is.LessThan(.7f));
    }

    [Test]
    public void RoastingPartSurvivesSaveLoadWithItsProgressAndIdentity()
    {
        var world=TestWorld.CreateWorld();
        var fire=world.Entities.Objects.Values.First(o=>o.DefinitionId==ContentIds.Campfire);
        fire.Contents.Add(new ItemInstance(HumanMeatCatalog.LegRaw){ResourceAmount=32});
        using var bytes=new System.IO.MemoryStream();
        using(var writer=new System.IO.BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
            HexLive.Simulation.Persistence.WorldSaveSerializer.Write(world,writer);
        bytes.Position=0;
        var restored=TestWorld.CreateWorld();
        using(var reader=new System.IO.BinaryReader(bytes,System.Text.Encoding.UTF8,true))
            HexLive.Simulation.Persistence.WorldSaveSerializer.Read(restored,reader);
        Assert.That(restored.Entities.Objects[fire.Id].Contents.Single(i=>i.DefinitionId==HumanMeatCatalog.LegRaw).ResourceAmount,
            Is.EqualTo(32));
    }

    [TestCase(HumanMeatCatalog.ArmRaw)]
    [TestCase(HumanMeatCatalog.LegRaw)]
    [TestCase(HumanMeatCatalog.TorsoRaw)]
    public void PartsRoastOnOrdinaryFireAndAreEdibleOnlyAfterCooking(string raw)
    {
        var world = TestWorld.CreateWorld();
        var fire = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Campfire);
        fire.Contents.Clear();
        fire.ResourceAmount = 0;
        fire.Contents.Add(new ItemInstance(raw));
        var system = new FireSystem();
        system.Run(world);
        Assert.That(fire.Contents[0].DefinitionId, Is.EqualTo(raw));
        Assert.That(fire.Contents[0].ResourceAmount, Is.Zero);
        Assert.That(world.Content.ObjectDefinitions[raw].Interactions.Any(i=>i.Type==InteractionType.Eat), Is.False);
        fire.ResourceAmount = 100000;
        for (var i=0;i<100;i++) { world.Tick+=world.SlowIntervalTicks; system.Run(world); }
        Assert.That(fire.Contents[0].DefinitionId, Is.EqualTo(HumanMeatCatalog.Cooked(raw)));
        Assert.That(world.Content.ObjectDefinitions[fire.Contents[0].DefinitionId].Interactions
            .Any(i=>i.Type==InteractionType.Eat && i.Effects.HungerDelta<0), Is.True);
    }
}
