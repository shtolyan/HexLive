#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.HutTest;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Localization;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class WorldHouseEditRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Test]
    public void ExistingHouseGestureSendsImmediatelyAndReceiptKeepsActualEditorOpen()
    {
        var host = new GameObject("664 live existing-house constructor"); host.SetActive(false);
        var designer = host.AddComponent<HutLayoutDesigner>();
        var plan = BuiltInBuildingBlueprints.Hut1Hex();
        var sends = 0; var acknowledged = false;
        void Set(string name, object value) => typeof(HutLayoutDesigner).GetField(name, Private).SetValue(designer, value);
        object Get(string name) => typeof(HutLayoutDesigner).GetField(name, Private).GetValue(designer);
        object Call(string name, params object[] args) => typeof(HutLayoutDesigner).GetMethod(name, Private).Invoke(designer, args);
        Set("_draft", plan);
        Set("_worldPlacement", new HutLayoutDesigner.WorldPlacementConfig {
            ExistingOwner = new ObjectId(123), ApplyEachGesture = true,
            IsRevisionApplied = json => acknowledged,
            OnBuild = draft => sends++
        });
        try
        {
            var wall = plan.Elements.First(e => e.Kind == BlueprintElementKind.Wall);
            Func<BuildingBlueprintDraft, BlueprintCommandResult> gesture = working => BlueprintEditorCommands.Delete(working, wall.Id);
            var result = (BlueprintCommandResult)Call("Execute", gesture);
            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(sends, Is.EqualTo(1), "No Apply button is required after an in-place gesture.");
            Assert.That(Get("_pendingRevisionJson"), Is.Not.Null);
            Call("RefreshRevisionReceipt");
            Assert.That(Get("_pendingRevisionJson"), Is.Not.Null);
            acknowledged = true; Call("RefreshRevisionReceipt");
            Assert.That(Get("_pendingRevisionJson"), Is.Null);
            Assert.That(designer != null, Is.True);
            Assert.That(Get("_statusKey"), Is.EqualTo("blueprint.status.planned"));
            Call("Undo"); Assert.That(sends, Is.EqualTo(2), "Undo is also an authoritative planned edit.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    [TestCase(Language.English)]
    [TestCase(Language.Russian)]
    public void ImportedRuntimeLocResolvesTheActualPlanningAndReceiptTerms(Language language)
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = language;
            foreach (var key in new[] { "blueprint.status.planned", "blueprint.status.applying", "blueprint.status.apply_timeout" })
            {
                Assert.That(Loc.Has(key), Is.True, key);
                var text = Loc.Get(key);
                Assert.That(text, Is.Not.Empty.And.Not.EqualTo(key));
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(text, "[А-Яа-я]"), Is.EqualTo(language == Language.Russian));
            }
        }
        finally { Loc.Current = previous; }
    }

    [TestCase(0)] [TestCase(60)] [TestCase(120)] [TestCase(180)] [TestCase(240)] [TestCase(300)]
    public void ActualWorldEditorProjectionContainsExistingHousePiecesAtTheirWorldSlots(float yaw)
    {
        var source = BuiltInBuildingBlueprints.Hut1Hex(); var tile = new TileCoord(-6, -26);
        var snapshot = new WorldSnapshot();
        snapshot.Objects.Add(new ObjectSnapshot { Id = new ObjectId(125), Tile = tile, RotationDegrees = yaw,
            BuildingBlueprintJson = BuildingBlueprintJson.Serialize(source, false) });
        var projection = (BuildingBlueprintDraft)typeof(BuildModePanel).GetMethod("FreeWorldProjection", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { snapshot });
        var expected = source.Elements.Select(e => ArchitectureWorldEditing.ToWorld(e, source, tile, yaw)).Select(BlueprintBuildingPlan.SlotKey).ToArray();
        Assert.That(projection.Elements.Select(BlueprintBuildingPlan.SlotKey), Is.EquivalentTo(expected));
        // §120.13: настил живого мира — комнаты, их растягивают за стену.
        Assert.That(projection.Elements.Where(e => e.Kind == BlueprintElementKind.FloorSector).All(e => e.RoomId > 0), Is.True);
        Assert.That(BlueprintBuildingPlan.AnchorTile(projection), Is.EqualTo(TileCoord.Zero));
    }
}
}
#endif
