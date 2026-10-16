using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§120: the art manifest is a measured bill, not a second tuning table.</summary>
public sealed class ConstructionArtContractTests
{
    [Test]
    public void CatalogHasAnAuthoredConstructionModelForEveryPhysicalEntry()
    {
        using var document = Manifest();
        var ids = document.RootElement.GetProperty("definitions").EnumerateArray()
            .Select(d => d.GetProperty("id").GetString()).ToHashSet();
        foreach (var entry in BuildCatalogDefinition.All)
        {
            if (entry.DefinitionId == "architecture.floor.open") continue; // tool, not another model
            Assert.That(ids, Does.Contain(entry.DefinitionId), entry.DefinitionId);
        }
    }

    [Test]
    public void EveryFurnitureResourceAgreesWithTheActualSimulationStageOrder()
    {
        using var document = Manifest();
        foreach (var definition in document.RootElement.GetProperty("definitions").EnumerateArray())
        {
            var id = definition.GetProperty("id").GetString();
            if (id.StartsWith("architecture.", StringComparison.Ordinal)) continue;
            var totals = Totals(definition);
            var site = new WorldObjectState
            {
                DefinitionId = "build.site", BuildProduct = id,
                BillLogs = Get(totals, ContentIds.Log), BillSticks = Get(totals, ContentIds.Stick),
                BillBoards = Get(totals, ContentIds.Board), BillRope = Get(totals, ContentIds.Rope),
                BillLeaves = Get(totals, ContentIds.PalmLeaf), BillStones = Get(totals, ContentIds.Stone)
            };
            foreach (var stage in definition.GetProperty("stages").EnumerateArray())
            {
                var material = stage.GetProperty("material").GetString();
                var count = stage.GetProperty("units").GetInt32();
                for (var i = 0; i < count; i++)
                {
                    Assert.That(BuildSiteMath.Needs(site, material), Is.True,
                        $"{id}: stage {stage.GetProperty("index")} resource {i+1}/{count}");
                    site.Contents.Add(new ItemInstance(material));
                }
            }
            Assert.That(BuildSiteMath.IsStocked(site), Is.True, id);
        }
    }

    [Test]
    public void ArchitectureBillsMatchBlueprintModules()
    {
        using var document = Manifest();
        foreach (var definition in document.RootElement.GetProperty("definitions").EnumerateArray())
        {
            var id = definition.GetProperty("id").GetString();
            if (!id.StartsWith("architecture.", StringComparison.Ordinal)) continue;
            var kind = id.Split('.')[1] switch
            {
                "wall" => BlueprintElementKind.Wall, "window" => BlueprintElementKind.Window,
                "door" => BlueprintElementKind.Door, "support" => BlueprintElementKind.Support,
                "floor" => BlueprintElementKind.FloorSector, "roof" => BlueprintElementKind.RoofSector,
                _ => throw new InvalidOperationException(id)
            };
            var draft = new BuildingBlueprintDraft();
            draft.Elements.Add(new BlueprintElementData
            {
                Id = "audit", Kind = kind,
                Segment = new BuildSegmentKey(new HexBuildNodeKey(0, 0), new HexBuildNodeKey(0, 1))
            });
            var module = BlueprintBuildingPlan.Modules(draft).Single();
            var total = Totals(definition);
            Assert.That(module.Sticks, Is.EqualTo(Get(total, ContentIds.Stick)), id);
            Assert.That(module.Boards, Is.EqualTo(Get(total, ContentIds.Board)), id);
            Assert.That(module.Rope, Is.EqualTo(Get(total, ContentIds.Rope)), id);
            Assert.That(module.Leaves, Is.EqualTo(Get(total, ContentIds.PalmLeaf)), id);
        }
    }

    [Test]
    public void FurnitureBillsMatchTheCurrentGameBalance()
    {
        using var document = Manifest();
        foreach (var definition in document.RootElement.GetProperty("definitions").EnumerateArray())
        {
            var id = definition.GetProperty("id").GetString();
            if (id.StartsWith("architecture.", StringComparison.Ordinal)) continue;
            // logs / sticks / boards / rope / leaves / stones
            var bill = id switch
            {
                ContentIds.BedBasic => new[] { SimBalance.BedBasicBillLogs, SimBalance.BedBasicBillSticks, 0,
                    SimBalance.BedBasicBillRope, SimBalance.BedBasicBillLeaves, 0 },
                ContentIds.Campfire => new[] { 0, SimBalance.CampfireBillSticks, 0, SimBalance.CampfireBillRope,
                    0, SimBalance.CampfireBillStones },
                ContentIds.FurnitureHearth => new[] { 0, SimBalance.HutHearthBillSticks, 0,
                    SimBalance.HutHearthBillRope, 0, SimBalance.HutHearthBillStones },
                ContentIds.DryingRack => new[] { 0, SimBalance.RackBillSticks, 0, SimBalance.RackBillRope, 0, 0 },
                ContentIds.WaterCollector => new[] { 0, SimBalance.WaterCollectorBillSticks, 0,
                    SimBalance.WaterCollectorBillRope, SimBalance.WaterCollectorBillLeaves, SimBalance.WaterCollectorBillStones },
                ContentIds.Workbench => new[] { 0, Spec119.WorkbenchBillSticks, Spec119.WorkbenchBillBoards,
                    Spec119.WorkbenchBillRope, 0, 0 },
                ContentIds.Wardrobe => new[] { 0, SimBalance.WardrobeBillSticks, SimBalance.WardrobeBillBoards,
                    SimBalance.WardrobeBillRope, 0, 0 },
                _ => throw new InvalidOperationException("Missing bill coverage for " + id)
            };
            var resources = new[] { ContentIds.Log, ContentIds.Stick, ContentIds.Board, ContentIds.Rope,
                ContentIds.PalmLeaf, ContentIds.Stone };
            var total = Totals(definition);
            for (var i = 0; i < resources.Length; i++)
                Assert.That(bill[i], Is.EqualTo(Get(total, resources[i])), id+" / "+resources[i]);
        }
    }

    private static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(RepoPaths.Root, "Tools/construction_stage_manifest.json")));

    private static Dictionary<string, int> Totals(JsonElement definition)
    {
        var total = new Dictionary<string, int>();
        foreach (var stage in definition.GetProperty("stages").EnumerateArray())
        {
            var material = stage.GetProperty("material").GetString();
            total[material] = Get(total, material)+stage.GetProperty("units").GetInt32();
        }
        return total;
    }

    private static int Get(Dictionary<string, int> values, string key) =>
        values.TryGetValue(key, out var value) ? value : 0;
}
