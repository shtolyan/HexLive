using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class TorchPlacementTests
{
    [TestCase(ContentIds.Torch, true)]
    [TestCase(ContentIds.YuccaSeed, true)]
    [TestCase(ContentIds.HerbSeed, true)]
    [TestCase(ContentIds.Coconut, true)]
    [TestCase(ContentIds.Stick, false)]
    [TestCase(GearCatalog.Saw, false)]
    public void PlacementWhitelistKeepsSeedsAndAddsOnlyTheTorch(string definitionId, bool allowed) =>
        Assert.That(InventoryPlacementMath.CanPlace(definitionId), Is.EqualTo(allowed));

    [Test]
    public void PlacingUsesTheTimedPlantGestureAndPreservesThePhysicalTorchesRemainingLifetime()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        foreach (var actor in world.Entities.Npcs.Values)
            engine.ApplyManualCommand(new SetManualControlCommand(actor.Id, true));
        engine.Step();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = 1f;
        npc.Inventory.Items.Clear();
        var torch = new ItemInstance(ContentIds.Torch) { Durability = 0.42f, Wetness = 0.21f };
        npc.Inventory.Items.Add(torch);
        var target = SpatialQueries.GetPassableNeighbors(world, npc.CurrentJunction!.Value)
            .Select(id => world.Junctions.Items[id]).First(j =>
                PlantGrowthSystem.CanPlantAt(world, j.Id, j.Tiles[0], npc.Id));
        var admission = engine.ApplyManualCommand(new PlantInventorySeedCommand(npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, npc.Inventory.Items.Count - 1, ContentIds.Torch),
            target.Id, target.Tiles[0]));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var sawPlant = false;
        for (var tick = 0; tick < 600 && npc.Inventory.Items.Contains(torch); tick++)
        {
            engine.Step();
            sawPlant |= npc.Execution.CurrentInteraction == InteractionType.Plant;
        }
        Assert.That(sawPlant, Is.True);
        Assert.That(world.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.Torch), Is.EqualTo(1), "Exactly one placed physical torch must exist.");
        var placed = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.Torch);
        Assert.That(placed.Junctions, Does.Contain(target.Id));
        Assert.That(placed.Variant, Is.EqualTo("planted"));
        Assert.That(placed.Durability, Is.InRange(0.4f, 0.42f));
        Assert.That(npc.Inventory.Items.Contains(torch), Is.False);

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        buffer.Position = 0;
        var restored = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(restored, reader);
        Assert.That(restored.Entities.Objects[placed.Id].Variant, Is.EqualTo("planted"));
        Assert.That(restored.Entities.Objects[placed.Id].Durability, Is.EqualTo(placed.Durability));

        admission = engine.ApplyManualCommand(new InteractCommand(npc.Id, placed.Id, InteractionType.PickUp));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var tick = 0; tick < 600 && !npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.Torch); tick++) engine.Step();
        Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.Torch), Is.True,
            "The ordinary pickup order must return the placed torch to the backpack. Goal=" + npc.Mind.CurrentGoal + " status=" + npc.Plan.Status);
        Assert.That(npc.Inventory.Items.Single(i => i.DefinitionId == ContentIds.Torch).Durability,
            Is.InRange(0.39f, 0.42f));
    }

    [Test]
    public void ConstructionEntryUsesTheSamePhysicalTorchAndRequiresTheWholeRecipeBill()
    {
        var world = TestWorld.CreateWorld();
        Assert.That(BuildCatalogDefinition.TryGet(ContentIds.Torch, out var entry), Is.True);
        Assert.That(entry.Mode, Is.EqualTo(BuildCatalogMode.Furniture));
        var site = BuildSiteView.FurnitureBillPreview(ContentIds.Torch);
        Assert.That(BuildSiteView.NeedsHammer(world, site), Is.False);
        var rows = BuildSiteView.Materials(site);
        foreach (var ingredient in RecipeCatalog.ByOutput[ContentIds.Torch].Inputs)
            Assert.That(rows.Single(r => r.DefinitionId == ingredient.Id).Required, Is.EqualTo(ingredient.Count));
        site.Contents.Add(new ItemInstance(ContentIds.Stick));
        site.Contents.Add(new ItemInstance(ContentIds.Rope));
        Assert.That(BuildSiteMath.IsStocked(site), Is.False, "Cloth must never become a free construction input.");
        site.Contents.Add(new ItemInstance(ContentIds.Cloth));
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
    }
    [Test]
    public void ConstructionOrderBuildsAndConsumesClothWithoutRequiringAHammer()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        foreach (var actor in world.Entities.Npcs.Values)
            engine.ApplyManualCommand(new SetManualControlCommand(actor.Id, true));
        engine.Step();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var other in world.Entities.Npcs.Values.Where(n => n.Id != npc.Id))
        {
            var remote = world.Junctions.Items.Values.First(j => j.Tiles.Count > 0 &&
                HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) >= 12 &&
                !j.Blocked && SpatialQueries.IsJunctionFree(world, j.Id));
            if (other.CurrentJunction is { } old)
            {
                SpatialMutations.FreeJunction(world, old, other.Id);
                SpatialMutations.ReleaseJunctionReservation(world, old, other.Id);
            }
            var previousTile = other.Tile;
            other.Position = remote.WorldPosition;
            other.Tile = remote.Tiles[0];
            other.Fragment = remote.Fragment;
            other.CurrentJunction = remote.Id;
            SpatialMutations.MoveEntityToTile(world, other.Id, previousTile, other.Tile);
            SpatialMutations.OccupyJunction(world, remote.Id, other.Id);
        }
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = 1f;
        npc.Inventory.Items.Clear();
        foreach (var id in new[] { ContentIds.Stick, ContentIds.Rope, ContentIds.Cloth })
            npc.Inventory.Items.Add(new ItemInstance(id));
        var tile = world.Tiles.Items.Keys.First(t =>
            HexSpatialMath.HexDistance(npc.Tile, t) >= 2 && HexSpatialMath.HexDistance(npc.Tile, t) <= 4 &&
            StructurePlacement.HexFreeForBuild(world, t) &&
            StructurePlacement.CenterJunction(world, t) is { } j &&
            Connectivity.Reachable(world, npc.CurrentJunction!.Value, j));
        var admission = engine.ApplyManualCommand(new PlaceFurnitureSiteCommand(ContentIds.Torch, tile, 0f));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var site = world.Entities.Objects.Values.Single(o => o.BuildProduct == ContentIds.Torch);
        admission = engine.ApplyManualCommand(new InteractCommand(npc.Id, site.Id, InteractionType.Build));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var i = 0; i < 1400 && world.Entities.Objects.ContainsKey(site.Id); i++)
        {
            engine.Step();
            if (npc.Mind.CurrentGoal == HexLive.Simulation.AI.GoalType.None &&
                npc.Plan.Status == HexLive.Simulation.AI.PlanStatus.Invalid) break;
        }
        if (world.Entities.Objects.ContainsKey(site.Id))
            TestContext.WriteLine("SITE: contents=" + string.Join(",", site.Contents.Select(i => i.DefinitionId)) +
                " installed=" + string.Join(",", site.InstalledBuildMaterials ?? System.Array.Empty<int>()) +
                " goal=" + npc.Mind.CurrentGoal + " plan=" + npc.Plan.Status + " exec=" + npc.Execution.Status +
                " movement=" + npc.Movement.Status + " step=" + npc.Plan.CurrentStepIndex + "/" + npc.Plan.Steps.Count +
                " inventory=" + string.Join(",", npc.Inventory.Items.Select(i => i.DefinitionId)) +
                "\n" + string.Join("\n", world.Events.Items.Where(e => e.EntityId == npc.Id.Value && e.Type != "PerceivedObject").TakeLast(20).Select(e => e.Type + " " + e.Message)));
        Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False,
            "A complete torch bill must finish the ordinary construction session without a hammer.");
        var placed = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.Torch);
        Assert.That(placed.Tile, Is.EqualTo(tile));
        Assert.That(placed.Variant, Is.EqualTo("planted"));
        Assert.That(npc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.Cloth), Is.False);
        Assert.That(placed.Contents, Is.Empty, "Construction inputs must be consumed, not duplicated inside the torch.");
    }

    [TestCase(87)]
    [TestCase(88)]
    [TestCase(89)]
    public void ObjectVariantTailKeepsOlderSaveLayoutsAndV88FuelRolesReadable(int version)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stick) { CampfireFuelRole = version >= 88 ? CampfireFuelRole.Discard : CampfireFuelRole.None });
        var obj = world.Entities.Objects.Values.First();
        obj.Variant = "planted";
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(restored, reader);
        Assert.That(bytes.Position, Is.EqualTo(bytes.Length));
        Assert.That(restored.Entities.Npcs[npc.Id].Inventory.Items.Single().CampfireFuelRole,
            Is.EqualTo(version >= 88 ? CampfireFuelRole.Discard : CampfireFuelRole.None));
        if (version >= 89) Assert.That(restored.Entities.Objects[obj.Id].Variant, Is.EqualTo("planted"));
    }

}
