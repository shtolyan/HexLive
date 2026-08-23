using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§138: authoritative manual-crafting options and commands.</summary>
[NonParallelizable]
public sealed class ManualCraftingTests
{
    private static readonly (int q, int r)[] RingOne =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)
    };

    // ResetToDefaults() один возвращает КОД-дефолты и стирает simdata-тюнинг
    // для всех фикстур ПОСЛЕ этой: реплей реального сида (жилетка, баг #156) жил
    // на нетюнингованных рецептах и разъехался, как только виртуальные мобы
    // §147 сделали его чувствительным. Поверх сброса — заново экспорт §59.3.
    [TearDown]
    public void ResetRecipes()
    {
        RecipeCatalog.ResetToDefaults();
        SimDataFile.Require(RepoPaths.SimData);
    }

    [Test]
    public void GroundCountUsesCenterAndSixNeighborsButRejectsNonFreeObjects()
    {
        var world = TestWorld.CreateWorld(138001);
        var npc = Colonist(world);
        var center = InteriorTile(world, npc.Fragment);
        Strip(world, ContentIds.Fiber);

        Spawn(world, npc, ContentIds.Fiber, center);
        foreach (var (q, r) in RingOne)
        {
            Spawn(world, npc, ContentIds.Fiber,
                new TileCoord(center.Q + q, center.R + r));
        }

        var distanceTwo = new TileCoord(center.Q + 2, center.R);
        Assert.That(world.Tiles.Items.ContainsKey(distanceTwo), Is.True,
            "The selected fixture tile must have a distance-two control.");
        Spawn(world, npc, ContentIds.Fiber, distanceTwo);

        var occupied = Spawn(world, npc, ContentIds.Fiber, center);
        occupied.IsOccupied = true;
        occupied.CurrentUser = npc.Id;
        var project = Spawn(world, npc, ContentIds.Fiber, center);
        project.CraftWorkRequired = 24;
        project.CraftWorkDone = 0;
        var buildSite = Spawn(world, npc, ContentIds.Fiber, center);
        buildSite.BuildProduct = ContentIds.Workbench;
        var container = Spawn(world, npc, ContentIds.Fiber, center);
        container.Contents.Add(new ItemInstance(ContentIds.Stone));

        Assert.That(CraftingOptions.CountGroundInputs(
            world, npc, center, ContentIds.Fiber), Is.EqualTo(7),
            "Only seven free physical pieces in the center+ring-one zone count.");
    }

    [Test]
    public void RecipeOptionDoesNotCombineSeparateRemotePiles()
    {
        var world = TestWorld.CreateWorld(138002);
        var npc = PrepareManual(world);
        ConfigureCloth(stones: 4, workTicks: 72);
        Strip(world, ContentIds.Stone, ContentIds.Cloth);

        Spawn(world, npc, ContentIds.Stone, npc.Tile);
        Spawn(world, npc, ContentIds.Stone, npc.Tile);
        var remote = world.Tiles.Items.Keys.First(tile =>
            world.Tiles.Items[tile].Junctions.Count > 0 &&
            HexSpatialMath.HexDistance(tile, npc.Tile) >= 3 &&
            HexSpatialMath.HexDistance(tile, npc.Tile) <= 5);
        Spawn(world, npc, ContentIds.Stone, remote);
        Spawn(world, npc, ContentIds.Stone, remote);

        var option = Option(world, npc, GoalType.CraftCloth);
        Assert.Multiple(() =>
        {
            Assert.That(option.CanCraft, Is.False);
            Assert.That(option.BlockReason, Is.EqualTo(CraftBlockReason.MissingResources));
            Assert.That(option.Ingredients.Single().Available, Is.EqualTo(2));
            Assert.That(option.Ingredients.Single().Required, Is.EqualTo(4));
        });
    }

    [Test]
    public void StationChoiceIsDistanceThenIdAndSkipsABusyCandidate()
    {
        var world = TestWorld.CreateWorld(138009);
        var npc = PrepareManual(world);
        RecipeCatalog.Override(ContentIds.WoodenArm,
            new[] { new RecipeIngredient(ContentIds.Stone, 1) },
            needsLitFire: false, station: "Workbench", baseWorkTicks: 48);
        Strip(world, ContentIds.Stone, ContentIds.WoodenArm);
        Add(npc, ContentIds.Stone, 1);

        var stationDefinition = new ObjectDefinition
        {
            Id = "test.manual_craft_station",
            DisplayName = "Test workbench"
        };
        stationDefinition.Tags.Add("Workbench");
        world.Content.ObjectDefinitions[stationDefinition.Id] = stationDefinition;
        var first = WorldObjectMutations.SpawnObject(
            world, stationDefinition.Id, npc.Fragment, npc.Tile,
            npc.CurrentJunction.Value);
        var second = WorldObjectMutations.SpawnObject(
            world, stationDefinition.Id, npc.Fragment, npc.Tile,
            npc.CurrentJunction.Value);
        first.CraftJunction = npc.CurrentJunction;
        second.CraftJunction = npc.CurrentJunction;

        Assert.That(Option(world, npc, GoalType.CraftWoodenArm).StationObjectId,
            Is.EqualTo(first.Id), "Equal-distance stations must tie-break by object id.");

        first.IsOccupied = true;
        first.CurrentUser = world.Entities.Npcs.Values.First(other => other.Id != npc.Id).Id;
        Assert.That(Option(world, npc, GoalType.CraftWoodenArm).StationObjectId,
            Is.EqualTo(second.Id), "A ready free station beats a nearer/equal busy fallback.");
    }

    [Test]
    public void CommandPaysInventoryAndGroundAtomicallyAndRejectsAStaleBill()
    {
        var engine = TestWorld.CreateEngine(138003);
        var world = engine.World;
        var npc = PrepareManual(world);
        ConfigureCloth(stones: 4, workTicks: 72);
        Strip(world, ContentIds.Stone, ContentIds.Cloth);

        Add(npc, ContentIds.Stone, 2);
        Spawn(world, npc, ContentIds.Stone, npc.Tile);
        Spawn(world, npc, ContentIds.Stone, npc.Tile);
        Assert.That(Option(world, npc, GoalType.CraftCloth).CanCraft, Is.True);

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftCloth));
        engine.Step();

        var projects = world.Entities.Objects.Values.Where(obj =>
            obj.DefinitionId == ContentIds.Cloth && obj.IsCraftProject).ToList();
        Assert.That(projects, Is.Not.Empty,
            $"goal={npc.Mind.CurrentGoal} plan={npc.Plan.Goal}/{npc.Plan.Status} " +
            $"exec={npc.Execution.Status}/{npc.Execution.CurrentInteraction}; " +
            string.Join(" | ", world.Events.Items.Select(e => $"{e.Type}:{e.Message}")));
        var project = projects.Single();
        Assert.Multiple(() =>
        {
            Assert.That(project.Contents.Count(item =>
                item.DefinitionId == ContentIds.Stone), Is.EqualTo(4));
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Stone), Is.Zero);
            Assert.That(CraftingOptions.CountGroundInputs(
                world, npc, npc.Tile, ContentIds.Stone), Is.Zero);
        });

        // A second world isolates the stale-click promise: the UI saw a full
        // bill, but it vanished before the command reached the next sim tick.
        var staleEngine = TestWorld.CreateEngine(138004);
        var staleWorld = staleEngine.World;
        var staleNpc = PrepareManual(staleWorld);
        ConfigureCloth(stones: 4, workTicks: 72);
        Strip(staleWorld, ContentIds.Stone, ContentIds.Cloth);
        Add(staleNpc, ContentIds.Stone, 4);
        Assert.That(Option(staleWorld, staleNpc, GoalType.CraftCloth).CanCraft, Is.True);
        staleNpc.Inventory.Items.RemoveAll(item => item.DefinitionId == ContentIds.Stone);

        staleEngine.Commands.Enqueue(
            new CraftItemCommand(staleNpc.Id, GoalType.CraftCloth));
        staleEngine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(staleWorld.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.Cloth && obj.IsCraftProject), Is.False);
            Assert.That(staleNpc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(HasTrace(staleWorld, staleNpc.Id,
                "ManualOrderRejected", "Reason=MissingResources"), Is.True);
        });
    }

    [Test]
    public void OneLongManualOrderSurvivesStopResumeAndSaveLoadToOneOutput()
    {
        var engine = TestWorld.CreateEngine(138005);
        var world = engine.World;
        var npc = PrepareManual(world);
        ConfigureCloth(stones: 4, workTicks: 72);
        Strip(world, ContentIds.Stone, ContentIds.Cloth);
        Add(npc, ContentIds.Stone, 4);

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftCloth));
        StepUntil(engine, () => world.Entities.Objects.Values.Any(obj =>
            obj.DefinitionId == ContentIds.Cloth && obj.IsCraftProject &&
            obj.CraftWorkDone >= Spec119.CraftCycleWork), 300);
        var paidProject = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.Cloth && obj.IsCraftProject);

        engine.Commands.Enqueue(new StopCommand(npc.Id));
        engine.Step();
        Assert.Multiple(() =>
        {
            Assert.That(paidProject.IsCraftProject, Is.True);
            Assert.That(paidProject.Contents, Has.Count.EqualTo(4));
            Assert.That(paidProject.IsOccupied, Is.False);
            Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active));
        });
        var resume = Option(world, npc, GoalType.CraftCloth);
        Assert.Multiple(() =>
        {
            Assert.That(resume.IsResume, Is.True);
            Assert.That(resume.CanCraft, Is.True);
            Assert.That(resume.ProjectObjectId, Is.EqualTo(paidProject.Id));
            Assert.That(resume.Ingredients.Single().Available,
                Is.GreaterThanOrEqualTo(resume.Ingredients.Single().Required),
                "Paid project contents count as already supplied ingredients.");
        });

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftCloth));
        engine.Step();
        using var save = new MemoryStream();
        using (var writer = new BinaryWriter(save, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        var loadedEngine = TestWorld.CreateEngine(138005);
        ConfigureCloth(stones: 4, workTicks: 72);
        save.Position = 0;
        using (var reader = new BinaryReader(save, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loadedEngine.World, reader);
        }

        var loadedNpc = loadedEngine.World.Entities.Npcs[npc.Id];
        StepUntil(loadedEngine, () =>
            CountCompletedCloth(loadedEngine.World, loadedNpc) == 1 &&
            loadedNpc.Mind.CurrentGoal == GoalType.None &&
            loadedNpc.Plan.Status == PlanStatus.Completed,
            600);
        Assert.Multiple(() =>
        {
            Assert.That(CountCompletedCloth(loadedEngine.World, loadedNpc), Is.EqualTo(1));
            Assert.That(loadedEngine.World.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.Cloth && obj.IsCraftProject), Is.False);
            Assert.That(loadedNpc.Mind.ManualControl, Is.True);
            Assert.That(loadedNpc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(loadedNpc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(loadedNpc.Mind.LastManualInputTick,
                Is.GreaterThanOrEqualTo(loadedEngine.World.Tick - 1));
        });
    }

    [Test]
    public void ManualLeatherCraftKeepsTheImmediateWearResult()
    {
        var engine = TestWorld.CreateEngine(138006);
        var world = engine.World;
        var npc = PrepareManual(world);
        RecipeCatalog.Override(ContentIds.LeatherPants,
            new[] { new RecipeIngredient(ContentIds.Hide, 1) },
            needsLitFire: false, station: "", baseWorkTicks: 24);
        Strip(world, ContentIds.Hide, ContentIds.LeatherPants);
        npc.WornItems.RemoveAll(id => id == ContentIds.LeatherPants);
        Add(npc, ContentIds.Hide, 1);

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftLeather));
        StepUntil(engine, () =>
            npc.WornItems.Contains(ContentIds.LeatherPants) &&
            npc.Plan.Status == PlanStatus.Completed,
            300);

        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Count(id => id == ContentIds.LeatherPants),
                Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Any(item =>
                item.DefinitionId == ContentIds.LeatherPants), Is.False);
            Assert.That(world.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.LeatherPants), Is.False);
        });
    }

    [Test]
    public void ManualStoneAxeOrderFinishesTheRealTunedRecipe()
    {
        var engine = TestWorld.CreateEngine(1365566998);
        var world = engine.World;
        var npc = PrepareManual(world);
        SimDataFile.Require(RepoPaths.SimData);
        Strip(world, ContentIds.Stick, ContentIds.Stone, ContentIds.AxeStone);
        Add(npc, ContentIds.Stick, 1);
        Add(npc, ContentIds.Stone, 1);

        var option = Option(world, npc, GoalType.CraftAxe);
        Assert.Multiple(() =>
        {
            Assert.That(option.CanCraft, Is.True);
            Assert.That(option.StationTag, Is.Empty,
                "The shipped stone axe is a hand craft, not a campfire job.");
            Assert.That(option.WorkRequired, Is.Zero,
                "A fresh order has no project progress yet.");
        });

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftAxe));
        StepUntil(engine, () =>
            npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.AxeStone) &&
            npc.Mind.CurrentGoal == GoalType.None &&
            npc.Plan.Status == PlanStatus.Completed,
            300);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.AxeStone), Is.EqualTo(1));
            Assert.That(world.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.AxeStone && obj.IsCraftProject), Is.False);
        });
    }

    [Test]
    public void ManualStoneAxeWalksToTheRemoteGroundBillBeforeCrafting()
    {
        var engine = TestWorld.CreateEngine(1365566998);
        var world = engine.World;
        var npc = PrepareManual(world);
        foreach (var other in world.Entities.Npcs.Values)
        {
            other.Mind.ManualControl = true;
        }
        SimDataFile.Require(RepoPaths.SimData);
        Strip(world, ContentIds.Stick, ContentIds.Stone, ContentIds.AxeStone);
        Add(npc, ContentIds.Stick, 1);
        var remote = world.Tiles.Items.Keys.First(tile =>
            world.Tiles.Items[tile].Junctions.Count > 0 &&
            HexSpatialMath.HexDistance(tile, npc.Tile) >= 3 &&
            HexSpatialMath.HexDistance(tile, npc.Tile) <= 5 &&
            Connectivity.Reachable(
                world, npc.CurrentJunction.Value,
                world.Tiles.Items[tile].Junctions[0], npc.Body.CanJump));
        Spawn(world, npc, ContentIds.Stone, remote);

        var option = Option(world, npc, GoalType.CraftAxe);
        Assert.Multiple(() =>
        {
            Assert.That(option.CanCraft, Is.True);
            Assert.That(option.WorkTile, Is.EqualTo(remote));
            Assert.That(option.WorkJunction, Is.Not.EqualTo(npc.CurrentJunction));
        });

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftAxe));
        engine.Step();
        Assert.That(npc.Plan.Steps.Select(step => step.Type), Is.EqualTo(new[]
        {
            PlanStepType.MoveToJunction,
            PlanStepType.CraftInPlace
        }));

        for (var tick = 0; tick < 600 && npc.Plan.Status != PlanStatus.Failed &&
             !(npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.AxeStone) &&
               npc.Plan.Status == PlanStatus.Completed); tick++)
        {
            engine.Step();
        }

        Assert.That(npc.Inventory.Items.Any(item =>
            item.DefinitionId == ContentIds.AxeStone) &&
            npc.Plan.Status == PlanStatus.Completed, Is.True,
            $"tile={npc.Tile} junction={npc.CurrentJunction?.Value} " +
            $"target={npc.Plan.TargetJunctionId?.Value} move={npc.Movement.Status}/" +
            $"{npc.Movement.JunctionPath.Count} plan={npc.Plan.Status}/" +
            $"{npc.Plan.CurrentStepIndex} exec={npc.Execution.Status}/" +
            $"{npc.Execution.CurrentInteraction} stones=" +
            $"{string.Join(',', world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Stone).Select(o => $"{o.Id.Value}@{o.Tile}"))}; " +
            string.Join(" | ", world.Events.Items.Where(e =>
                e.Type is "ExecFailed" or "PlanFailed" or "ManualOrderRejected")
                .Select(e => $"{e.Type}:{e.Message}")));

        Assert.Multiple(() =>
        {
            Assert.That(npc.Tile, Is.EqualTo(remote));
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.AxeStone), Is.EqualTo(1));
        });
    }

    [Test]
    public void CookingCountsRawMeatOnlyInInventoryAndHangsItOnTheSpit()
    {
        var engine = TestWorld.CreateEngine(138007);
        var world = engine.World;
        var npc = PrepareManual(world);
        Strip(world, ContentIds.Campfire, ContentIds.MeatRaw, ContentIds.MeatCooked);
        var stationJunction = npc.CurrentJunction.Value;
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, npc.Tile, stationJunction);
        var approachNode = world.Junctions.Items.Values.First(node =>
            node.Fragment.Equals(npc.Fragment) && !node.Blocked &&
            node.Tiles.Count > 0 && SpatialQueries.IsJunctionFree(world, node.Id));
        var approach = approachNode.Id;
        var previousTile = npc.Tile;
        npc.Tile = approachNode.Tiles[0];
        npc.Fragment = approachNode.Fragment;
        npc.Position = approachNode.WorldPosition;
        npc.CurrentJunction = approach;
        SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, approach, npc.Id);
        fire.CraftJunction = approach;
        fire.ResourceAmount = 10f;
        Add(fire.Contents, BuildSiteMath.MaterialSticks, SimBalance.CampfireBillSticks);
        Add(fire.Contents, BuildSiteMath.MaterialRope, SimBalance.CampfireBillRope);
        Assert.That(BuildSiteMath.CampfireSpitComplete(fire), Is.True);

        // A loose chunk beside the fire is deliberately not a cooking input:
        // the existing specialized action takes meat from the cook's pack.
        Spawn(world, npc, ContentIds.MeatRaw, fire.Tile);
        var groundOnly = Option(world, npc, GoalType.CookMeat);
        Assert.Multiple(() =>
        {
            Assert.That(groundOnly.CanCraft, Is.False);
            Assert.That(groundOnly.BlockReason,
                Is.EqualTo(CraftBlockReason.MissingResources));
            Assert.That(groundOnly.Ingredients.Single().Available, Is.Zero);
        });

        Add(npc, ContentIds.MeatRaw, 1);
        Assert.That(Option(world, npc, GoalType.CookMeat).CanCraft, Is.True);
        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CookMeat));
        StepUntil(engine, () =>
            BuildSiteMath.HangingMeat(fire, ContentIds.MeatRaw) +
            BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked) == 1 &&
            npc.Plan.Status == PlanStatus.Completed,
            300);

        Assert.That(npc.Inventory.Items.Any(item =>
            item.DefinitionId == ContentIds.MeatRaw), Is.False);
    }

    [Test]
    public void CompletedManualCraftStaysAtTheWorkplaceWhenBackpackIsFull()
    {
        var engine = TestWorld.CreateEngine(138008);
        var world = engine.World;
        var npc = PrepareManual(world);
        ConfigureCloth(stones: 1, workTicks: 24);
        Strip(world, ContentIds.Stone, ContentIds.Cloth);
        foreach (var bystander in world.Entities.Npcs.Values.Where(other => other.Id != npc.Id))
        {
            bystander.Mind.ManualControl = true;
            bystander.Mind.CurrentGoal = GoalType.None;
            bystander.Needs.Hunger = 0f;
            bystander.Needs.Thirst = 0f;
            bystander.Plan.Status = PlanStatus.Completed;
            bystander.Plan.Steps.Clear();
        }
        npc.Inventory.Items.Clear();
        npc.Inventory.HolsterSlotIds.Clear();
        EquipmentMath.RecalculateCapacity(world, npc);
        for (var i = 0; i < npc.Inventory.Capacity; i++)
        {
            var blockerId = "test.manual_craft_blocker." + i;
            world.Content.ObjectDefinitions[blockerId] = new ObjectDefinition
            {
                Id = blockerId,
                DisplayName = blockerId
            };
            npc.Inventory.Items.Add(new ItemInstance(blockerId));
        }
        Spawn(world, npc, ContentIds.Stone, npc.Tile);

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftCloth));
        var sawActive = false;
        for (var i = 0; i < 300; i++)
        {
            engine.Step();
            sawActive |= npc.Plan.Status == PlanStatus.Active;
            if (sawActive && npc.Plan.Status == PlanStatus.Completed) break;
        }
        var groundOutputs = world.Entities.Objects.Values.Where(obj =>
            obj.DefinitionId == ContentIds.Cloth && !obj.IsCraftProject).ToList();
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed),
            $"goal={npc.Mind.CurrentGoal}; inv={string.Join(',', npc.Inventory.Items)}; " +
            $"cloth={string.Join(',', world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Cloth).Select(o => $"{o.Id.Value}:{o.CraftWorkDone}/{o.CraftWorkRequired}"))}");

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Any(item =>
                item.DefinitionId == ContentIds.Cloth), Is.False);
            Assert.That(groundOutputs, Has.Count.EqualTo(1));
        });
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static NPCState PrepareManual(WorldState world)
    {
        var npc = Colonist(world);
        npc.Mind.ManualControl = true;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.LastManualInputTick = world.Tick;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Health = System.Math.Max(0.8f, npc.Health);
        npc.CurrentJunction ??= world.Tiles.Items[npc.Tile].Junctions[0];
        npc.Plan.Status = PlanStatus.Completed;
        npc.Plan.Steps.Clear();
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        return npc;
    }

    private static void ConfigureCloth(int stones, int workTicks) =>
        RecipeCatalog.Override(ContentIds.Cloth,
            new[] { new RecipeIngredient(ContentIds.Stone, stones) },
            needsLitFire: false, station: "", baseWorkTicks: workTicks);

    private static CraftRecipeOption Option(
        WorldState world, NPCState npc, GoalType goal)
    {
        var options = new List<CraftRecipeOption>();
        Assert.That(CraftingOptions.TryFill(world, npc.Id, options), Is.True);
        return options.Single(option => option.Goal == goal);
    }

    private static WorldObjectState Spawn(
        WorldState world, NPCState npc, string definitionId, TileCoord tile)
    {
        var junction = world.Tiles.Items[tile].Junctions[0];
        return WorldObjectMutations.SpawnObject(
            world, definitionId, npc.Fragment, tile, junction);
    }

    private static TileCoord InteriorTile(WorldState world, FragmentId fragment)
    {
        var fragmentTiles = world.Fragments.Items[fragment].Tiles;
        return fragmentTiles.Keys.First(center =>
            fragmentTiles[center].Junctions.Count > 0 &&
            RingOne.All(direction =>
            {
                var neighbor = new TileCoord(
                    center.Q + direction.q, center.R + direction.r);
                return fragmentTiles.TryGetValue(neighbor, out var tile) &&
                    tile.Junctions.Count > 0;
            }) && fragmentTiles.ContainsKey(new TileCoord(center.Q + 2, center.R)));
    }

    private static void Strip(WorldState world, params string[] definitionIds)
    {
        var set = new HashSet<string>(definitionIds);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Inventory.Items.RemoveAll(item => set.Contains(item.DefinitionId));
        }
        var objects = world.Entities.Objects.Values
            .Where(obj => set.Contains(obj.DefinitionId))
            .Select(obj => obj.Id).ToList();
        foreach (var id in objects) WorldObjectMutations.DespawnObject(world, id);
    }

    private static void Add(NPCState npc, string definitionId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            npc.Inventory.Items.Add(new ItemInstance(definitionId));
        }
    }

    private static void Add(List<ItemInstance> into, string definitionId, int count)
    {
        for (var i = 0; i < count; i++) into.Add(new ItemInstance(definitionId));
    }

    private static void StepUntil(
        SimulationEngine engine, System.Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks && !done(); i++) engine.Step();
        Assert.That(done(), Is.True, $"Condition did not become true in {maxTicks} ticks.");
    }

    private static int CountCompletedCloth(WorldState world, NPCState npc) =>
        npc.Inventory.Items.Count(item => item.DefinitionId == ContentIds.Cloth) +
        world.Entities.Objects.Values.Count(obj =>
            obj.DefinitionId == ContentIds.Cloth && !obj.IsCraftProject);

    private static bool HasTrace(
        WorldState world, EntityId npc, string type, string contains) =>
        world.Events.Items.Any(e => e.Type == type && e.EntityId == npc.Value &&
            (e.Message?.Contains(contains) ?? false));
}

}
