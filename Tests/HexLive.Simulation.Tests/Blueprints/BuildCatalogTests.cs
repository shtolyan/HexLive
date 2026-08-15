using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Blueprints
{
    [TestFixture]
    public sealed class BuildCatalogTests
    {
        [Test]
        public void EveryBuildableDefinitionHasOneCatalogCardAndMarkersDefaultOn()
        {
            var entries = BuildCatalogDefinition.All;
            Assert.That(entries.Select(entry => entry.DefinitionId).Distinct().Count(),
                Is.EqualTo(entries.Count));
            Assert.That(entries, Has.All.Matches<BuildCatalogEntryDefinition>(entry =>
                entry.ShowPlanningMarkers));
        }

        [Test]
        public void OutdoorCategoryContainsEveryExistingOutdoorBuildSiteProduct()
        {
            var outdoor = BuildCatalogDefinition.ForCategory(
                    BuildCatalogMode.Furniture, BuildCatalogCategories.Outdoor)
                .Select(entry => entry.DefinitionId).ToHashSet();
            Assert.That(outdoor, Does.Contain(ContentIds.Campfire));
            Assert.That(outdoor, Does.Contain(ContentIds.DryingRack));
            Assert.That(outdoor, Does.Contain(ContentIds.WaterCollector));
        }

        [Test]
        public void ExistingFurnitureAndWorkbenchUseTheSameCatalog()
        {
            var furniture = BuildCatalogDefinition.ForMode(BuildCatalogMode.Furniture)
                .Select(entry => entry.DefinitionId).ToHashSet();
            Assert.That(furniture, Does.Contain(ContentIds.BedBasic));
            Assert.That(furniture, Does.Contain("furniture.hearth"));
            Assert.That(furniture, Does.Contain(ContentIds.Wardrobe));
            Assert.That(furniture, Does.Contain(ContentIds.Workbench));
        }

        [Test]
        public void OutdoorPlansNeedNoFloorButIndoorFurnitureStillDoes()
        {
            var outdoor = new BuildingBlueprintDraft();
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                outdoor, ContentIds.DryingRack, TileCoord.Zero, 18).Succeeded, Is.True);

            var indoor = new BuildingBlueprintDraft();
            var refused = BlueprintEditorCommands.PlaceFurniture(
                indoor, ContentIds.BedBasic, TileCoord.Zero, 18);
            Assert.That(refused.Succeeded, Is.False);
            Assert.That(refused.Validation.Issues.Any(issue => issue.Code == "furniture.floor"), Is.True);
        }

        [Test]
        public void DependencyIndexSeparatesPlanningFromNpcBuildability()
        {
            var draft = new BuildingBlueprintDraft();
            Assert.That(BlueprintEditorCommands.CreateRoom(draft, Enumerable.Range(0, 6)
                .Select(sector => new FloorSectorKey(TileCoord.Zero, sector))).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.PlaceFurniture(
                draft, ContentIds.BedBasic, TileCoord.Zero, 18).Succeeded, Is.True);
            var bed = draft.Furniture.Single();

            var beforeFloor = new ConstructionDependencyIndex(draft, Enumerable.Empty<string>());
            Assert.That(beforeFloor.ForElement(bed.Id), Is.EqualTo(ConstructionAvailability.WaitingForFloor));

            var floorIds = draft.Elements.Where(element => element.Kind == BlueprintElementKind.FloorSector)
                .Select(element => element.Id).ToArray();
            var afterFloor = new ConstructionDependencyIndex(draft, floorIds);
            Assert.That(afterFloor.ForElement(bed.Id), Is.EqualTo(ConstructionAvailability.Ready));
        }

        [Test]
        public void RoofWaitsForItsExactCompletedSupports()
        {
            var draft = new BuildingBlueprintDraft();
            var sector = new RoofSectorKey(TileCoord.Zero, 0);
            foreach (var node in BlueprintGeometry.RoofSupports(sector))
                Assert.That(BlueprintEditorCommands.AddSupport(draft, node).Succeeded, Is.True);
            Assert.That(BlueprintEditorCommands.AddRoofSector(draft, sector).Succeeded, Is.True);
            var roof = draft.Elements.Single(element => element.Kind == BlueprintElementKind.RoofSector);
            var supportIds = draft.Elements.Where(element => element.Kind == BlueprintElementKind.Support)
                .Select(element => element.Id).ToArray();

            Assert.That(new ConstructionDependencyIndex(draft, supportIds.Take(1)).ForElement(roof.Id),
                Is.EqualTo(ConstructionAvailability.WaitingForSupports));
            Assert.That(new ConstructionDependencyIndex(draft, supportIds).ForElement(roof.Id),
                Is.EqualTo(ConstructionAvailability.Ready));
        }

        [Test]
        public void FurniturePlanningIntentAlwaysUsesFourOrientedStakes()
        {
            var item = new FurniturePlacementData
            {
                Id = "f1",
                DefinitionId = ContentIds.BedBasic,
                JunctionSlot = 18,
                YawStep = 2
            };
            var markers = BlueprintPlanningMarkers.ForFurniture(item);
            Assert.That(markers, Has.Count.EqualTo(4));
            Assert.That(markers.Select(marker => marker.Position).Distinct().Count(), Is.EqualTo(4));
            Assert.That(markers, Has.All.Matches<PlanningMarkerPoint>(marker =>
                marker.Layer == PlanningMarkerLayer.Furniture));
        }
    }
}
