using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
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
        var bandageStation = string.IsNullOrEmpty(
                RecipeCatalog.StationOf(GoalType.CraftBandage))
            ? null
            : WorldObjectMutations.SpawnObject(
                world, ContentIds.Campfire, crafter.Fragment, crafter.Tile, anchor);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftBandage, bandageStation, out var bandage), Is.True);
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
        var bandageStation = string.IsNullOrEmpty(
                RecipeCatalog.StationOf(GoalType.CraftBandage))
            ? null
            : WorldObjectMutations.SpawnObject(
                world, ContentIds.Campfire, crafter.Fragment, crafter.Tile,
                crafter.CurrentJunction.Value);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftBandage, bandageStation, out var project), Is.True);

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
            Distance = 0f,
            AvailableInteractions = { InteractionType.PickUp }
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
    public void EveryPersistentItemRecipe_DerivesReadyOutputFromTheCatalog()
    {
        var world = TestWorld.CreateWorld(119090);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];

        foreach (var pair in RecipeCatalog.ItemRecipes())
        {
            var goal = pair.Value.Goal;
            if (!RecipeCatalog.UsesPersistentProject(goal))
            {
                continue;
            }

            foreach (var existing in world.Entities.Objects.Values
                         .Where(o => o.DefinitionId == pair.Key).Select(o => o.Id).ToArray())
            {
                WorldObjectMutations.DespawnObject(world, existing);
            }

            var ready = WorldObjectMutations.SpawnObject(
                world, pair.Key, crafter.Fragment, crafter.Tile, anchor);
            new PerceptionSystem().Run(world);

            Assert.That(CraftProjectMath.HasReachableCompletedOutput(
                    world, crafter, goal), Is.True,
                $"{goal} did not inherit completed-output awareness from " +
                $"RecipeCatalog output {pair.Key}.");

            WorldObjectMutations.DespawnObject(world, ready.Id);
        }
    }

    [Test]
    public void ReadyBandage_TurnsTheCraftGoalIntoPickup_NotAnotherProject()
    {
        var world = TestWorld.CreateWorld(119091);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        crafter.CurrentJunction = anchor;
        crafter.Inventory.Items.RemoveAll(i => i.DefinitionId == ContentIds.Bandage);
        foreach (var loose in world.Entities.Objects.Values
                     .Where(o => o.DefinitionId == ContentIds.Bandage).Select(o => o.Id).ToArray())
        {
            WorldObjectMutations.DespawnObject(world, loose);
        }

        var ready = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bandage, crafter.Fragment, crafter.Tile, anchor);
        new PerceptionSystem().Run(world);
        Assert.That(CraftProjectMath.CanSatisfyItemCraftDemand(
            world, crafter, GoalType.CraftBandage, canManufacture: false), Is.True,
            "A nearby dressing must satisfy demand even with no herbs carried.");

        crafter.Mind.CurrentGoal = GoalType.CraftBandage;
        crafter.Plan.Status = PlanStatus.None;
        crafter.Plan.Steps.Clear();
        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(crafter.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(crafter.Plan.TargetObjectId, Is.EqualTo(ready.Id));
            Assert.That(crafter.Plan.TargetItemDefinitionId, Is.EqualTo(ContentIds.Bandage));
            Assert.That(crafter.Plan.Steps.Last().Interaction,
                Is.EqualTo(InteractionType.PickUp));
            Assert.That(world.Entities.Objects.Values.Count(o =>
                o.DefinitionId == ContentIds.Bandage && o.IsCraftProject), Is.Zero,
                "Planning the reuse path must not create another bandage project.");
        });
    }

    [Test]
    public void VisibleFinishedOutput_BlocksAnotherBill_WhenThePackCannotTakeIt()
    {
        var world = TestWorld.CreateWorld(119092);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        crafter.CurrentJunction = anchor;
        crafter.Inventory.Items.Clear();
        crafter.Inventory.Capacity = 0;
        var ready = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bandage, crafter.Fragment, crafter.Tile, anchor);
        new PerceptionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(CraftProjectMath.HasReachableCompletedOutput(
                world, crafter, GoalType.CraftBandage), Is.True);
            Assert.That(CraftProjectMath.TryFindTakeableCompletedOutput(
                world, crafter, GoalType.CraftBandage, out _), Is.False);
            Assert.That(CraftProjectMath.CanSatisfyItemCraftDemand(
                world, crafter, GoalType.CraftBandage, canManufacture: true), Is.False,
                "A full pack should defer the demand, never authorize a duplicate.");
        });

        Assert.That(world.Entities.Objects.ContainsKey(ready.Id), Is.True);
    }

    [Test]
    public void ReadyBandageInsideDroppedClothing_IsRecoveredWithoutTakingTheClothing()
    {
        var world = TestWorld.CreateWorld(119094);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        crafter.CurrentJunction = anchor;
        crafter.Inventory.Items.RemoveAll(i => i.DefinitionId == ContentIds.Bandage);
        var stash = WorldObjectMutations.SpawnObject(
            world, "clothing.jacket_autumn", crafter.Fragment, crafter.Tile, anchor);
        stash.Contents.Add(new ItemInstance(ContentIds.Bandage));
        stash.Contents.Add(new ItemInstance(ContentIds.Stone));
        new PerceptionSystem().Run(world);

        crafter.Mind.CurrentGoal = GoalType.CraftBandage;
        crafter.Plan.Status = PlanStatus.None;
        crafter.Plan.Steps.Clear();
        new PlanningSystem().Run(world);
        Assert.That(crafter.Plan.TargetObjectId, Is.EqualTo(stash.Id));

        var stand = crafter.Plan.TargetJunctionId!.Value;
        crafter.CurrentJunction = stand;
        crafter.Position = world.Junctions.Items[stand].WorldPosition;
        crafter.Movement.JunctionPath.Clear();
        crafter.Movement.IsMoving = false;
        crafter.Movement.SetStatus(MovementStatus.Arrived);
        var execution = new ExecutionSystem();
        execution.Run(world);
        world.Tick = crafter.Execution.EndTick;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(crafter.Inventory.Items.Count(i =>
                i.DefinitionId == ContentIds.Bandage), Is.EqualTo(1));
            Assert.That(world.Entities.Objects.ContainsKey(stash.Id), Is.True,
                "The dropped garment is only a container, not the requested output.");
            Assert.That(stash.Contents.Select(i => i.DefinitionId),
                Is.EqualTo(new[] { ContentIds.Stone }));
        });
    }

    [Test]
    public void PaidContentsOfANonGarment_DoNotMasqueradeAsLooseCraftOutput()
    {
        var world = TestWorld.CreateWorld(119096);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        foreach (var loose in world.Entities.Objects.Values
                     .Where(o => o.DefinitionId == ContentIds.Rope).Select(o => o.Id).ToArray())
        {
            WorldObjectMutations.DespawnObject(world, loose);
        }

        // A pickable resource object is deliberately given Contents to model
        // paid construction/processing contents. It is not clothing and has no
        // pockets, so the rope is unavailable to an inventory pickup plan.
        var paidContainer = WorldObjectMutations.SpawnObject(
            world, ContentIds.Log, crafter.Fragment, crafter.Tile, anchor);
        paidContainer.Contents.Add(new ItemInstance(ContentIds.Rope));
        new PerceptionSystem().Run(world);

        Assert.That(CraftProjectMath.HasReachableCompletedOutput(
            world, crafter, GoalType.CraftRope), Is.False,
            "Materials already committed to a non-garment object must not be stolen as stash.");
    }

    [Test]
    public void AutonomousStationCraft_KeepsOwnershipThroughTheFinalTakeBeat()
    {
        var world = TestWorld.CreateWorld(119093);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        crafter.CurrentJunction = anchor;
        crafter.Inventory.Items.Clear();
        var station = WorldObjectMutations.SpawnObject(
            world, ContentIds.Workbench, crafter.Fragment, crafter.Tile, anchor);
        Add(crafter, ContentIds.Board, 2);
        Add(crafter, ContentIds.Rope, 2);
        Add(crafter, ContentIds.Hide, 1);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftWoodenArm, station, out var project), Is.True);

        project.CraftWorkDone = project.CraftWorkRequired - Spec119.CraftCycleWork;
        crafter.Execution.CraftCycleStartWork = project.CraftWorkDone;
        crafter.Mind.ManualControl = false;
        crafter.Mind.CurrentGoal = GoalType.CraftWoodenArm;
        crafter.Plan.Goal = GoalType.CraftWoodenArm;
        crafter.Plan.Status = PlanStatus.Active;
        crafter.Plan.TargetObjectId = station.Id;
        crafter.Plan.TargetJunctionId = anchor;
        crafter.Plan.Steps.Clear();
        crafter.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = station.Id,
            TargetJunction = anchor,
            Interaction = InteractionType.Craft
        });
        world.Tick = 100;
        crafter.Execution.Status = ExecutionStatus.InProgress;
        crafter.Execution.CurrentInteraction = InteractionType.Craft;
        crafter.Execution.TargetObject = station.Id;
        crafter.Execution.StartTick = world.Tick - 1;
        crafter.Execution.EndTick = world.Tick;
        station.IsOccupied = true;
        station.CurrentUser = crafter.Id;

        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(project.IsCraftProject, Is.False);
            Assert.That(crafter.Execution.CurrentInteraction,
                Is.EqualTo(InteractionType.PickUp));
            Assert.That(crafter.Execution.CraftLayout, Does.Contain(project.Id));
            Assert.That(crafter.Plan.Status, Is.EqualTo(PlanStatus.Active));
        });

        world.Tick = crafter.Execution.EndTick;
        execution.Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.False,
                "The exact finished world object should move into inventory.");
            Assert.That(crafter.Inventory.Items.Count(i =>
                i.DefinitionId == ContentIds.WoodenArm), Is.EqualTo(1));
            Assert.That(crafter.Plan.Status, Is.EqualTo(PlanStatus.Completed));
        });
    }

    [Test]
    public void AutonomousStationLeather_UsesItsImmediateWearAdapterAfterTheTakeBeat()
    {
        var world = TestWorld.CreateWorld(119097);
        var crafter = world.Entities.Npcs.Values.First();
        var anchor = crafter.CurrentJunction ?? world.Tiles.Items[crafter.Tile].Junctions[0];
        crafter.CurrentJunction = anchor;
        crafter.Inventory.Items.Clear();
        crafter.WornItems.RemoveAll(i => i.DefinitionId == ContentIds.LeatherPants);
        var station = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, crafter.Fragment, crafter.Tile, anchor);
        Add(crafter, ContentIds.Hide, 1);
        Assert.That(CraftProjectMath.TryBeginCycle(
            world, crafter, GoalType.CraftLeather, station, out var project), Is.True);

        project.CraftWorkDone = project.CraftWorkRequired - Spec119.CraftCycleWork;
        crafter.Execution.CraftCycleStartWork = project.CraftWorkDone;
        crafter.Mind.ManualControl = false;
        crafter.Mind.CurrentGoal = GoalType.CraftLeather;
        crafter.Plan.Goal = GoalType.CraftLeather;
        crafter.Plan.Status = PlanStatus.Active;
        crafter.Plan.TargetObjectId = station.Id;
        crafter.Plan.TargetJunctionId = anchor;
        crafter.Plan.Steps.Clear();
        crafter.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = station.Id,
            TargetJunction = anchor,
            Interaction = InteractionType.Craft
        });
        world.Tick = 100;
        crafter.Execution.Status = ExecutionStatus.InProgress;
        crafter.Execution.CurrentInteraction = InteractionType.Craft;
        crafter.Execution.TargetObject = station.Id;
        crafter.Execution.StartTick = world.Tick - 1;
        crafter.Execution.EndTick = world.Tick;
        station.IsOccupied = true;
        station.CurrentUser = crafter.Id;

        var execution = new ExecutionSystem();
        execution.Run(world);
        world.Tick = crafter.Execution.EndTick;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.False);
            Assert.That(crafter.WornItems.Count(i =>
                i.DefinitionId == ContentIds.LeatherPants), Is.EqualTo(1));
            Assert.That(crafter.Inventory.Items.Any(i =>
                i.DefinitionId == ContentIds.LeatherPants), Is.False);
        });
    }

    [Test]
    public void FullPack_UsesNearbyBandageInPlace_WithoutPickingItUp()
    {
        var wasEnabled = Spec118.Enabled;
        Spec118.Enabled = true;
        try
        {
            var world = TestWorld.CreateWorld(119098);
            var patient = world.Entities.Npcs.Values.First();
            var anchor = patient.CurrentJunction ?? world.Tiles.Items[patient.Tile].Junctions[0];
            patient.CurrentJunction = anchor;
            patient.Inventory.Items.Clear();
            patient.Inventory.Capacity = 0;
            patient.Wounds.Clear();
            patient.Wounds.Add(new WoundState
            {
                Id = 119098,
                Zone = BodyPart.Torso,
                Severity = 0.65f,
                Heal01 = 0f,
                Clot01 = 0.2f,
                BleedFactor = 1f
            });
            patient.Needs.Blood = 0.35f;
            foreach (var id in world.Entities.Objects.Values
                         .Where(o => o.DefinitionId == ContentIds.Bandage)
                         .Select(o => o.Id).ToArray())
            {
                WorldObjectMutations.DespawnObject(world, id);
            }

            var bandage = WorldObjectMutations.SpawnObject(
                world, ContentIds.Bandage, patient.Fragment, patient.Tile, anchor);
            bandage.ResourceAmount = 1f;
            new PerceptionSystem().Run(world);
            patient.Mind.CurrentGoal = GoalType.None;
            patient.Mind.Cooldowns.Clear();
            patient.Plan.Status = PlanStatus.Completed;
            patient.Execution.Status = ExecutionStatus.None;
            patient.Plan.Steps.Clear();

            Assert.That(MedicalSupplyMath.TryFindReachableBandageSource(
                world, patient, out var perceivedSource), Is.True);
            Assert.That(perceivedSource.Id, Is.EqualTo(bandage.Id));
            Assert.That(DecisionSystem.SelfTreatmentIndicated(patient, 1), Is.True);
            new DecisionSystem().Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(patient.Mind.LastScores.Single(score =>
                    score.Goal == GoalType.TreatWounds).FinalScore, Is.GreaterThan(0f));
                Assert.That(patient.Mind.CurrentGoal, Is.EqualTo(GoalType.TreatWounds));
            });
            new PlanningSystem().Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(patient.Plan.TargetObjectId, Is.EqualTo(bandage.Id));
                Assert.That(patient.Plan.Steps.Last().Type, Is.EqualTo(PlanStepType.TreatSelf));
                Assert.That(patient.Plan.Steps.Any(step =>
                    step.Interaction == InteractionType.PickUp), Is.False);
                Assert.That(patient.Inventory.Items, Is.Empty);
            });

            var execution = new ExecutionSystem();
            execution.Run(world);
            Assert.That(bandage.CurrentUser, Is.EqualTo(patient.Id),
                "The physical source must be reserved for the whole treatment beat.");
            world.Tick = patient.Execution.EndTick;
            execution.Run(world);

            Assert.Multiple(() =>
            {
                Assert.That(world.Entities.Objects.ContainsKey(bandage.Id), Is.False);
                Assert.That(patient.Inventory.Items, Is.Empty,
                    "Direct treatment must not transiently put the bandage in a full pack.");
                Assert.That(patient.Wounds.Single().Stabilized, Is.True);
                Assert.That(patient.BandagedZones, Does.Contain(BodyPart.Torso));
            });
        }
        finally
        {
            Spec118.Enabled = wasEnabled;
        }
    }

    [Test]
    public void CraftedBandage_FlowsDirectlyIntoTreatment_AndSurvivesInterruption()
    {
        var wasEnabled = Spec118.Enabled;
        Spec118.Enabled = true;
        try
        {
            var world = TestWorld.CreateWorld(119099);
            var patient = world.Entities.Npcs.Values.First();
            var anchor = patient.CurrentJunction ?? world.Tiles.Items[patient.Tile].Junctions[0];
            patient.CurrentJunction = anchor;
            patient.Inventory.Items.Clear();
            patient.Inventory.Capacity = 1;
            Add(patient, ContentIds.HerbLeaf, 2);
            patient.Wounds.Clear();
            patient.Wounds.Add(new WoundState
            {
                Id = 119099,
                Zone = BodyPart.ArmL,
                Severity = 0.7f,
                Heal01 = 0f,
                Clot01 = 0.15f,
                BleedFactor = 1f
            });
            foreach (var id in world.Entities.Objects.Values
                         .Where(o => o.DefinitionId == ContentIds.Bandage)
                         .Select(o => o.Id).ToArray())
            {
                WorldObjectMutations.DespawnObject(world, id);
            }

            Assert.That(CraftProjectMath.TryBeginCycle(
                world, patient, GoalType.CraftBandage, null, out var project), Is.True);
            project.CraftWorkDone = project.CraftWorkRequired - Spec119.CraftCycleWork;
            patient.Execution.CraftCycleStartWork = project.CraftWorkDone;
            patient.Mind.CurrentGoal = GoalType.CraftBandage;
            patient.Plan.Goal = GoalType.CraftBandage;
            patient.Plan.Status = PlanStatus.Active;
            patient.Plan.TargetObjectId = project.Id;
            patient.Plan.TargetJunctionId = null;
            patient.Plan.Steps.Clear();
            patient.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
            world.Tick = 100;
            patient.Execution.Status = ExecutionStatus.InProgress;
            patient.Execution.CurrentInteraction = InteractionType.Craft;
            patient.Execution.TargetObject = project.Id;
            patient.Execution.StartTick = world.Tick - 1;
            patient.Execution.EndTick = world.Tick;

            var execution = new ExecutionSystem();
            execution.Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(project.IsCraftProject, Is.False);
                Assert.That(patient.Plan.Goal, Is.EqualTo(GoalType.TreatWounds));
                Assert.That(patient.Execution.CurrentInteraction,
                    Is.EqualTo(InteractionType.TreatSelf));
                Assert.That(patient.Inventory.Items.Any(i =>
                    i.DefinitionId == ContentIds.Bandage), Is.False);
                Assert.That(project.CurrentUser, Is.EqualTo(patient.Id));
            });

            Assert.That(PlanInterruption.TryAbort(
                world, patient, InterruptionCause.Auction, "test direct-bandage interruption"),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.True,
                    "The crafted bandage must remain physical until treatment completes.");
                Assert.That(project.IsOccupied, Is.False);
                Assert.That(project.CurrentUser, Is.Null);
                Assert.That(patient.Wounds.Single().Stabilized, Is.False);
            });

            new PerceptionSystem().Run(world);
            patient.Mind.CurrentGoal = GoalType.TreatWounds;
            patient.Plan.Status = PlanStatus.None;
            patient.Plan.Steps.Clear();
            new PlanningSystem().Run(world);
            execution.Run(world);
            world.Tick = patient.Execution.EndTick;
            execution.Run(world);

            Assert.Multiple(() =>
            {
                Assert.That(world.Entities.Objects.ContainsKey(project.Id), Is.False);
                Assert.That(patient.Wounds.Single().Stabilized, Is.True);
                Assert.That(patient.Inventory.Items.Any(i =>
                    i.DefinitionId == ContentIds.Bandage), Is.False);
            });
        }
        finally
        {
            Spec118.Enabled = wasEnabled;
        }
    }

    [Test]
    public void GiveOrDrop_FillsAnExistingMedicineStackBeforeDropping()
    {
        var world = TestWorld.CreateWorld(119095);
        var crafter = world.Entities.Npcs.Values.First();
        crafter.Inventory.Items.Clear();
        crafter.Inventory.Capacity = 1;
        Add(crafter, ContentIds.Bandage, InventoryState.MedicineStackSize - 1);
        Assert.That(crafter.Inventory.HasSpace, Is.False,
            "The one inventory cell is already occupied by the partial stack.");

        var looseBefore = world.Entities.Objects.Values.Count(o =>
            o.DefinitionId == ContentIds.Bandage);
        ExecutionSystem.GiveOrDrop(
            world, crafter, MedicalSupplyMath.CreateBandage(herbal: true));

        Assert.Multiple(() =>
        {
            Assert.That(crafter.Inventory.Items.Count(i =>
                i.DefinitionId == ContentIds.Bandage),
                Is.EqualTo(InventoryState.MedicineStackSize));
            Assert.That(world.Entities.Objects.Values.Count(o =>
                o.DefinitionId == ContentIds.Bandage), Is.EqualTo(looseBefore),
                "A free position in the current stack must not spill to the map.");
        });
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
