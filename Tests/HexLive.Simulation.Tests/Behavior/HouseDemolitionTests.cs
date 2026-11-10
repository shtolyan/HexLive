using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;
public sealed class HouseDemolitionTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void PaidHouseWallStaysUntilWorkThenReturnsHalfWithoutResettingNeighbours(bool reload, bool tracked)
    {
        const int seed = 12345;
        var engine = TestWorld.CreateEngine(seed); var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(o.BuildProduct));
        var wall = BuildingRules.ArchitectureObjects(world, hut).First(o => o.DefinitionId == "architecture.wall.wood");
        var oldId = wall.Id; var state = wall.ArchitectureElements[0];
        if (tracked)
        {
            hut.Contents.Clear();
            hut.InstalledBuildMaterials = new int[BuildSiteMath.AllMaterials.Length];
            foreach (var module in BuildingRules.Elements(world, hut))
            {
                var counts = new[] { module.DeliveredSticks, module.DeliveredBoards, module.DeliveredRope, module.DeliveredLeaves };
                var materials = new[] { ContentIds.Stick, ContentIds.Board, ContentIds.Rope, ContentIds.PalmLeaf };
                for (var i = 0; i < materials.Length; i++)
                {
                    hut.InstalledBuildMaterials[System.Array.IndexOf(BuildSiteMath.AllMaterials, materials[i])] += counts[i];
                    for (var n = 0; n < counts[i]; n++) hut.Contents.Add(new HexLive.Simulation.Agents.ItemInstance(materials[i]) { Wetness = .37f });
                }
            }
        }
        var blocked = wall.BlockedJunctions.ToArray();
        Assert.That(blocked, Is.Not.Empty, "Use an actual raised, blocking wall.");
        var neighbours = BuildingRules.ArchitectureObjects(world, hut).Where(o => o.Id != oldId).ToDictionary(o => o.Id, o => o.ArchitectureElements[0].Clone());
        BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var draft, out _);
        draft.Elements.RemoveAll(e => BlueprintBuildingPlan.SlotKey(e) == state.SlotKey);
        var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(draft, false)));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.Entities.Objects[oldId], Is.SameAs(wall));
        Assert.That(FreeArchitectureRules.IsDemolitionSite(wall), Is.True);
        Assert.That(blocked.All(j => world.Junctions.Items[j].Blocked), Is.True, "An edit must not remove physical wall blocking before work.");
        Assert.That(BuildSiteMath.IsStocked(wall), Is.True);
        Assert.That(BuildSiteMath.NeedsHammer(world, wall), Is.False);
        if (reload)
        {
            using var blob = new MemoryStream();
            using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
            blob.Position = 0; world = TestWorld.CreateWorld(seed);
            using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(world, reader);
            hut = world.Entities.Objects[hut.Id]; wall = world.Entities.Objects[oldId];
            Assert.That(FreeArchitectureRules.IsDemolitionSite(wall), Is.True);
            Assert.That(blocked.All(j => world.Junctions.Items[j].Blocked), Is.True);
        }
        var before = world.Entities.Objects.Keys.ToHashSet();
        FreeArchitectureRules.CompleteDemolition(world, wall);
        Assert.That(world.Entities.Objects.ContainsKey(oldId), Is.False);
        var salvage = world.Entities.Objects.Values.Where(o => !before.Contains(o.Id)).ToArray();
        Assert.That(salvage.Count(o => o.DefinitionId == ContentIds.Stick), Is.EqualTo(state.DeliveredSticks / 2));
        Assert.That(salvage.Count(o => o.DefinitionId == ContentIds.Board), Is.EqualTo(state.DeliveredBoards / 2));
        Assert.That(salvage.Count(o => o.DefinitionId == ContentIds.Rope), Is.EqualTo(state.DeliveredRope / 2));
        if (tracked)
        {
            Assert.That(salvage.All(o => System.Math.Abs(o.Wetness - .37f) < .00001f), Is.True);
            Assert.That(BuildSiteMath.IsAssembled(hut), Is.True);
            Assert.That(BuildSiteMath.IsStocked(hut), Is.True, "Paid neighbours must not request their historical bill again.");
            foreach (var material in BuildSiteMath.AllMaterials)
                Assert.That(BuildSiteMath.Installed(hut, material), Is.EqualTo(hut.Contents.Count(i => i.DefinitionId == material)));
        }
        foreach (var pair in neighbours)
        {
            Assert.That(world.Entities.Objects.ContainsKey(pair.Key), Is.True);
            Assert.That(world.Entities.Objects[pair.Key].ArchitectureElements[0].DeliveredTotal, Is.EqualTo(pair.Value.DeliveredTotal));
        }
        var count = world.Entities.Objects.Count;
        FreeArchitectureRules.CompleteDemolition(world, wall);
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(count), "Completion must not duplicate salvage after despawn.");
    }

    [Test]
    public void PreexistingLegacyExtensionKeepsItsNewDeliverySeparateFromOldPaidModules()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(o.BuildProduct));
        Assert.That(hut.InstalledBuildMaterials, Is.Null);
        Assert.That(hut.Contents, Is.Empty);
        BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var extended, out _);
        Assert.That(BlueprintEditorCommands.AddSupport(extended, BlueprintGeometry.HexCenter(BlueprintBuildingPlan.AnchorTile(extended))).Succeeded, Is.True);
        // Reproduce the old extension representation: only the delta is billed
        // and stored; original raised modules are already paid in their counters.
        world.PlayerBlueprints[hut.BlueprintId] = extended;
        var bill = BuildingRules.ReconcileHutElements(world, hut);
        hut.BuildProduct = ContentIds.HutPlan;
        hut.BillSticks = bill.Sticks; hut.BillBoards = bill.Boards; hut.BillRope = bill.Rope; hut.BillLeaves = bill.Leaves;
        Assert.That(bill.Sticks, Is.EqualTo(2));
        var stock = Enumerable.Range(0, 4).Select(_ => new HexLive.Simulation.Agents.ItemInstance(ContentIds.Stick) { Wetness = .91f }).ToArray();
        hut.Contents.AddRange(stock);
        var wall = BuildingRules.ArchitectureObjects(world, hut).First(o => o.DefinitionId == "architecture.wall.wood");
        extended.Elements.RemoveAll(e => BlueprintBuildingPlan.SlotKey(e) == wall.ArchitectureElements[0].SlotKey);
        var admission = engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(extended, false)));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        FreeArchitectureRules.CompleteDemolition(world, wall);
        Assert.That(stock.All(item => hut.Contents.Any(other => ReferenceEquals(item, other))), Is.True,
            "Dismantling a paid old wall must not consume the separate new extension delivery.");
        Assert.That(hut.Contents.Count(item => item.Wetness > .9f), Is.EqualTo(4));
        Assert.That(BuildSiteMath.Delivered(hut, ContentIds.Stick) - BuildSiteMath.Installed(hut, ContentIds.Stick), Is.EqualTo(4));
    }

    [Test]
    public void LegacyPaidPrefixCannotBeLootedAndNewLooseDeliverySurvivesOldWallTeardown()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(o.BuildProduct));
        BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var original, out _);
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(original, false))).Accepted, Is.True);
        Assert.That(hut.InstalledBuildMaterials, Is.Not.Null);
        Assert.That(ContainerLootMath.IsLootable(world, hut), Is.False, "Installed resources are building geometry, not a lootable duplicate.");
        var loose = new HexLive.Simulation.Agents.ItemInstance(ContentIds.Stick) { Wetness = .91f };
        hut.Contents.Add(loose); hut.Contents.Add(new HexLive.Simulation.Agents.ItemInstance(ContentIds.Stick) { Wetness = .91f });
        var cells = new System.Collections.Generic.List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, hut, cells);
        Assert.That(cells.Single().Count, Is.EqualTo(2));
        Assert.That(hut.Contents[cells.Single().SourceIndex], Is.SameAs(loose));
        var wall = BuildingRules.ArchitectureObjects(world, hut).First(o => o.DefinitionId == "architecture.wall.wood");
        var slot = wall.ArchitectureElements[0].SlotKey;
        original.Elements.RemoveAll(e => BlueprintBuildingPlan.SlotKey(e) == slot);
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(original, false))).Accepted, Is.True);
        FreeArchitectureRules.CompleteDemolition(world, wall);
        ContainerLootMath.BuildCells(world, hut, cells);
        Assert.That(cells.Single().Count, Is.EqualTo(2));
        Assert.That(hut.Contents[cells.Single().SourceIndex], Is.SameAs(loose));
        Assert.That(hut.Contents.Count(i => i.Wetness > .9f), Is.EqualTo(2));
    }

    [Test]
    public void WallToWindowWaitsForDemolitionThenStartsUnpaidAndUndoBeforeWorkRestoresWall()
    {
        var engine = TestWorld.CreateEngine(12345); var world = engine.World;
        var hut = world.Entities.Objects.Values.Single(o => o.DefinitionId == ContentIds.HutPlan && string.IsNullOrEmpty(o.BuildProduct));
        var wall = BuildingRules.ArchitectureObjects(world, hut).First(o => o.DefinitionId == "architecture.wall.wood");
        var id = wall.Id; var slot = wall.ArchitectureElements[0].SlotKey;
        BuildingBlueprintJson.TryDeserialize(BuildingRules.EditablePlanJsonFor(world, hut), out var original, out _);
        var revision = original.Clone(); revision.Elements.Single(e => BlueprintBuildingPlan.SlotKey(e) == slot).Kind = BlueprintElementKind.Window;
        var json = BuildingBlueprintJson.Serialize(revision, false);
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, json)).Accepted, Is.True);
        Assert.That(wall.DefinitionId, Is.EqualTo("architecture.wall.wood"));
        Assert.That(wall.ArchitectureElements[0].ReplacementDefinitionId, Is.EqualTo("architecture.window.wood"));
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, BuildingBlueprintJson.Serialize(original, false))).Accepted, Is.True);
        Assert.That(FreeArchitectureRules.IsDemolitionSite(wall), Is.False);
        Assert.That(wall.Id, Is.EqualTo(id));
        Assert.That(engine.ApplyManualCommand(new UpdateBuildingBlueprintCommand(hut.Id, json)).Accepted, Is.True);
        FreeArchitectureRules.CompleteDemolition(world, wall);
        var replacement = BuildingRules.ArchitectureObjects(world, hut).Single(o => o.ArchitectureElements[0].SlotKey == slot);
        Assert.That(replacement.DefinitionId, Is.EqualTo("architecture.window.wood"));
        Assert.That(replacement.ArchitectureElements[0].DeliveredTotal, Is.Zero);
        Assert.That(replacement.ArchitectureElements[0].Complete, Is.False);
        Assert.That(hut.BuildProduct, Is.EqualTo(ContentIds.HutPlan));
    }
}
