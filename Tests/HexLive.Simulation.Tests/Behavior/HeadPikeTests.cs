using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class HeadPikeTests
{
    [Test]
    public void OutdoorConstructionConsumesOneRealHeadAndSpearAndBlocksOnlyItsAnchor()
    {
        var engine = TestWorld.CreateEngine(675);
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        Assert.That(engine.ApplyManualCommand(new SetManualControlCommand(npc.Id, true)).Accepted, Is.True);
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = npc.Needs.Energy = 1f;
        npc.Inventory.Items.Clear();
        world.Mobs.Clear();
        engine.Step();

        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count == 1 &&
            j.Id != npc.CurrentJunction && j.Fragment == npc.Fragment &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) <= 2 &&
            SpatialQueries.IsJunctionFree(world, j.Id) &&
            FurniturePlacement.Validate(world, ContentIds.HeadPike, j, 2) is null &&
            Connectivity.Reachable(world, npc.CurrentJunction!.Value, j.Id));
        var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
        var place = engine.ApplyManualCommand(new PlaceFurnitureCommand(ContentIds.HeadPike, key.XKey, key.YKey, 2));
        Assert.That(place.Accepted, Is.True, place.Reason);
        var site = FurniturePlacement.FindAt(world, ContentIds.HeadPike, key);
        Assert.That(site, Is.Not.Null);
        Assert.That(site.DefinitionId, Is.EqualTo(ContentIds.BuildSite));
        Assert.That(site.BlockedJunctions, Is.Empty);
        Assert.That(BuildSiteMath.NeedsHammer(world, site), Is.False);
        Assert.That(BuildSiteMath.Bill(site, HumanMeatCatalog.Head), Is.EqualTo(1));
        Assert.That(BuildSiteMath.Bill(site, ContentIds.Spear), Is.EqualTo(1));
        Assert.That(BuildSiteMath.IsStocked(site), Is.False);

        var spear = new ItemInstance(ContentIds.Spear);
        var head = new ItemInstance(HumanMeatCatalog.Head);
        var spare = new ItemInstance(ContentIds.Spear);
        foreach (var looseSpear in world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Spear).ToArray())
            WorldObjectMutations.DespawnObject(world, looseSpear.Id);
        npc.Inventory.Items.Add(head);
        var partial = engine.ApplyManualCommand(new InteractCommand(npc.Id, site.Id, InteractionType.Build));
        Assert.That(partial.Accepted, Is.True, partial.Reason);
        for (var i = 0; i < 700 && !site.Contents.Any(item => ReferenceEquals(item, head)); i++) engine.Step();
        Assert.That(site.Contents.Any(item => ReferenceEquals(item, head)), Is.True);
        Assert.That(BuildSiteMath.IsStocked(site), Is.False);
        Assert.That(world.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.HeadPike), Is.False);
        for (var i = 0; i < 100 && npc.Plan.Status == PlanStatus.Active; i++) engine.Step();

        npc.Inventory.Items.Add(spear);
        npc.Inventory.Items.Add(spare);
        var build = engine.ApplyManualCommand(new InteractCommand(npc.Id, site.Id, InteractionType.Build));
        Assert.That(build.Accepted, Is.True, build.Reason);
        for (var i = 0; i < 700 && world.Entities.Objects.ContainsKey(site.Id); i++) engine.Step();

        var pike = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.HeadPike);
        Assert.Multiple(() =>
        {
            Assert.That(InventoryMath.ContainsReference(npc.Inventory.Items, spear), Is.False);
            Assert.That(InventoryMath.ContainsReference(npc.Inventory.Items, head), Is.False);
            Assert.That(InventoryMath.ContainsReference(npc.Inventory.Items, spare), Is.True);
            Assert.That(pike.OwnerFaction, Is.EqualTo(npc.Faction));
            Assert.That(pike.RotationDegrees, Is.EqualTo(120f));
            Assert.That(pike.BlockedJunctions, Is.EquivalentTo(new[] { anchor.Id }));
            for (var yaw = 0; yaw < 6; yaw++)
                Assert.That(FurniturePlacement.FootprintKeys(ContentIds.HeadPike, key, yaw), Is.EqualTo(new[] { key }));
        });
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(675);
        using (var reader = new BinaryReader(bytes)) WorldSaveSerializer.Read(restored, reader);
        Assert.That(restored.Entities.Objects[pike.Id].OwnerFaction, Is.EqualTo(npc.Faction));
        Assert.That(restored.Entities.Objects[pike.Id].BlockedJunctions, Is.EquivalentTo(new[] { anchor.Id }));
    }

    [TestCase(TraitKind.Coward, false, 0f, "HeadPikeRetreat", 0.32f)]
    [TestCase(TraitKind.Brave, false, 0.7f, "HeadPikeDefiant", 0.76f)]
    [TestCase(TraitKind.Coward, true, 0f, "HeadPikeUneasy", 0.32f)]
    [TestCase(null, false, 0.5f, "HeadPikeRetreat", 0.7f)]
    [TestCase(null, false, 0f, "HeadPikeUneasy", 0.2f)]
    public void ReachableForeignPikeProducesTheAppropriateReaction(
        TraitKind? trait, bool manual, float initialStress, string cue, float afterStress)
    {
        var (world, npc, pike, seen) = Scene();
        npc.Mind.ManualControl = manual;
        npc.Needs.Stress = initialStress;
        if (trait is { } chosen) npc.Traits.Add(chosen);
        Assert.That(seen.IsReachable, Is.True);
        var system = new HeadPikeWarningSystem();
        system.Run(world);

        Assert.That(npc.Needs.Stress, Is.EqualTo(afterStress).Within(0.001f));
        Assert.That(npc.Execution.LastSocialCueKind, Is.EqualTo(cue));
        Assert.That(npc.Execution.LastSocialCueItemId, Is.EqualTo(ContentIds.HeadPike));
        if (cue == "HeadPikeRetreat")
        {
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Flee));
            var refuge = world.Junctions.Items[npc.Plan.TargetJunctionId!.Value];
            Assert.That(HexSpatialMath.HexDistance(refuge.Tiles[0], pike.Tile),
                Is.GreaterThan(HeadPikeWarningSystem.NoticeRadiusTiles));
        }
        else
        {
            Assert.That(npc.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Flee));
        }

        world.Tick++;
        system.Run(world);
        Assert.That(npc.Needs.Stress, Is.EqualTo(afterStress).Within(0.001f));
    }

    [TestCase("memory")]
    [TestCase("unreachable")]
    [TestCase("allied")]
    public void PikeDoesNotApplyStressWithoutAFreshReachableForeignSighting(string reason)
    {
        var (world, npc, pike, seen) = Scene();
        if (reason == "memory") seen.FromMemory = true;
        if (reason == "unreachable") seen.IsReachable = false;
        if (reason == "allied") pike.OwnerFaction = npc.Faction;
        npc.Needs.Stress = 0.12f;

        new HeadPikeWarningSystem().Run(world);

        Assert.That(npc.Needs.Stress, Is.EqualTo(0.12f));
        Assert.That(npc.Execution.LastSocialCueKind, Does.Not.StartWith("HeadPike"));
    }

    private static (WorldState world, NPCState npc, WorldObjectState pike, PerceivedObject seen) Scene()
    {
        var engine = TestWorld.CreateEngine(675);
        engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Traits.Clear();
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.ManualControl = false;
        npc.Mind.Cooldowns.Clear();
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Plan.Status = PlanStatus.Completed;
        npc.IsFighting = false;
        var point = SpatialQueries.GetPassableNeighbors(world, npc.CurrentJunction!.Value)
            .First(id => SpatialQueries.IsJunctionFree(world, id));
        var tile = world.Junctions.Items[point].Tiles[0];
        var pike = WorldObjectMutations.SpawnObject(world, ContentIds.HeadPike, npc.Fragment, tile, point);
        pike.OwnerFaction = Faction.Outsiders;
        new PerceptionSystem().Run(world);
        var seen = npc.Perception.Objects.Single(obj => obj.Id == pike.Id);
        return (world, npc, pike, seen);
    }
}
