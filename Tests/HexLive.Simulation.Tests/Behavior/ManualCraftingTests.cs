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
    public void ExistingHammerCanBeCraftedInPlaceFromItsPhysicalBill()
    {
        var engine = TestWorld.CreateEngine(44901);
        var world = engine.World;
        var npc = PrepareManual(world);
        Strip(world, ContentIds.Stick, ContentIds.Stone, ContentIds.Rope, GearCatalog.Hammer);
        Add(npc, ContentIds.Stick, 1);
        Add(npc, ContentIds.Stone, 1);
        Add(npc, ContentIds.Rope, 1);
        var options = new List<CraftRecipeOption>();
        CraftingOptions.TryFill(world, npc.Id, options);
        var option = options.Single(o => o.OutputDefinitionId == GearCatalog.Hammer);
        Assert.That(option.CanCraft, Is.True);
        Assert.That(option.StationTag, Is.Empty);
        engine.Commands.Enqueue(new CraftByOutputCommand(npc.Id, GearCatalog.Hammer));
        StepUntil(engine, () => npc.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Hammer) ||
            world.Entities.Objects.Values.Any(o => o.DefinitionId == GearCatalog.Hammer && !o.IsCraftProject), 250);
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == GearCatalog.Hammer) +
            world.Entities.Objects.Values.Count(o => o.DefinitionId == GearCatalog.Hammer && !o.IsCraftProject), Is.EqualTo(1));
        foreach (var id in new[] {ContentIds.Stick, ContentIds.Stone, ContentIds.Rope})
            Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == id), Is.False, id);
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

    [TestCase(false)]
    [TestCase(true)]
    public void ManualLeatherCraftKeepsTheImmediateWearResult(bool byOutput)
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

        engine.Commands.Enqueue(byOutput
            ? new CraftByOutputCommand(npc.Id, ContentIds.LeatherPants)
            : new CraftItemCommand(npc.Id, GoalType.CraftLeather));
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

    [TestCase("clothing.top_primal")]
    [TestCase("clothing.skirt_primal_male")]
    [TestCase("gear.backpack_primal_gatherer")]
    [TestCase("gear.backpack_primal_hunter")]
    [TestCase("gear.backpack_primal_trail")]
    [TestCase("gear.backpack_primal_gatherer_male")]
    [TestCase("gear.backpack_primal_hunter_male")]
    [TestCase("gear.backpack_primal_trail_male")]
    public void PreparedWearBillCompletesAtWorkbenchThroughGenericCommand(string output)
    {
        var savedGarments = GarmentLibrary.Active.ToArray();
        try
        {
            var path = Path.Combine(RepoPaths.Root, "Assets", "HexLiveContent", "People", "catalog.json");
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var row = json.RootElement.GetProperty("records").EnumerateArray().Single(r =>
                r.GetProperty("type").GetString() == "wear" && r.GetProperty("id").GetString() == output);
            var metadata = row.GetProperty("metadata").GetProperty("simulation");
            var craft = metadata.GetProperty("craft");
            var covers = metadata.GetProperty("covers").EnumerateArray().Select(c =>
                System.Enum.Parse<BodyPart>(c.GetString()!)).ToArray();
            GarmentLibrary.Override(savedGarments.Where(g => g.Id != output).Append(new GarmentParams(output,
                metadata.GetProperty("displayName").GetString()!, System.Enum.Parse<WearLayer>(metadata.GetProperty("layer").GetString()!),
                0, 0, 0, 8, metadata.GetProperty("capacity").GetInt32(),
                System.Enum.Parse<GarmentSex>(metadata.GetProperty("sex").GetString()!), covers)));
            var engine = TestWorld.CreateEngine(138604); engine.Step();
            var world = engine.World; var npc = PrepareManual(world);
            var inputs = craft.GetProperty("inputs").EnumerateArray().Select(i => new RecipeIngredient(
                i.GetProperty("id").GetString()!, i.GetProperty("count").GetInt32())).ToArray();
            RecipeCatalog.Override(output, inputs, needsLitFire: false,
                station: craft.GetProperty("station").GetString()!, baseWorkTicks: craft.GetProperty("baseWorkTicks").GetInt32());
            Strip(world, inputs.Select(i => i.Id).Append(output).ToArray());
            foreach (var ingredient in inputs) Add(npc, ingredient.Id, ingredient.Count);
            var definition = new ObjectDefinition { Id = "test.wear_workbench" };
            definition.Tags.Add("Workbench");
            definition.Interactions.Add(new InteractionDefinition { Id = "test.craft", Type = InteractionType.Craft, DurationTicks = 24 });
            world.Content.ObjectDefinitions[definition.Id] = definition;
            var station = WorldObjectMutations.SpawnObject(world, definition.Id, npc.Fragment, npc.Tile, npc.CurrentJunction.Value);
            station.CraftJunction = npc.CurrentJunction;
            Assert.That(CraftingOptions.Resolve(world, npc, output).CanCraft, Is.True);
            Assert.That(engine.ApplyManualCommand(new CraftByOutputCommand(npc.Id, output)).Accepted, Is.True);
            StepUntil(engine, () => npc.Plan.Status == PlanStatus.Completed, 600);
            var count = npc.Inventory.Items.Count(i => i.DefinitionId == output) + npc.WornItems.Count(i => i == output)
                + world.Entities.Objects.Values.Count(o => o.DefinitionId == output && !o.IsCraftProject);
            Assert.That(count, Is.EqualTo(1), output);
            foreach (var input in inputs) Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == input.Id), Is.Zero);
        }
        finally { GarmentLibrary.Override(savedGarments); }
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

    // §138.5 (#233): готовая верёвка почти всегда лежит в лагере — это
    // стройматериал. Заказ игрока обязан вить новую, а не умирать молча в
    // дубль-гейте §119.2, который панель крафта не считает и который поэтому
    // не может доехать до игрока ни одной причиной.
    [Test]
    public void ManualRopeOrderCraftsWhileAFinishedRopeLiesUnderfoot()
    {
        var engine = TestWorld.CreateEngine(138010);
        var world = engine.World;
        var npc = PrepareManual(world);
        SimDataFile.Require(RepoPaths.SimData);
        Strip(world, ContentIds.Fiber, ContentIds.Rope);
        Add(npc, ContentIds.Fiber,
            RecipeCatalog.InputCount(GoalType.CraftRope, ContentIds.Fiber));
        var lying = Spawn(world, npc, ContentIds.Rope, npc.Tile);

        // Восприятие наполняется тиком: на живом мире (сервер, тик 298k)
        // лежащая рядом верёвка УЖЕ воспринята к моменту приказа, и только
        // тогда дубль-гейт §119.2 успевает сработать. Заказ на нулевом тике
        // проскакивал мимо бага и делал регрессию слепой.
        engine.Step();
        engine.Step();
        Assert.That(npc.Perception.Objects.Any(perceived =>
            perceived.Id == lying.Id && perceived.IsReachable), Is.True,
            "The fixture only reproduces #233 while the finished rope is perceived.");

        var option = Option(world, npc, GoalType.CraftRope);
        Assert.Multiple(() =>
        {
            Assert.That(option.CanCraft, Is.True,
                "The panel offers the recipe with a finished rope in sight.");
            Assert.That(option.StationTag, Is.Empty,
                "The shipped rope is a hand craft, not a campfire job.");
        });

        engine.Commands.Enqueue(new CraftItemCommand(npc.Id, GoalType.CraftRope));
        StepUntil(engine, () =>
            npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.Rope) &&
            npc.Mind.CurrentGoal == GoalType.None &&
            npc.Plan.Status == PlanStatus.Completed,
            300);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Rope), Is.EqualTo(1),
                "The accepted order produced exactly one new rope.");
            Assert.That(npc.Inventory.Items.Any(item =>
                item.DefinitionId == ContentIds.Fiber), Is.False,
                "The fiber bill was actually paid.");
            Assert.That(world.Entities.Objects.ContainsKey(lying.Id), Is.True,
                "The rope that was already lying there is untouched.");
            Assert.That(world.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.Rope && obj.IsCraftProject), Is.False,
                "No half-finished rope project is left behind.");
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

    // #234: «действие доступно, а результата нет». Панель считает бинт
    // доступным (бинт лежит рядом её не волнует), а исполнитель молча ронял
    // план: §119-защита от дубля видела готовый выход в восприятии и
    // отказывалась начать цикл. Для ИИ это правильно — решение перенаправят на
    // PickUp; ЯВНЫЙ приказ игрока перенаправлять некому.
    [TestCase(false)]
    [TestCase(true)]
    public void ManualBandageOrderCraftsEvenWithAFinishedBandageLyingNearby(bool byOutput)
    {
        var engine = TestWorld.CreateEngine(138010);
        var world = engine.World;
        var npc = PrepareManual(world);
        SimDataFile.Require(RepoPaths.SimData);
        Strip(world, ContentIds.HerbLeaf, ContentIds.Bandage);
        Add(npc, ContentIds.HerbLeaf, 2);
        var lying = Spawn(world, npc, ContentIds.Bandage, npc.Tile);

        // Восприятие обязано УЖЕ держать лежащий бинт к первому тику
        // исполнения — иначе гонка прячет баг: на холодном восприятии цикл
        // успевал начаться и приказ проходил.
        engine.Step();
        Assert.That(CraftProjectMath.HasReachableCompletedOutput(
            world, npc, GoalType.CraftBandage), Is.True,
            "Фикстура обязана воспроизводить именно «готовый выход на виду».");

        var option = Option(world, npc, GoalType.CraftBandage);
        Assert.Multiple(() =>
        {
            Assert.That(option.CanCraft, Is.True,
                "Панель обязана предлагать бинт — это и видел игрок.");
            Assert.That(option.StationTag, Is.Empty,
                "Бинт в поставке — крафт на месте, без костра.");
        });

        engine.Commands.Enqueue(byOutput
            ? new CraftByOutputCommand(npc.Id, ContentIds.Bandage)
            : new CraftItemCommand(npc.Id, GoalType.CraftBandage));
        StepUntil(engine, () =>
            npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.Bandage) &&
            npc.Plan.Status == PlanStatus.Completed,
            300);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Bandage), Is.EqualTo(1),
                "Заказ обязан выдать ровно один новый бинт.");
            Assert.That(MedicalSupplyMath.HerbalBandageCount(npc), Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Any(item =>
                item.DefinitionId == ContentIds.HerbLeaf), Is.False,
                "Оба листа травы оплачены.");
            Assert.That(world.Entities.Objects.ContainsKey(lying.Id), Is.True,
                "Лежавший рядом чужой бинт приказ не трогает.");
            Assert.That(world.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.Bandage && obj.IsCraftProject), Is.False,
                "Незавершённых проектов после приказа не остаётся.");
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

    // §138.6: ONE fixture recipe, loaded as JSON; no enum/goal/output adapter.
    private static void DataRecipe(string station = "", bool fire = false) =>
        Assert.That(SimDataFile.ApplyJson("{\"recipes\":[{\"output\":\"" + ContentIds.Plaster +
            "\",\"inputs\":[{\"id\":\"resource.stone\",\"count\":2}],\"station\":\"" + station +
            "\",\"needsLitFire\":" + (fire ? "true" : "false") + ",\"baseWorkTicks\":72}]}"), Is.True);

    [TestCase(false)]
    [TestCase(true)]
    public void DataOnlyRecipeCompletesAfterSaveLoadAndLeavesOverflowAtWorkplace(bool fullPack)
    {
        var engine = TestWorld.CreateEngine(138601);
        var world = engine.World;
        var npc = PrepareManual(world);
        DataRecipe();
        Strip(world, ContentIds.Stone, ContentIds.Plaster);
        npc.Inventory.Items.Clear();
        foreach (var other in world.Entities.Npcs.Values.Where(n => n.Id != npc.Id))
        {
            other.Mind.ManualControl = true;
            other.Mind.CurrentGoal = GoalType.None;
            other.Plan.Status = PlanStatus.Completed;
            other.Plan.Steps.Clear();
            other.Needs.Hunger = other.Needs.Thirst = 0f;
        }
        if (fullPack)
        {
            npc.Inventory.HolsterSlotIds.Clear();
            EquipmentMath.RecalculateCapacity(world, npc);
            for (var i = 0; i < npc.Inventory.Capacity * InventoryState.StackSize; i++)
                npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick));
            Spawn(world, npc, ContentIds.Stone, npc.Tile);
            Spawn(world, npc, ContentIds.Stone, npc.Tile);
        }
        else Add(npc, ContentIds.Stone, 2);

        var options = new List<CraftRecipeOption>();
        CraftingOptions.TryFill(world, npc.Id, options);
        var option = options.Single(o => o.OutputDefinitionId == ContentIds.Plaster);
        Assert.That(option.Goal, Is.EqualTo(GoalType.CraftItem));
        Assert.That(option.CanCraft, Is.True);
        engine.Commands.Enqueue(new CraftByOutputCommand(npc.Id, ContentIds.Plaster));
        StepUntil(engine, () => world.Entities.Objects.Values.Any(o =>
            o.DefinitionId == ContentIds.Plaster && o.IsCraftProject && o.CraftWorkDone >= 24), 300);
        var project = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.Plaster);
        Assert.That(project.Contents.Count, Is.EqualTo(2));
        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.CraftItem));
        Assert.That(CraftingOptions.Resolve(world, npc, ContentIds.Plaster).BlockReason,
            Is.EqualTo(CraftBlockReason.Active));
        // A different output does not acquire the generic goal's active flag.
        Assert.That(CraftingOptions.Resolve(world, npc, ContentIds.Rope).IsActive, Is.False);
        engine.Commands.Enqueue(new StopCommand(npc.Id));
        engine.Step();
        Assert.That(project.Contents.Count, Is.EqualTo(2));
        Assert.That(CraftingOptions.Resolve(world, npc, ContentIds.Plaster).IsResume, Is.True);
        engine.Commands.Enqueue(new CraftByOutputCommand(npc.Id, ContentIds.Plaster));
        engine.Step();

        using var save = new MemoryStream();
        using (var writer = new BinaryWriter(save, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        var loaded = TestWorld.CreateEngine(138601);
        DataRecipe();
        save.Position = 0;
        using (var reader = new BinaryReader(save, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(loaded.World, reader);
        var actor = loaded.World.Entities.Npcs[npc.Id];
        Assert.That(actor.Plan.TargetItemDefinitionId, Is.EqualTo(ContentIds.Plaster));
        StepUntil(loaded, () => actor.Plan.Status == PlanStatus.Completed, 600);
        var ground = loaded.World.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Plaster).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(actor.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Plaster), Is.EqualTo(fullPack ? 0 : 1));
            Assert.That(ground.Length, Is.EqualTo(fullPack ? 1 : 0));
            Assert.That(ground.All(o => !o.IsCraftProject && o.Contents.Count == 0), Is.True);
            Assert.That(actor.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Stone), Is.Zero);
        });
    }

    [Test]
    public void DataRecipeRejectsIncompleteBillUnknownOutputAndMissingStationWithoutPayment()
    {
        var world = TestWorld.CreateWorld(138602);
        var npc = PrepareManual(world);
        DataRecipe();
        Strip(world, ContentIds.Stone, ContentIds.Plaster);
        Add(npc, ContentIds.Stone, 1);
        ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, ContentIds.Plaster));
        Assert.That(HasTrace(world, npc.Id, "ManualOrderRejected", "Reason=MissingResources"), Is.True);
        Add(npc, ContentIds.Stone, 1);
        DataRecipe("FixtureStation");
        ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, ContentIds.Plaster));
        Assert.That(HasTrace(world, npc.Id, "ManualOrderRejected", "Reason=NoStation"), Is.True);
        ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "unknown.output"));
        Assert.That(HasTrace(world, npc.Id, "ManualOrderRejected", "Reason=InvalidRecipe"), Is.True);
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Stone), Is.EqualTo(2));
        Assert.That(world.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Plaster), Is.False);
        world.Content.ObjectDefinitions.Remove(ContentIds.Plaster);
        Assert.That(CraftingOptions.Resolve(world, npc, ContentIds.Plaster).BlockReason, Is.EqualTo(CraftBlockReason.InvalidRecipe));
    }

    [Test]
    public void DataRecipeUsesStationFireGateAndCompletesThroughTheStationPlan()
    {
        var engine = TestWorld.CreateEngine(138603);
        engine.Step(); // establish real junction occupancy before a station order
        var world = engine.World;
        var npc = PrepareManual(world);
        DataRecipe("FixtureStation", fire: true);
        Strip(world, ContentIds.Stone, ContentIds.Plaster);
        Add(npc, ContentIds.Stone, 2);
        var definition = new ObjectDefinition { Id = "test.data_station" };
        definition.Tags.Add("FixtureStation");
        definition.Interactions.Add(new InteractionDefinition { Id = "test.craft", Type = InteractionType.Craft, DurationTicks = 24 });
        world.Content.ObjectDefinitions[definition.Id] = definition;
        var station = WorldObjectMutations.SpawnObject(world, definition.Id, npc.Fragment, npc.Tile, npc.CurrentJunction.Value);
        station.CraftJunction = npc.CurrentJunction;
        station.ResourceAmount = 0f;
        Assert.That(CraftingOptions.Resolve(world, npc, ContentIds.Plaster).CanCraft, Is.False);
        station.ResourceAmount = 10f;
        Assert.That(CraftingOptions.Resolve(world, npc, ContentIds.Plaster).CanCraft, Is.True);
        var admission = ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, ContentIds.Plaster));
        Assert.That(admission.Accepted, Is.True);
        station.ResourceAmount = 0f; // fire went out after admission, before payment
        Assert.That(CraftProjectMath.TryBeginCycle(world, npc, GoalType.CraftItem, station, out _), Is.False);
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Stone), Is.EqualTo(2));
        station.ResourceAmount = 10f;
        for (var tick = 0; tick < 600 && !npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.Plaster); tick++)
        {
            engine.Step();
            if (npc.Plan.Status is PlanStatus.Invalid or PlanStatus.Failed) break;
        }
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Plaster), Is.EqualTo(1),
            $"goal={npc.Mind.CurrentGoal} plan={npc.Plan.Status} step={npc.Plan.CurrentStepIndex} " +
            $"exec={npc.Execution.Status}/{npc.Execution.CurrentInteraction} fire={station.ResourceAmount}; " +
            string.Join(" | ", world.Events.Items.Where(e => e.EntityId == npc.Id.Value &&
                (e.Type.Contains("Craft") || e.Type.Contains("Fail") || e.Type.Contains("Manual") ||
                 e.Type.Contains("Plan") || e.Type.Contains("Abort")))
                .TakeLast(20).Select(e => e.Type + ":" + e.Message)));
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
    }

    [Test]
    public void DataRecipeValidatesIngredientsAndExportsWithoutAGoal()
    {
        DataRecipe();
        Assert.That(RecipeCatalog.ByOutput[ContentIds.Plaster].Goal, Is.EqualTo(GoalType.CraftItem));
        Assert.That(RecipeCatalog.ByGoal.ContainsKey(GoalType.CraftItem), Is.False);
        var json = SimDataFile.ExportJson();
        RecipeCatalog.ResetToDefaults();
        Assert.That(SimDataFile.ApplyJson(json), Is.True);
        Assert.That(RecipeCatalog.ByOutput[ContentIds.Plaster].BaseWorkTicks, Is.EqualTo(72));
        Assert.Throws<System.ArgumentException>(() => RecipeCatalog.Override(ContentIds.Plaster,
            new[] { new RecipeIngredient(ContentIds.Stone, 1), new RecipeIngredient(ContentIds.Stone, 1) }, false));
        Assert.Throws<System.ArgumentException>(() => RecipeCatalog.Override(ContentIds.Plaster,
            new[] { new RecipeIngredient(ContentIds.Stone, 0) }, false));
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    [TestCase(false)]
    [TestCase(true)]
    public void RequestIdReplaysDuringWorkAndAfterCompletionIncludingSaveReload(bool reload)
    {
        var engine = TestWorld.CreateEngine(138610);
        engine.Step();
        var world = engine.World;
        var npc = PrepareManual(world);
        DataRecipe();
        Strip(world, ContentIds.Stone, ContentIds.Plaster);
        npc.Inventory.Items.Clear();
        Add(npc, ContentIds.Stone, 6); // enough to expose an accidental second payment
        var command = new CraftByOutputCommand(npc.Id, ContentIds.Plaster, "order-1");
        var first = engine.ApplyManualCommand(command, "player");
        Assert.That(first.Accepted, Is.True);
        StepUntil(engine, () => world.Entities.Objects.Values.Any(o =>
            o.DefinitionId == ContentIds.Plaster && o.IsCraftProject && o.CraftWorkDone >= 24), 300);
        var project = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.Plaster);
        var progress = (project.Id, project.CraftWorkDone, npc.Plan.CurrentStepIndex);
        Assert.That(engine.ApplyManualCommand(command, "player"), Is.EqualTo(first));
        Assert.That((project.Id, project.CraftWorkDone, npc.Plan.CurrentStepIndex), Is.EqualTo(progress));
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Stone), Is.EqualTo(4));
        if (reload) Reload();
        Assert.That(engine.ApplyManualCommand(command, "player"), Is.EqualTo(first));
        StepUntil(engine, () => npc.Plan.Status == PlanStatus.Completed, 600);
        if (reload) Reload();
        Assert.That(engine.ApplyManualCommand(command, "player"), Is.EqualTo(first));
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Plaster), Is.EqualTo(1));
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Stone), Is.EqualTo(4));
        Assert.That(world.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Plaster), Is.False);
        Assert.That(engine.ApplyManualCommand(new CraftByOutputCommand(npc.Id, ContentIds.Rope, "order-1"), "player")
            .Reason, Is.EqualTo("CraftRequestConflict"));
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
        // §138.5 stays intact: an explicit unkeyed repeat makes another thing.
        Assert.That(engine.ApplyManualCommand(new CraftByOutputCommand(npc.Id, ContentIds.Plaster)).Accepted, Is.True);
        Assert.That(engine.ApplyManualCommand(new CraftByOutputCommand(npc.Id, ContentIds.Plaster)).Accepted, Is.False);
        StepUntil(engine, () => npc.Plan.Status == PlanStatus.Completed, 600);
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Plaster), Is.EqualTo(2));
        Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.Stone), Is.EqualTo(2));
        Assert.That(world.CraftRequests[npc.Id.Value].Receipts.Count, Is.EqualTo(1));

        void Reload()
        {
            using var save = new MemoryStream();
            using (var writer = new BinaryWriter(save, System.Text.Encoding.UTF8, true))
                WorldSaveSerializer.Write(world, writer);
            engine = TestWorld.CreateEngine(world.Seed);
            DataRecipe();
            save.Position = 0;
            using var reader = new BinaryReader(save);
            WorldSaveSerializer.Read(engine.World, reader);
            world = engine.World;
            npc = world.Entities.Npcs[npc.Id];
        }
    }

    [Test]
    public void RequestIdKeepsRefusalsAndScopesKeysByOwnerAndNpc()
    {
        var world = TestWorld.CreateWorld(138611);
        var npc = PrepareManual(world);
        DataRecipe();
        Strip(world, ContentIds.Stone, ContentIds.Plaster);
        npc.Inventory.Items.Clear();
        var command = new CraftByOutputCommand(npc.Id, ContentIds.Plaster, "refused");
        var first = ManualCommandExecutor.Apply(world, command, "owner-1");
        Assert.That(first.Reason, Is.EqualTo("MissingResources"));
        Add(npc, ContentIds.Stone, 2);
        Assert.That(ManualCommandExecutor.Apply(world, command, "owner-1"), Is.EqualTo(first));
        Assert.That(ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "unknown", "refused"), "owner-1")
            .Reason, Is.EqualTo("CraftRequestConflict"));
        Assert.That(ManualCommandExecutor.Apply(world, command, "owner-2").Accepted, Is.True);
        ManualCommandExecutor.Apply(world, new StopCommand(npc.Id));
        Assert.That(ManualCommandExecutor.Apply(world, command, "owner-2").Accepted, Is.True);
        Assert.That(npc.Plan.Status, Is.Not.EqualTo(PlanStatus.Active), "Replay must not resume a cancelled craft");
        var other = world.Entities.Npcs.Values.First(n => n.Id != npc.Id);
        ManualCommandExecutor.Apply(world, new CraftByOutputCommand(other.Id, "unknown", "refused"), "owner-1");
        Assert.That(world.CraftRequests[other.Id.Value].Receipts.Single().OutputId, Is.EqualTo("unknown"));
        npc.Health = 0;
        Assert.That(ManualCommandExecutor.Apply(world, command, "owner-2").Accepted, Is.True);
        Assert.That(world.CraftRequests[npc.Id.Value].Receipts.Count, Is.EqualTo(2));
    }

    [Test]
    public void RequestJournalKeepsTheNewestWindowAndRejectsMalformedIds()
    {
        var world = TestWorld.CreateWorld(138612);
        var npc = PrepareManual(world);
        foreach (var key in new[] { "bad key", new string('a', 97), "не-ascii" })
            Assert.That(ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "unknown", key)).Reason,
                Is.EqualTo("InvalidCraftRequest"));
        Assert.That(world.CraftRequests, Is.Empty);
        ManualCommandAdmission first = default;
        for (var i = 0; i < CraftRequestLedger.Capacity; i++)
        {
            var result = ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "unknown", "key-" + i));
            if (i == 0) first = result;
        }
        Assert.That(first.Reason, Is.EqualTo("InvalidRecipe"));
        Assert.That(ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "unknown", "key-0")), Is.EqualTo(first),
            "inside the window a retry is answered from the journal");
        // A full journal never stops a NEW key: the oldest receipt makes room.
        Assert.That(ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "unknown", "overflow")).Reason,
            Is.EqualTo("InvalidRecipe"));
        var receipts = world.CraftRequests[npc.Id.Value].Receipts;
        Assert.That(receipts.Count, Is.EqualTo(CraftRequestLedger.Capacity));
        Assert.That(receipts.Select(r => r.RequestId), Does.Not.Contain("key-0").And.Contain("key-1").And.Contain("overflow"));
        // A retry of a key still in the window keeps its stored outcome after eviction of others.
        Assert.That(ManualCommandExecutor.Apply(world, new CraftByOutputCommand(npc.Id, "other", "key-1")).Reason,
            Is.EqualTo("CraftRequestConflict"));
    }

    [TestCase(81)]
    [TestCase(82)]
    public void CraftRequestSaveVersionPreservesOnlySupportedReceipts(int version)
    {
        var world = TestWorld.CreateWorld(138613);
        var npc = PrepareManual(world);
        var command = new CraftByOutputCommand(npc.Id, "unknown", "saved-refusal");
        var first = ManualCommandExecutor.Apply(world, command, "owner");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using var reader = new BinaryReader(bytes);
        WorldSaveSerializer.Read(restored, reader);
        Assert.That(bytes.Position, Is.EqualTo(bytes.Length));
        Assert.That(restored.CraftRequests.Count, Is.EqualTo(version >= 82 ? 1 : 0));
        if (version >= 82)
        {
            Assert.That(restored.CraftRequests[npc.Id.Value].Receipts.Single().Owner, Is.EqualTo("owner"));
            Assert.That(ManualCommandExecutor.Apply(restored, command, "owner"), Is.EqualTo(first));
        }
    }

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
