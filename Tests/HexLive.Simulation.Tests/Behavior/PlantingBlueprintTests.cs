using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
public sealed class PlantingBlueprintTests
{
    [Test]
    public void MultiplePlantingBlueprintsShareOneHexAtTheirExactOffCentrePoints()
    {
        var (engine,npc,centreSite) = Place(ContentIds.YuccaSeed);
        var world = engine.World;
        var points = world.Tiles.Items[centreSite.Tile].Junctions
            .Where(j => !centreSite.Junctions.Contains(j) && PlantGrowthSystem.CanPlantAt(world,j,centreSite.Tile))
            .Take(3).ToArray();
        Assert.That(points.Length, Is.EqualTo(3));
        foreach (var point in points)
        {
            var accepted = engine.ApplyManualCommand(new PlacePlantingSiteCommand(ContentIds.HerbSeed, centreSite.Tile, point));
            Assert.That(accepted.Accepted, Is.True, accepted.Reason);
            Assert.That(world.Entities.Objects.Values.Any(o => o.BuildProduct == ContentIds.HerbSeed && o.Junctions.Contains(point)), Is.True);
        }
        var count = world.Entities.Objects.Count;
        var duplicate = engine.ApplyManualCommand(new PlacePlantingSiteCommand(ContentIds.HerbSeed, centreSite.Tile, points[0]));
        Assert.That(duplicate.Accepted, Is.False);
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(count));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.HerbSeed));
        var site = world.Entities.Objects.Values.Single(o => o.BuildProduct == ContentIds.HerbSeed && o.Junctions.Contains(points[0]));
        Assert.That(engine.ApplyManualCommand(new InteractCommand(npc.Id,site.Id,InteractionType.Build)).Accepted, Is.True);
        for (var tick = 0; tick < 1000 && world.Entities.Objects.ContainsKey(site.Id); tick++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False);
        Assert.That(world.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.HerbSprout && o.Junctions.Contains(points[0])), Is.True);
    }

    [Test]
    public void WorldBuildMenuAcceptsSeedsUnderTheBlueprintAndUsesOneGroundInstance()
    {
        var (engine, npc, center) = Place(ContentIds.YuccaSeed);
        var world = engine.World;
        var point = world.Tiles.Items[center.Tile].Junctions
            .First(j => world.Junctions.Items[j].Tiles.Count == 1 &&
                PlantGrowthSystem.CanPlantAt(world, j, center.Tile));
        var first = WorldObjectMutations.SpawnObject(world, ContentIds.HerbSeed, npc.Fragment, center.Tile, point);
        var second = WorldObjectMutations.SpawnObject(world, ContentIds.HerbSeed, npc.Fragment, center.Tile, point);
        var otherCrop = WorldObjectMutations.SpawnObject(world, ContentIds.YuccaSeed, npc.Fragment, center.Tile, point);
        var key = FurniturePlacement.KeyOf(world.Junctions.Items[point].WorldPosition);
        var placed = engine.ApplyManualCommand(new PlaceFurnitureCommand(ContentIds.HerbSeed, key.XKey, key.YKey, 0));
        Assert.That(placed.Accepted, Is.True, placed.Reason);
        var site = world.Entities.Objects.Values.Single(o => o.BuildProduct == ContentIds.HerbSeed &&
            o.Junctions.Contains(point));

        var accepted = engine.ApplyManualCommand(new InteractCommand(npc.Id, site.Id, InteractionType.Build));
        Assert.That(accepted.Accepted, Is.True, accepted.Reason);
        Assert.That(npc.Plan.Steps[^1].TargetObject, Is.EqualTo(first.Id));
        FinishAtPlannedPoint(world, npc);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False);
            Assert.That(world.Entities.Objects.ContainsKey(first.Id), Is.False);
            Assert.That(world.Entities.Objects.ContainsKey(second.Id), Is.True);
            Assert.That(world.Entities.Objects.ContainsKey(otherCrop.Id), Is.True);
            Assert.That(world.Entities.Objects.Values.Any(o =>
                o.DefinitionId == ContentIds.HerbSprout && o.Junctions.Contains(point)), Is.True);
        });
    }

    [Test]
    public void AutonomousGardenCanPlantTheSeedAlreadyOnItsBlueprint()
    {
        var (engine, npc, site) = Place(ContentIds.HerbSeed);
        var world = engine.World;
        var seed = WorldObjectMutations.SpawnObject(
            world, ContentIds.HerbSeed, npc.Fragment, site.Tile, site.Junctions[0]);
        npc.Mind.ManualControl = false;
        npc.Mind.CurrentGoal = GoalType.TendGarden;
        npc.Plan.Status = PlanStatus.None;
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject
        {
            Id = site.Id, DefinitionId = site.DefinitionId, Tile = site.Tile, IsReachable = true
        });

        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(site.Id));
        Assert.That(npc.Plan.Steps[^1].TargetObject, Is.EqualTo(seed.Id));
        FinishAtPlannedPoint(world, npc);
        Assert.That(world.Entities.Objects.ContainsKey(seed.Id), Is.False);
        Assert.That(world.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.HerbSprout &&
            o.Junctions.Contains(site.Junctions[0])), Is.True);
    }

    [Test]
    public void OneBuildPlantsTheLocalBlueprintsUntilMatchingSeedsRunOut()
    {
        var (engine, npc, first) = Place(ContentIds.YuccaSeed);
        var world = engine.World;
        var additional = world.Tiles.Items[first.Tile].Junctions
            .Where(j => PlantGrowthSystem.CanPlantAt(world, j, first.Tile))
            .Take(2).Select(j => PlacePoint(engine, ContentIds.YuccaSeed, first.Tile, j)).ToArray();
        Assert.That(additional, Has.Length.EqualTo(2));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));

        StartAndRunBatch(engine, npc, first);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(first.Id), Is.False);
            Assert.That(additional.Count(site => world.Entities.Objects.ContainsKey(site.Id)), Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.YuccaSeed), Is.Zero);
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(npc.Plan.PlantingBatchCenter, Is.Null);
        });
    }

    [Test]
    public void BuildBatchUsesNeighboringTilesButKeepsTheRadiusAtTheClickedTile()
    {
        var (engine, npc, first) = Place(ContentIds.YuccaSeed);
        var world = engine.World;
        var centers = world.Tiles.Items.Values.Where(tile =>
            HexSpatialMath.HexDistance(first.Tile, tile.Coord) <= 2 &&
            StructurePlacement.CenterJunction(world, tile.Coord) is { } anchor &&
            PlantGrowthSystem.CanPlantAt(world, anchor, tile.Coord) &&
            Connectivity.Reachable(world, npc.CurrentJunction.Value, anchor)).ToArray();
        var innerTile = centers.First(tile => HexSpatialMath.HexDistance(first.Tile, tile.Coord) == 1 &&
            centers.Any(other => HexSpatialMath.HexDistance(first.Tile, other.Coord) == 2 &&
                HexSpatialMath.HexDistance(tile.Coord, other.Coord) == 1));
        var outerTile = centers.First(tile => HexSpatialMath.HexDistance(first.Tile, tile.Coord) == 2 &&
            HexSpatialMath.HexDistance(innerTile.Coord, tile.Coord) == 1);
        var inner = PlacePoint(engine, ContentIds.HerbSeed, innerTile.Coord,
            StructurePlacement.CenterJunction(world, innerTile.Coord).Value);
        var outer = PlacePoint(engine, ContentIds.YuccaSeed, outerTile.Coord,
            StructurePlacement.CenterJunction(world, outerTile.Coord).Value);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.HerbSeed));

        StartAndRunBatch(engine, npc, first);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(first.Id), Is.False);
            Assert.That(world.Entities.Objects.ContainsKey(inner.Id), Is.False);
            Assert.That(world.Entities.Objects.ContainsKey(outer.Id), Is.True);
            Assert.That(npc.Inventory.Items.Count(i => i.DefinitionId == ContentIds.YuccaSeed), Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.HerbSeed), Is.False);
        });
    }

    [Test]
    public void NewManualOrderReplacesThePlantingBatchWithoutSpendingTheNextSeed()
    {
        var (engine, npc, first) = Place(ContentIds.YuccaSeed);
        var world = engine.World;
        var point = world.Tiles.Items[first.Tile].Junctions.First(j => PlantGrowthSystem.CanPlantAt(world, j, first.Tile));
        var second = PlacePoint(engine, ContentIds.YuccaSeed, first.Tile, point);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.YuccaSeed));
        var remaining = new ItemInstance(ContentIds.YuccaSeed);
        npc.Inventory.Items.Add(remaining);
        Assert.That(engine.ApplyManualCommand(new InteractCommand(npc.Id, first.Id, InteractionType.Build)).Accepted, Is.True);
        FinishAtPlannedPoint(world, npc);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(second.Id));

        var move = engine.ApplyManualCommand(new MoveToCommand(npc.Id, npc.Position));
        Assert.That(move.Accepted, Is.True, move.Reason);
        for (var i = 0; i < 80; i++) engine.Step();

        Assert.That(world.Entities.Objects.ContainsKey(second.Id), Is.True);
        Assert.That(npc.Inventory.Items.Contains(remaining), Is.True);
    }

    private static WorldObjectState PlacePoint(SimulationEngine engine, string seedId, TileCoord tile, JunctionId point)
    {
        var placed = engine.ApplyManualCommand(new PlacePlantingSiteCommand(seedId, tile, point));
        Assert.That(placed.Accepted, Is.True, placed.Reason);
        return engine.World.Entities.Objects.Values.Single(o => o.BuildProduct == seedId && o.Junctions.Contains(point));
    }

    private static void StartAndRunBatch(SimulationEngine engine, NPCState npc, WorldObjectState first)
    {
        var accepted = engine.ApplyManualCommand(new InteractCommand(npc.Id, first.Id, InteractionType.Build));
        Assert.That(accepted.Accepted, Is.True, accepted.Reason);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(first.Id));
        for (var i = 0; i < 1500 && npc.Plan.Status == PlanStatus.Active; i++) engine.Step();
        Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
    }

    [Test]
    public void PointPlantingRejectsInvalidTileAndNonSeedCatalogWithoutMutation()
    {
        var (engine,npc,site) = Place(ContentIds.YuccaSeed);
        var before = engine.World.Entities.Objects.Count;
        foreach (var command in new[] {
            new PlacePlantingSiteCommand(ContentIds.HerbSeed, new TileCoord(999,999), site.Junctions[0]),
            new PlacePlantingSiteCommand(ContentIds.BedBasic, site.Tile, site.Junctions[0]) })
            Assert.That(engine.ApplyManualCommand(command).Accepted, Is.False);
        Assert.That(engine.World.Entities.Objects.Count, Is.EqualTo(before));
    }

    private static (SimulationEngine engine, NPCState npc, WorldObjectState site) Place(string seed)
    {
        var engine = TestWorld.CreateEngine(); engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var resident in world.Entities.Npcs.Values)
            engine.ApplyManualCommand(new SetManualControlCommand(resident.Id, true));
        foreach (var other in world.Entities.Npcs.Values.Where(n=>n.Id!=npc.Id).ToArray())
        {
            if(other.CurrentJunction is { } occupied) SpatialMutations.FreeJunction(world,occupied,other.Id);
            world.Entities.Npcs.Remove(other.Id);
        }
        npc.Inventory.Items.Clear();
        npc.Needs.Hunger = npc.Needs.Thirst = 0;
        npc.Needs.Energy = npc.Needs.Stamina = 1;
        var home = world.FactionHomes[npc.Faction];
        var tile = world.Tiles.Items.Keys.OrderBy(t => HexSpatialMath.HexDistance(home,t))
            .First(t => HexSpatialMath.HexDistance(home,t) <= 4 &&
                StructurePlacement.HexFreeForBuild(world,t) &&
                StructurePlacement.CenterJunction(world,t) is { } j &&
                PlantGrowthSystem.CanPlantAt(world,j,t));
        var admission = engine.ApplyManualCommand(new PlaceFurnitureSiteCommand(seed,tile,0));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var site = world.Entities.Objects.Values.Single(o => o.Tile.Equals(tile) && o.BuildProduct == seed);
        Assert.That(BuildSiteMath.IsPlantingSite(site), Is.True);
        Assert.That(ManualBuildSession.Eligible(site), Is.False);
        return (engine,npc,site);
    }

    [TestCase(ContentIds.Coconut,ContentIds.PalmSprout)]
    [TestCase(ContentIds.YuccaSeed,ContentIds.YuccaSprout)]
    [TestCase(ContentIds.HerbSeed,ContentIds.HerbSprout)]
    public void BuildMenuPlacementAndOneManualBuildUseTheOrdinarySeedAnimation(string seedId,string sproutId)
    {
        var (engine,npc,site) = Place(seedId); var world=engine.World;
        var seed = new ItemInstance(seedId);npc.Inventory.Items.Add(seed);
        var admission=engine.ApplyManualCommand(new InteractCommand(npc.Id,site.Id,InteractionType.Build));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var sawPlant=false;
        for(var i=0;i<600 && world.Entities.Objects.ContainsKey(site.Id) && npc.Plan.Status == PlanStatus.Active;i++)
        {
            engine.Step();
            sawPlant |= npc.Execution.CurrentInteraction == InteractionType.Plant;
        }
        if (!sawPlant) TestContext.WriteLine($"goal={npc.Mind.CurrentGoal} plan={npc.Plan.Status} current={npc.CurrentJunction} target={npc.Plan.TargetJunctionId} movement={npc.Movement.Status} exec={npc.Execution.Status} tick={world.Tick} " +
            string.Join("\n", world.Events.Items.Where(e=>e.EntityId==npc.Id.Value && e.Type!="PerceivedObject").TakeLast(12).Select(e=>e.Type+": "+e.Message)));
        Assert.Multiple(() =>
        {
            Assert.That(sawPlant, Is.True);
            Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False);
            Assert.That(npc.Inventory.Items.Contains(seed), Is.False);
            Assert.That(world.Entities.Objects.Values.Any(o=>o.DefinitionId==sproutId &&
                o.Junctions.Contains(site.Junctions[0])), Is.True);
        });
    }

    [TestCase(ContentIds.Coconut)]
    [TestCase(ContentIds.YuccaSeed)]
    [TestCase(ContentIds.HerbSeed)]
    public void AutonomousGardenSelectsTheExplicitBlueprintBeforeItsOrdinaryTarget(string seedId)
    {
        var (engine,npc,site)=Place(seedId);var world=engine.World;
        npc.Mind.ManualControl=false;
        npc.Mind.CurrentGoal=GoalType.TendGarden; npc.Plan.Status=PlanStatus.None;
        npc.Inventory.Items.Add(new ItemInstance(seedId));
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject {Id=site.Id,DefinitionId=site.DefinitionId,
            Tile=site.Tile,IsReachable=true});
        Assert.That(GardenMath.TryChooseTask(world,npc,out var chosen), Is.True);
        Assert.That(chosen.Site, Is.SameAs(site));
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(site.Id));
        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(site.Junctions[0]));
        Assert.That(npc.Plan.Steps.Last().Type, Is.EqualTo(PlanStepType.PlantInventorySeed));
        Assert.That(DecisionSystem.FindBuildSite(npc,world), Is.Null,
            "The generic zero-bill furniture executor must not create a free seed.");
    }

    [Test]
    public void MissingSeedKeepsTheBlueprintAndCannotBuildAPlantForFree()
    {
        var (engine,npc,site)=Place(ContentIds.YuccaSeed);var world=engine.World;
        var admission=engine.ApplyManualCommand(new InteractCommand(npc.Id,site.Id,InteractionType.Build));
        Assert.That(admission.Accepted, Is.False);
        Assert.That(admission.Reason, Is.EqualTo("NoMaterials"));
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject {Id=site.Id,DefinitionId=site.DefinitionId,
            Tile=site.Tile,IsReachable=true});
        Assert.That(GardenMath.TryChooseTask(world,npc,out _), Is.False);
        Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.True);
    }

    [Test]
    public void SeedErrandFetchesTheRealGroundSeedAndBlueprintSurvivesSaveLoad()
    {
        var (engine,npc,site)=Place(ContentIds.YuccaSeed);var world=engine.World;
        var point=SpatialQueries.GetPassableNeighbors(world,npc.CurrentJunction.Value)
            .First(j=>world.Entities.Objects.Values.All(o=>!o.Junctions.Contains(j)));
        var source=WorldObjectMutations.SpawnObject(world,ContentIds.YuccaSeed,npc.Fragment,
            world.Junctions.Items[point].Tiles[0],point);
        npc.Perception.Objects.Clear();
        foreach(var obj in new[]{site,source}) npc.Perception.Objects.Add(new PerceivedObject {
            Id=obj.Id,DefinitionId=obj.DefinitionId,Tile=obj.Tile,IsReachable=true,
            AvailableInteractions={InteractionType.PickUp}});
        Assert.That(GardenMath.TryChooseTask(world,npc,out var chosen), Is.True);
        Assert.That(chosen.Site, Is.SameAs(site));
        Assert.That(chosen.Source.Id, Is.EqualTo(source.Id));
        Assert.That(chosen.Interaction, Is.EqualTo(InteractionType.PickUp));
        using var bytes=new MemoryStream();
        using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Write(world,writer);
        bytes.Position=0;
        var copy=TestWorld.CreateWorld();
        using(var reader=new BinaryReader(bytes)) WorldSaveSerializer.Read(copy,reader);
        Assert.That(copy.Entities.Objects[site.Id].BuildProduct, Is.EqualTo(ContentIds.YuccaSeed));
        Assert.That(copy.Entities.Objects[site.Id].OwnerFaction, Is.EqualTo(npc.Faction));
        npc.Mind.ManualControl=false;
        npc.Mind.CurrentGoal=GoalType.TendGarden;
        npc.Plan.Status=PlanStatus.None;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(source.Id));
        FinishAtPlannedPoint(world,npc);
        Assert.That(world.Entities.Objects.ContainsKey(source.Id), Is.False);
        Assert.That(npc.Inventory.Items.Any(i=>i.DefinitionId==ContentIds.YuccaSeed), Is.True);
        npc.Mind.CurrentGoal=GoalType.TendGarden; npc.Plan.Status=PlanStatus.None;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(site.Id));
        FinishAtPlannedPoint(world,npc);
        Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False);
        Assert.That(npc.Inventory.Items.Any(i=>i.DefinitionId==ContentIds.YuccaSeed), Is.False);
        Assert.That(world.Entities.Objects.Values.Any(o=>o.DefinitionId==ContentIds.YuccaSprout &&
            o.Junctions.Contains(site.Junctions[0])), Is.True);
    }

    private static void FinishAtPlannedPoint(WorldState world,NPCState npc)
    {
        var target=npc.Plan.TargetJunctionId.Value;
        if(npc.CurrentJunction is { } old) SpatialMutations.FreeJunction(world,old,npc.Id);
        npc.CurrentJunction=target; npc.Position=world.Junctions.Items[target].WorldPosition;
        npc.Tile=world.Junctions.Items[target].Tiles[0];
        npc.Movement.IsMoving=false; npc.Movement.JunctionPath.Clear();
        npc.Movement.SetStatus(HexLive.Simulation.Navigation.MovementStatus.Arrived);
        var execution=new ExecutionSystem();execution.Run(world);
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
        world.Tick=npc.Execution.EndTick;execution.Run(world);
    }
}
