using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>Executable contract for spec §119 and bug #62.</summary>
[NonParallelizable]
public sealed class WorkbenchCraftingTests
{
    [Test]
    public void WorkbenchStages_ExposeExactlyOneResourceAtATime()
    {
        var site = new WorldObjectState
        {
            BuildProduct = ContentIds.Workbench,
            BillSticks = Spec119.WorkbenchBillSticks,
            BillBoards = Spec119.WorkbenchBillBoards,
            BillRope = Spec119.WorkbenchBillRope
        };

        AssertStage(site, BuildSiteMath.MaterialSticks, 4);
        Deliver(site, BuildSiteMath.MaterialSticks, 4);
        AssertStage(site, BuildSiteMath.MaterialSticks, 2);
        Deliver(site, BuildSiteMath.MaterialSticks, 2);
        AssertStage(site, BuildSiteMath.MaterialBoards, 2);
        Deliver(site, BuildSiteMath.MaterialBoards, 2);
        AssertStage(site, BuildSiteMath.MaterialRope, 2);
        Deliver(site, BuildSiteMath.MaterialRope, 2);
        AssertStage(site, BuildSiteMath.MaterialBoards, 4);
        Deliver(site, BuildSiteMath.MaterialBoards, 4);

        Assert.Multiple(() =>
        {
            Assert.That(BuildSiteMath.IsStocked(site), Is.True);
            Assert.That(site.Contents.Count(i => i.DefinitionId == ContentIds.Stick),
                Is.EqualTo(6));
            Assert.That(site.Contents.Count(i => i.DefinitionId == ContentIds.Board),
                Is.EqualTo(6));
            Assert.That(site.Contents.Count(i => i.DefinitionId == ContentIds.Rope),
                Is.EqualTo(2));
        });
    }

    [Test]
    public void WorkbenchYaw_PutsTheFixedPointOnAnExactInteriorJunction()
    {
        foreach (var requested in new[] { 0f, 31f, 89f, 151f, 221f, 359f })
        {
            var yaw = StructurePlacement.QuantizeHexYaw(requested);
            var radians = (yaw + 180f) * (System.MathF.PI / 180f);
            var expectedX = System.MathF.Cos(radians) * Spec119.WorkbenchStandDistance;
            var expectedY = System.MathF.Sin(radians) * Spec119.WorkbenchStandDistance;
            Assert.That(HexPointLayout.GetInteriorTemplates().Any(template =>
                System.MathF.Abs(template.Offset.X - expectedX) < 0.0001f &&
                System.MathF.Abs(template.Offset.Y - expectedY) < 0.0001f), Is.True,
                $"Yaw {yaw}° did not land the 0.75 wu work point on the sub-grid.");
        }
    }

