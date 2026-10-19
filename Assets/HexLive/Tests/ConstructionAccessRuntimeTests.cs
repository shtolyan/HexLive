#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.HutTest;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
public sealed class ConstructionAccessRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private).SetValue(owner, value);
    private static object Get(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
    private static object Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Private).Invoke(owner, args);

    [Test]
    public void ForeignSelectionHasNoEditDeleteControlsAndBlueprintChecksWholeTerritory()
    {
        SimDataFile.Require(Path.Combine(Application.dataPath, "..", "SimData", "simdata.json"));
        var definition = PrototypeWorldDefinitionFactory.Create(493);
        var world = new WorldStateFactory().Create(definition);
        world.FactionHomes.Clear();
        world.FactionHomes[Faction.Colony] = new TileCoord(-20, 0);
        world.FactionHomes[Faction.Colony2] = TileCoord.Zero;
        var settings = new SimulationSettings(); var clock = new SimulationClock();
        var engine = new SimulationEngine(world, settings, clock);
        var host = new GameObject("Isolated construction UI 493"); host.SetActive(false);
        var runner = host.AddComponent<SimulationRunnerBehaviour>();
        Set(runner, "_backend", new LocalEngineBackend(engine, clock, settings));
        var panel = host.AddComponent<BuildModePanel>(); panel.SetRunner(runner);
        Set(panel, "_snapshot", runner.CreateSnapshot());
        Call(panel, "RefreshBuildPermissions");
        Set(panel, "_selectionLabel", new Label()); Set(panel, "_selectionDescription", new Label());
        Set(panel, "_deleteButton", new Button()); Set(panel, "_editButton", new Button());
        try
        {
            var foreign = new ObjectSnapshot { Id = new ObjectId(100000), Tile = TileCoord.Zero,
                BuildProduct = "bed.basic", BuildingBlueprintJson = BuildingBlueprintJson.Serialize(BuiltInBuildingBlueprints.Hut1Hex()) };
            Call(panel, "SelectWorldObject", foreign, true, true);
            Assert.That(Get(panel, "_selectedSiteId"), Is.Null);
            Assert.That(((Button)Get(panel, "_deleteButton")).style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(((Button)Get(panel, "_editButton")).style.display.value, Is.EqualTo(DisplayStyle.None));
            var html = (JObject)Call(panel, "HtmlModel");
            Assert.That((bool)html["selection"]["canDelete"], Is.False);
            Assert.That((bool)html["selection"]["canEdit"], Is.False);
            Assert.That((string)html["status"], Is.EqualTo("toast.order_rejected.ForeignConstruction"));
            var draft = BuiltInBuildingBlueprints.Hut1Hex();
            Assert.That((bool)Call(panel, "CanBuildDraft", draft, TileCoord.Zero, 0f), Is.False);
            Assert.That((bool)Call(panel, "CanBuildDraft", draft, new TileCoord(-20, 0), 0f), Is.True);
            Assert.That((bool)Call(panel, "CanEditTile", new TileCoord(5, 0)), Is.False);
            Assert.That((bool)Call(panel, "CanEditTile", new TileCoord(6, 0)), Is.True);
            var designer = host.AddComponent<HutLayoutDesigner>();
            Set(designer, "_runner", runner);
            Set(designer, "_worldPlacement", new HutLayoutDesigner.WorldPlacementConfig { DirectWorldConstruction = true });
            var stale = new BuildingBlueprintDraft();
            stale.Elements.Add(new BlueprintElementData { Id = "rejected-preview", Kind = BlueprintElementKind.Support });
            Set(designer, "_draft", stale);
            var history = (BlueprintCommandHistory)Get(designer, "_history");
            history.Execute(stale, d => BlueprintEditorCommands.AddSupport(d, new HexBuildNodeKey(2, 0)));
            ManualOrderFeedback.ReportWorld("Order=ApplyFreeArchitecture Reason=ForeignConstruction");
            Call(designer, "RefreshWorldRejection");
            Assert.That(((BuildingBlueprintDraft)Get(designer, "_draft")).Elements
                .Any(e => e.Id == "rejected-preview"), Is.False, "A server refusal must restore the authoritative projection.");
            Assert.That(history.CanUndo, Is.False, "Rejected operations must not remain replayable through undo.");
            Assert.That(Get(designer, "_statusKey"), Is.EqualTo("toast.order_rejected.ForeignConstruction"));
            var folder = "Build/Diagnostics/20261019-bug493"; Directory.CreateDirectory(folder);
            File.WriteAllText(folder + "/foreign-build-ui-model.json", html.ToString());
        }
        finally { Object.DestroyImmediate(host); }
    }

    [Test]
    public void RealDesignerRejectsForbiddenGestureAndDoesNotPolluteUndoHistory()
    {
        var host = new GameObject("Isolated free constructor 493"); host.SetActive(false);
        var designer = host.AddComponent<HutLayoutDesigner>();
        var draft = new BuildingBlueprintDraft();
        Set(designer, "_draft", draft);
        var config = new HutLayoutDesigner.WorldPlacementConfig { DirectWorldConstruction = true,
            CanApplyDraft = (before, after) => false };
        Set(designer, "_worldPlacement", config);
        try
        {
            Func<BuildingBlueprintDraft, BlueprintCommandResult> gesture = working =>
                BlueprintEditorCommands.AddSupport(working, new HexBuildNodeKey(0, 0));
            var result = (BlueprintCommandResult)Call(designer, "Execute", gesture);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(draft.Elements, Is.Empty);
            Assert.That(((BlueprintCommandHistory)Get(designer, "_history")).CanUndo, Is.False);
        }
        finally { Object.DestroyImmediate(host); }
    }
}
}
#endif
