using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
/// <summary>Bug #255: actual NPC menu, UITK mouse press, RTS camera and aid execution.</summary>
public sealed class AidMenuRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, Private)!.Invoke(target, args);

    [UnityTest]
    public IEnumerator FeedSubmenuSurvivesItsOpeningPressAndFeedsComatoseWard() => Run(AidKind.Feed, true);

    [UnityTest]
    public IEnumerator HydrateSubmenuSurvivesItsOpeningPressAndWatersComatoseWard() => Run(AidKind.Hydrate, true);

    [UnityTest]
    public IEnumerator EmptyPackStillShowsAidAndReportsNoSupplies() => Run(AidKind.Hydrate, false);

    private static IEnumerator Run(AidKind kind, bool withSupplies)
    {
        SimDataFile.Require(System.IO.Path.Combine(Application.dataPath, "..", "SimData", "simdata.json"));
        var definition = PrototypeWorldDefinitionFactory.Create(271816231);
        var world = new WorldStateFactory().Create(definition);
        var settings = new SimulationSettings {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval };
        var clock = new SimulationClock();
        var engine = new SimulationEngine(world, settings, clock);
        SimulationSystemRegistry.RegisterDefaults(engine);
        DefinitionIdTable.Build(world.Content);
        engine.Step(); // Canonical spawn placement assigns CurrentJunction on the first tick.
        var pair = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value).Take(2).ToArray();
        Assert.That(pair.Length, Is.EqualTo(2));
        var helper = pair[0];
        var ward = pair[1];
        foreach (var npc in pair)
        {
            npc.Needs.Hunger = 0f;
            npc.Needs.Thirst = 0f;
            Assert.That(engine.ApplyManualCommand(new SetManualControlCommand(npc.Id, true)).Status,
                Is.EqualTo(ManualCommandAdmissionStatus.Accepted));
        }
        Assert.That(helper.CurrentJunction.HasValue, Is.True, "The first engine tick must place the helper.");
        Assert.That(ward.CurrentJunction.HasValue, Is.True, "The first engine tick must place the ward.");
        var neighbors = SpatialQueries.GetPassableNeighbors(world, helper.CurrentJunction.Value)
            .Where(id => SpatialQueries.IsJunctionFree(world, id)).ToArray();
        Assert.That(neighbors, Is.Not.Empty, "The controlled ward needs a real free neighbor.");
        var destinationId = neighbors[0];
        var destination = world.Junctions.Items[destinationId];
        if (ward.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, ward.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, ward.Id);
        }
        var oldTile = ward.Tile;
        ward.Tile = destination.Tiles.Count > 0 ? destination.Tiles[0] : ward.Tile;
        ward.Fragment = destination.Fragment;
        ward.Position = destination.WorldPosition;
        ward.CurrentJunction = destinationId;
        SpatialMutations.MoveEntityToTile(world, ward.Id, oldTile, ward.Tile);
        SpatialMutations.OccupyJunction(world, destinationId, ward.Id);
        ward.Mind.ComaCause = ComaCause.Exhaustion;
        ward.Needs.Energy = 0f;
        ward.Needs.Blood = 1f;
        ward.Health = 1f;
        ward.Needs.Hunger = kind == AidKind.Feed ? .4f : .1f;
        ward.Needs.Thirst = .4f;
        helper.Inventory.Items.Clear();
        if (withSupplies)
        {
            if (kind == AidKind.Feed) helper.Inventory.Items.Add(new ItemInstance("food.meat_cooked"));
            else helper.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle) { WaterKind = WaterKind.Rain, ResourceAmount = 2f });
        }
        var backend = new LocalEngineBackend(engine, clock, settings);
        var owner = new GameObject("Aid menu methods");
        owner.SetActive(false); // No world startup or automatic engine/camera updates.
        var runner = owner.AddComponent<SimulationRunnerBehaviour>();
        typeof(SimulationRunnerBehaviour).GetField("_backend", Private)!.SetValue(runner, backend);
        var adapter = owner.AddComponent<SimulationInputAdapter>();
        adapter.SetRunner(runner);
        var camera = owner.AddComponent<RtsCameraController>();
        var panelOwner = new GameObject("Aid menu panel");
        var panel = panelOwner.AddComponent<ContextMenuPanel>();
        var document = panelOwner.GetComponent<UIDocument>();
        var panelSettings = document.panelSettings;
        var mouse = InputSystem.AddDevice<Mouse>();
        mouse.MakeCurrent();
        var oldSelection = NpcSelection.SelectedIds.ToArray();
        var eventSystemType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("UnityEngine.EventSystems.EventSystem"))
            .FirstOrDefault(type => type != null);
        Assert.That(eventSystemType == null || !Object.FindObjectsByType(eventSystemType, FindObjectsSortMode.None)
            .OfType<Behaviour>().Any(behaviour => behaviour.isActiveAndEnabled), Is.True,
            "Explicit UITK delivery requires no active uGUI EventSystem.");
        var inputConfiguration = Object.FindObjectsByType<PanelInputConfiguration>(FindObjectsSortMode.None)
            .FirstOrDefault(configuration => configuration.isActiveAndEnabled);
        GameObject inputConfigurationOwner = null;
        if (inputConfiguration == null)
        {
            inputConfigurationOwner = new GameObject("Aid menu input isolation");
            inputConfiguration = inputConfigurationOwner.AddComponent<PanelInputConfiguration>();
        }
        var previousRedirection = inputConfiguration.panelInputRedirection;
        // The test runner still executes the title-screen bootstrap. Hide its
        // document roots without disabling components, rebuilding trees or
        // changing the player's menu/world state; restore the exact styles.
        var backgroundDocuments = Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)
            .Where(other => other != document)
            .Select(other => (Document: other, Display: other.rootVisualElement.style.display)).ToArray();
        var backgroundOwner = new GameObject("Aid menu clean background");
        try
        {
            // The UI-only runner scene has no camera clearing the backbuffer.
            // Clear each frame so removed rows cannot survive in PNG evidence.
            var backgroundCamera = backgroundOwner.AddComponent<Camera>();
            backgroundCamera.clearFlags = CameraClearFlags.SolidColor;
            backgroundCamera.backgroundColor = new Color(.08f, .1f, .12f, 1f);
            backgroundCamera.cullingMask = 0;
            // Unity 6.4 public input mode: wait for a uGUI EventSystem. With
            // none active, only our explicit SendEvent delivers UITK events;
            // Mouse.current still supplies real button edges to the RTS camera.
            inputConfiguration.panelInputRedirection = PanelInputConfiguration.PanelInputRedirection.Always;
            foreach (var entry in backgroundDocuments)
                entry.Document.rootVisualElement.style.display = DisplayStyle.None;
            NpcSelection.Activate(helper.Id.Value);
            Invoke(adapter, "RefreshControlSelection");
            // Feed exercises left presses; supplied Hydrate exercises right presses.
            var button = kind == AidKind.Hydrate && withSupplies ? 1 : 0;
            var screen = new Vector2(Screen.width * .3f, Screen.height * .6f);
            Invoke(adapter, "OpenNpcMenu", screen, ward.Id.Value);
            for (var i = 0; i < 3; i++) yield return null;
            var root = document.rootVisualElement;
            var uiPressCount = 0;
            root.RegisterCallback<MouseDownEvent>(_ => uiPressCount++, TrickleDown.TrickleDown);
            var help = FindRow(root, "menu.aid");
            Assert.That(help.panel, Is.Not.Null);
            PressRow(mouse, help, button: button);
            Assert.That(uiPressCount, Is.EqualTo(1));
            Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
            Assert.That(ContextMenuPanel.IsOpen, Is.True,
                "The same MouseDown that opens Help must not dismiss its new submenu in the camera update.");
            Assert.That(FindRow(root, "menu.aid.feed"), Is.Not.Null);
            Assert.That(FindRow(root, "menu.aid.hydrate"), Is.Not.Null);
            Release(mouse);
            for (var i = 0; i < 3; i++) yield return null;
            yield return new WaitForEndOfFrame();
            Assert.That(uiPressCount, Is.EqualTo(1), "No automatic duplicate press may arrive on a later frame.");
            Assert.That(engine.Commands.TryDequeue(out _), Is.False,
                "Opening Help must not automatically choose a child action.");
            AssertForegroundMenu(document, FindRow(root, "menu.aid.feed"));
            AssertForegroundMenu(document, FindRow(root, "menu.aid.hydrate"));
            var evidenceDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hexlive-bug255-runtime");
            System.IO.Directory.CreateDirectory(evidenceDirectory);
            Debug.Log("[bug255] Menu evidence: " + evidenceDirectory);
            var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
            try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidenceDirectory,
                $"{kind}-{(withSupplies ? "supplied" : "empty")}.png"), screenshot.EncodeToPNG()); }
            finally { Object.Destroy(screenshot); }
            // Negative control: a separate outside press still dismisses the card.
            InputSystem.QueueStateEvent(mouse, new MouseState {
                position = new Vector2(1f, Screen.height - 1f), buttons = (ushort)(1 << button) });
            InputSystem.Update();
            Assert.That((button == 0 ? mouse.leftButton : mouse.rightButton).wasPressedThisFrame, Is.True);
            Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
            Assert.That(ContextMenuPanel.IsOpen, Is.False, "A new outside press must dismiss the menu.");
            Assert.That(engine.Commands.TryDequeue(out _), Is.False, "Dismissal must not issue a world order.");
            Release(mouse);
            Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
            Assert.That(engine.Commands.TryDequeue(out _), Is.False,
                "The outside release must not issue a world order either.");
            Assert.That(NpcSelection.SelectedIds, Is.EqualTo(new[] { helper.Id.Value }),
                "The complete outside gesture must not change the selected helper.");
            for (var i = 0; i < 3; i++) yield return null;
            Invoke(adapter, "OpenNpcMenu", screen, ward.Id.Value);
            for (var i = 0; i < 3; i++) yield return null;
            PressRow(mouse, FindRow(root, "menu.aid"), button: button);
            Assert.That(uiPressCount, Is.EqualTo(2));
            Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
            Assert.That(ContextMenuPanel.IsOpen, Is.True);
            Release(mouse);
            for (var i = 0; i < 3; i++) yield return null;
            Assert.That(uiPressCount, Is.EqualTo(2), "The reopened menu must not receive a duplicate press.");
            var row = FindRow(root, kind == AidKind.Feed ? "menu.aid.feed" : "menu.aid.hydrate");
            // Hide/Open in one callback need not produce a fresh enter event.
            // Exercise the opposite update order with that stale hover state.
            var card = row.parent.parent;
            using (var leave = PointerLeaveEvent.GetPooled(new Event {
                type = EventType.MouseMove, mousePosition = row.worldBound.center }))
            {
                leave.target = card;
                card.SendEvent(leave);
            }
            Assert.That(ContextMenuPanel.PointerOverPanel, Is.False);
            PressRow(mouse, row, () => {
                Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
                Assert.That(ContextMenuPanel.IsOpen, Is.True,
                    "A press inside the new card must survive camera-before-UI even without a fresh enter event.");
            }, button);
            Assert.That(uiPressCount, Is.EqualTo(3));
            Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
            Assert.That(ContextMenuPanel.IsOpen, Is.False);
            Release(mouse);
            Invoke(camera, "HandlePointerGesture", backend.CreateSnapshot());
            Assert.That(NpcSelection.SelectedIds, Is.EqualTo(new[] { helper.Id.Value }),
                "Completing an aid-menu gesture must not change selection.");
            Assert.That(engine.Commands.TryDequeue(out var command), Is.True);
            Assert.That(command, Is.TypeOf<AidPersonCommand>());
            var aid = (AidPersonCommand)command;
            Assert.That(aid.Npc, Is.EqualTo(helper.Id));
            Assert.That(aid.Target, Is.EqualTo(ward.Id));
            Assert.That(aid.Kind, Is.EqualTo(kind));
            Assert.That(engine.Commands.TryDequeue(out _), Is.False, "No click-through movement command.");
            var admission = engine.ApplyManualCommand(aid);
            if (!withSupplies)
            {
                Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
                Assert.That(admission.Reason, Is.EqualTo("NoSupplies"));
                Assert.That(ward.Needs.Thirst, Is.EqualTo(.4f));
                yield break;
            }
            Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
            var acted = false;
            for (var i = 0; i < 400; i++)
            {
                ward.Needs.Energy = 0f;
                engine.Step();
                acted |= helper.Execution.CurrentInteraction ==
                    (kind == AidKind.Feed ? InteractionType.FeedOther : InteractionType.HydrateOther);
                if ((kind == AidKind.Feed ? ward.Needs.Hunger : ward.Needs.Thirst) < .4f) break;
            }
            Assert.That(acted, Is.True, "The real aid interaction must start.");
            Assert.That(kind == AidKind.Feed ? ward.Needs.Hunger : ward.Needs.Thirst,
                Is.LessThan(.4f), "The chosen aid must reach the comatose ward.");
            if (kind == AidKind.Feed) Assert.That(helper.Inventory.Items.Any(x => x.DefinitionId == "food.meat_cooked"), Is.False);
            else Assert.That(helper.BottleCharges, Is.EqualTo(1));
        }
        finally
        {
            Release(mouse);
            InputSystem.RemoveDevice(mouse);
            NpcSelection.ActivateMany(oldSelection);
            ContextMenuPanel.Close();
            foreach (var entry in backgroundDocuments)
                if (entry.Document != null) entry.Document.rootVisualElement.style.display = entry.Display;
            inputConfiguration.panelInputRedirection = previousRedirection;
            if (inputConfigurationOwner != null)
            {
                inputConfiguration.enabled = false;
                Object.Destroy(inputConfigurationOwner);
            }
            Object.Destroy(panelOwner);
            Object.Destroy(backgroundOwner);
            Object.Destroy(owner);
            if (panelSettings != null) Object.Destroy(panelSettings);
        }
    }

    private static void AssertForegroundMenu(UIDocument document, VisualElement row)
    {
        foreach (var other in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
        {
            if (other == document) continue;
            Assert.That(other.rootVisualElement.resolvedStyle.display, Is.EqualTo(DisplayStyle.None),
                other.name + " must not render or intercept a click above the test menu.");
        }
        Assert.That(row.worldBound.width, Is.GreaterThan(0f));
        Assert.That(row.worldBound.height, Is.GreaterThan(0f));
        var rootBounds = document.rootVisualElement.worldBound;
        Assert.That(rootBounds.Contains(row.worldBound.min) && rootBounds.Contains(row.worldBound.max),
            Is.True, "The complete aid row must fit in the visible panel.");
        var picked = row.panel.Pick(row.worldBound.center);
        Assert.That(picked == row || (picked != null && row.Contains(picked)), Is.True,
            "The real topmost element at the row center must belong to that aid row.");
    }

    private static VisualElement FindRow(VisualElement root, string key)
    {
        var label = root.Query<Label>().ToList().SingleOrDefault(x => x.text == Loc.Get(key));
        Assert.That(label, Is.Not.Null, key + " must be visible in the actual menu.");
        Assert.That(label.resolvedStyle.display, Is.Not.EqualTo(DisplayStyle.None));
        return label.parent;
    }

    private static void PressRow(Mouse mouse, VisualElement row, Action beforeUi = null, int button = 0)
    {
        var point = row.worldBound.center;
        var start = RuntimePanelUtils.ScreenToPanel(row.panel, Vector2.zero);
        var end = RuntimePanelUtils.ScreenToPanel(row.panel, new Vector2(Screen.width, Screen.height));
        var screen = new Vector2(
            (point.x - start.x) / (end.x - start.x) * Screen.width,
            Screen.height - (point.y - start.y) / (end.y - start.y) * Screen.height);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = screen, buttons = (ushort)(1 << button) });
        InputSystem.Update();
        Assert.That((button == 0 ? mouse.leftButton : mouse.rightButton).wasPressedThisFrame, Is.True);
        beforeUi?.Invoke();
        // Unity 6.4 PointerEnterEvent.PreDispatch reads elementTarget before
        // dispatch routing. A synthesized enter must carry its assigned target.
        var card = row.parent.parent;
        using (var enter = PointerEnterEvent.GetPooled(new Event {
            type = EventType.MouseMove, mousePosition = point }))
        {
            enter.target = card;
            card.SendEvent(enter);
        }
        Assert.That(ContextMenuPanel.PointerOverPanel, Is.True, "The pointer entered the real menu card before the press.");
        using (var press = MouseDownEvent.GetPooled(new Event {
            type = EventType.MouseDown, button = button, mousePosition = point })) row.SendEvent(press);
    }

    private static void Release(Mouse mouse)
    {
        InputSystem.QueueStateEvent(mouse, new MouseState { position = mouse.position.ReadValue() });
        InputSystem.Update();
    }
}
}