    [Test]
    public void WoodenArmProject_IsPaidAtomically_ResumedByAnotherNpc_AndSaved()
    {
        var world = TestWorld.CreateWorld(119062);
        var workers = world.Entities.Npcs.Values.Take(2).ToList();
        var crafter = workers[0];
        var helper = workers[1];
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        var station = WorldObjectMutations.SpawnObject(
            world, ContentIds.Workbench, crafter.Fragment, crafter.Tile, anchor);

        Add(crafter, ContentIds.Board, 2);
        Add(crafter, ContentIds.Rope, 2);
        Add(crafter, ContentIds.Hide, 1);

        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftWoodenArm, station, out var project), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(project.DefinitionId, Is.EqualTo(ContentIds.WoodenArm));
            Assert.That(project.CraftWorkDone, Is.Zero);
            Assert.That(project.CraftWorkRequired,
                Is.EqualTo(Spec119.WoodenProstheticCraftWork));
            Assert.That(project.Contents, Has.Count.EqualTo(5));
            Assert.That(crafter.Inventory.Items.Any(i =>
                i.DefinitionId == ContentIds.Board ||
                i.DefinitionId == ContentIds.Rope ||
                i.DefinitionId == ContentIds.Hide), Is.False,
                "The whole bill must move into the project in one mutation.");
        });

        Add(helper, ContentIds.Board, 3);
        Add(helper, ContentIds.Rope, 2);
        Add(helper, ContentIds.Hide, 1);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, helper, GoalType.CraftWoodenArm, station, out _), Is.False,
            "The occupied arm project must not be duplicated on the same tabletop.");
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, helper, GoalType.CraftWoodenLeg, station, out _), Is.False,
            "A second unfinished output must not occupy the same tabletop.");

        CraftProjectMath.ReleaseWorker(world, crafter);
        helper.Tile = crafter.Tile;
        helper.Fragment = crafter.Fragment;
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, helper, GoalType.CraftWoodenArm, station, out var resumed), Is.True);
        Assert.That(resumed.Id, Is.EqualTo(project.Id));
        Assert.That(CraftProjectMath.CompleteCycle(world, helper, GoalType.CraftWoodenArm), Is.True);
        Assert.That(project.CraftWorkDone, Is.EqualTo(Spec119.CraftCycleWork));

        helper.Mind.ProstheticAidTargetId = crafter.Id;
        helper.Mind.ProstheticAidPart = BodyPart.ArmL;
        helper.Mind.ProstheticAidRetryAfterTick = 123456;

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(119062);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var savedProject = loaded.Entities.Objects[project.Id];
        var savedHelper = loaded.Entities.Npcs[helper.Id];
        Assert.Multiple(() =>
        {
            Assert.That(savedProject.IsCraftProject, Is.True);
            Assert.That(savedProject.CraftWorkDone, Is.EqualTo(Spec119.CraftCycleWork));
            Assert.That(savedProject.CraftWorkRequired,
                Is.EqualTo(Spec119.WoodenProstheticCraftWork));
            Assert.That(savedProject.CraftStationObjectId, Is.EqualTo(station.Id));
            Assert.That(savedProject.Contents, Has.Count.EqualTo(5));
            Assert.That(savedHelper.Mind.ProstheticAidTargetId, Is.EqualTo(crafter.Id));
            Assert.That(savedHelper.Mind.ProstheticAidPart, Is.EqualTo(BodyPart.ArmL));
            Assert.That(savedHelper.Mind.ProstheticAidRetryAfterTick, Is.EqualTo(123456));
        });
    }

    [Test]
    public void BandageAndLeatherRecipes_KeepPhysicalGameplayOutputs()
    {
        var world = TestWorld.CreateWorld(119065);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        crafter.CurrentJunction = anchor;
        var bandagesBefore = MedicalSupplyMath.BandageCount(crafter);

        Add(crafter, ContentIds.HerbLeaf, 2);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftBandage, null, out var bandage), Is.True);
        Assert.That(CraftProjectMath.CompleteCycle(
            world, crafter, GoalType.CraftBandage), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(bandage.Id), Is.True,
                "A completed dressing stays a takeable physical world item.");
            Assert.That(bandage.IsCraftProject, Is.False);
            Assert.That(bandage.DefinitionId, Is.EqualTo(ContentIds.Bandage));
            Assert.That(bandage.ResourceAmount, Is.EqualTo(1f),
                "Crafted plantain wraps retain herbal provenance.");
            Assert.That(MedicalSupplyMath.BandageCount(crafter), Is.EqualTo(bandagesBefore),
                "Completing the project does not teleport the item into the pack.");
        });

        Add(crafter, ContentIds.Hide, 1);
        var leatherStation = string.IsNullOrEmpty(RecipeCatalog.StationOf(GoalType.CraftLeather))
            ? null
            : WorldObjectMutations.SpawnObject(
                world, ContentIds.Campfire, crafter.Fragment, crafter.Tile, anchor);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftLeather, leatherStation, out var pants), Is.True);
        Assert.That(pants.DefinitionId, Is.EqualTo(ContentIds.LeatherPants));
        Assert.That(CraftProjectMath.CompleteCycle(
            world, crafter, GoalType.CraftLeather), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(pants.IsCraftProject, Is.False);
            Assert.That(world.Content.ObjectDefinitions.ContainsKey(pants.DefinitionId), Is.True,
                "The physical 100% output must be a real world item, not resource.leather.");
        });
    }

    [Test]
    public void BandageCycle_FinalVisualTick_LeavesTakeablePhysicalOutput()
    {
        var world = TestWorld.CreateWorld(119088);
        var crafter = world.Entities.Npcs.Values.First();
        crafter.CurrentJunction ??= world.Tiles.Items[crafter.Tile].Junctions[0];

        Add(crafter, ContentIds.HerbLeaf, 2);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftBandage, null, out var project), Is.True);

        crafter.Execution.StartTick = world.Tick;
        crafter.Execution.EndTick = world.Tick + Spec119.CraftCycleWork;
        world.Tick = crafter.Execution.EndTick;
        CraftProjectMath.UpdateCycleProgress(world, crafter);

        Assert.That(project.IsCraftProject, Is.False,
            "The progress renderer exposes exactly 100% before completion runs.");
        Assert.That(CraftProjectMath.CompleteCycle(
            world, crafter, GoalType.CraftBandage), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.True);
            Assert.That(project.IsCraftProject, Is.False);
            Assert.That(project.DefinitionId, Is.EqualTo(ContentIds.Bandage));
            Assert.That(project.ResourceAmount, Is.EqualTo(1f));
            Assert.That(crafter.Execution.CraftProjectId, Is.Null);
        });
    }

    [Test]
    public void LegacyMedicalPouch_MaterializesIntoPhysicalInventoryOnLoad()
    {
        var world = TestWorld.CreateWorld(119089);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.RemoveAll(item => item.DefinitionId == ContentIds.Bandage);
        npc.Needs.Bandages = 3;
        npc.Needs.HerbalBandages = 1;
        npc.Inventory.Items.RemoveAll(item => item.DefinitionId == ContentIds.Pill);
        npc.Needs.Pills = 2;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(119089);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var restored = loaded.Entities.Npcs[npc.Id];
        Assert.Multiple(() =>
        {
            Assert.That(MedicalSupplyMath.BandageCount(restored), Is.EqualTo(3));
            Assert.That(MedicalSupplyMath.HerbalBandageCount(restored), Is.EqualTo(1));
            Assert.That(restored.Needs.Bandages, Is.Zero);
            Assert.That(restored.Needs.HerbalBandages, Is.Zero);
            Assert.That(MedicalSupplyMath.PillCount(restored), Is.EqualTo(2));
            Assert.That(restored.Needs.Pills, Is.Zero);
            Assert.That(restored.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Bandage), Is.EqualTo(3));
        });
    }

    [Test]
    public void CancellingAStationProject_SpillsEveryPaidIngredient()
    {
        var world = TestWorld.CreateWorld(119063);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        var station = WorldObjectMutations.SpawnObject(
            world, ContentIds.Workbench, crafter.Fragment, crafter.Tile, anchor);
        Add(crafter, ContentIds.Board, 3);
        Add(crafter, ContentIds.Rope, 2);
        Add(crafter, ContentIds.Hide, 1);

        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftWoodenLeg, station, out var project), Is.True);
        var before = CountLooseBill(world);
        Assert.That(CraftProjectMath.Cancel(world, project.Id, "test cancel"), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.False);
            Assert.That(CountLooseBill(world) - before, Is.EqualTo(6));
        });
    }

    [Test]
    public void OrdinaryReachableGroundToolSuppressesCraftingAnotherCopy()
    {
        var world = TestWorld.CreateWorld(119065);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        var output = RecipeCatalog.OutputOf(GoalType.CraftAxe);
        var axe = WorldObjectMutations.SpawnObject(
            world, output, crafter.Fragment, crafter.Tile, anchor);
        crafter.Perception.Objects.Clear();
        crafter.Perception.Objects.Add(new PerceivedObject
        {
            Id = axe.Id,
            DefinitionId = output,
            Tile = axe.Tile,
            IsReachable = true,
            IsOccupied = false,
            Distance = 0f
        });

        Assert.That(CraftProjectMath.HasReachableCompletedOutput(
                world, crafter, GoalType.CraftAxe), Is.True,
            "A ready axe on the ground must route through GatherTools instead " +
            "of paying for a new CraftAxe project.");

        axe.IsOccupied = true;
        Assert.That(CraftProjectMath.HasReachableCompletedOutput(
                world, crafter, GoalType.CraftAxe), Is.False,
            "A tool currently used by somebody else must not block future crafting.");
    }

    [Test]
    public void NewMap_SpawnsExactlyTwoArmsAndTwoLegs_OnDistinctDryTiles()
    {
        var world = TestWorld.CreateWorld(119064);
        var expected = new HashSet<string>
        {
            ContentIds.WoodenArm, ContentIds.MechanicalArm,
            ContentIds.WoodenLeg, ContentIds.MechanicalLeg
        };
        var drops = world.Entities.Objects.Values
            .Where(o => expected.Contains(o.DefinitionId)).ToList();

        Assert.That(drops.Select(o => o.DefinitionId), Is.EquivalentTo(expected));
        Assert.That(drops.Select(o => o.Tile).Distinct().Count(), Is.EqualTo(4));
        foreach (var drop in drops)
        {
            var tile = world.Tiles.Items[drop.Tile];
            Assert.Multiple(() =>
            {
                Assert.That(tile.Flags.HasFlag(TileFlags.Water), Is.False);
                Assert.That(tile.Flags.HasFlag(TileFlags.Blocked), Is.False);
                Assert.That(drop.Junctions, Is.Not.Empty);
            });
        }
    }

    private static void AssertStage(WorldObjectState site, string material, int remaining)
    {
        foreach (var candidate in BuildSiteMath.AllMaterials)
        {
            Assert.That(BuildSiteMath.Remaining(site, candidate),
                Is.EqualTo(candidate == material ? remaining : 0),
                $"Unexpected active workbench stage for {candidate}.");
        }
    }

    private static void Deliver(WorldObjectState site, string id, int count)
    {
        for (var i = 0; i < count; i++) site.Contents.Add(new ItemInstance(id));
    }

    private static void Add(NPCState npc, string id, int count)
    {
        for (var i = 0; i < count; i++) npc.Inventory.Items.Add(new ItemInstance(id));
    }

    private static int CountLooseBill(WorldState world) => world.Entities.Objects.Values.Count(o =>
        !o.IsCraftProject && o.Contents.Count == 0 &&
        (o.DefinitionId == ContentIds.Board ||
         o.DefinitionId == ContentIds.Rope ||
         o.DefinitionId == ContentIds.Hide));
}

}
