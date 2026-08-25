using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§150: the HUD minimap is separate, while every world zoom keeps
/// the same honest meshes and the same world-input command path.</summary>
public sealed class TacticalMapUiContractTests
{
    [Test]
    public void DistantOverviewKeepsRealMeshesAndNormalWorldCommands()
    {
        var view = Read("Assets", "HexLive", "UnityPresentation", "UI", "TacticalMapView.cs");
        var panel = Read("Assets", "HexLive", "UnityPresentation", "UI", "TacticalMapPanel.cs");
        var camera = Read("Assets", "HexLive", "UnityPresentation", "Input", "RtsCameraController.cs");
        var input = Read("Assets", "HexLive", "UnityPresentation", "Input", "SimulationInputAdapter.cs");
        var renderer = Read("Assets", "HexLive", "UnityPresentation", "Rendering", "HexWorldRenderer.cs");
        var roster = Read("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.cs");
        var uxml = Read("Assets", "HexLiveContent", "RuntimeSource", "UI",
            "TacticalMapPanel.uxml");
        var bootstrap = Read("Assets", "HexLive", "UnityPresentation", "Bootstrap",
            "PrototypeRuntimeBootstrap.cs");

        Assert.Multiple(() =>
        {
            Assert.That(view, Does.Contain("HexSpatialMath.TileToWorld(coord)"));
            Assert.That(view, Does.Contain("HexSpatialMath.WorldToTile(world)"));
            Assert.That(view, Does.Contain("painter.fillColor = TacticalMapPalette.Ocean"));
            Assert.That(view, Does.Contain("if (tile.Water)"));
            Assert.That(view, Does.Not.Contain("TacticalMapPalette.OceanLine"));
            Assert.That(panel, Does.Contain("new TacticalMapView()"));
            Assert.That(panel, Does.Contain("SetMiniCollapsed(true)"));
            Assert.That(panel, Does.Contain("SetPortraitCache(NpcPortraitCache"));
            Assert.That(panel, Does.Not.Contain("new DistantWorldMarkersView()"));
            Assert.That(panel, Does.Contain("ItemIcons.Load(marker.DefinitionId)"));
            Assert.That(panel, Does.Contain("IsPortable(runner"));
            Assert.That(panel, Does.Contain("InteractionType.PickUp"));
            Assert.That(panel, Does.Contain("ItemCatalog.Classify(definition)"));
            Assert.That(panel, Does.Contain("exactCount = count > 4 ? 3 : count"));
            Assert.That(panel, Does.Contain("overflow: true"));
            Assert.That(panel, Does.Contain("snapshot.Mobs.Count"));
            Assert.That(panel, Does.Contain("snapshot.Crabs.Count"));
            Assert.That(panel, Does.Contain("snapshot.Sharks.Count"));
            Assert.That(panel, Does.Contain("_storedGarmentJunctions"));
            Assert.That(panel, Does.Contain("_emittedMarkerKeys.Clear()"));
            Assert.That(panel, Does.Contain("_emittedItemTypes.Clear()"));
            Assert.That(panel, Does.Contain("if (!visible && !homeCamp)"));
            Assert.That(view, Does.Contain("DrawCamp(painter"));
            Assert.That(view, Does.Contain("marker.Live || marker.AlwaysKnown"));
            Assert.That(view, Does.Contain("frame.DroppedItems"));
            Assert.That(view, Does.Contain("context.Allocate(4, 6, texture)"));
            Assert.That(view, Does.Contain("Mathf.Clamp(hexRadius * 0.82f, 7f, 12f)"));
            Assert.That(view, Does.Contain("TacticalMapMobKind.Shark"));
            Assert.That(view, Does.Contain("internal sealed class DistantWorldMarkersView"));
            Assert.That(view, Does.Contain("pickingMode = PickingMode.Ignore"));
            Assert.That(view, Does.Not.Contain("new GameObject("));
            Assert.That(view, Does.Not.Contain("SpriteRenderer"));
            Assert.That(view, Does.Contain("RuntimePanelUtils.ScreenToPanel"));
            Assert.That(view, Does.Contain("TryGetNpcViewPosition(person.NpcId"));
            Assert.That(view, Does.Contain("TryGetAnimalViewPosition"));
            Assert.That(view, Does.Contain("Mathf.Lerp(26f, 16f, distanceT)"));
            Assert.That(view, Does.Contain("frame.UnknownPeople"));
            Assert.That(view, Does.Contain("person.Dead"));
            Assert.That(uxml, Does.Contain("miniMapExpandTab"));
            Assert.That(uxml, Does.Contain("distantWorldMarkersHost"));
            Assert.That(panel, Does.Contain("_worldRenderer.PlayerVisibilityReady"));
            Assert.That(panel, Does.Contain("IsTileVisibleToPlayer(tile.Coord)"));
            Assert.That(panel, Does.Contain("_cameraController?.MoveToMapPoint(point)"));
            Assert.That(panel, Does.Not.Contain("TryMoveSelectionFromMap(point"));
            Assert.That(camera, Does.Contain("public void MoveToMapPoint(Float2 point)"));
            Assert.That(camera, Does.Not.Contain("MoveToMapPoint(mapPoint)"));
            Assert.That(camera, Does.Contain("_orbitMaxDistance = 260f"));
            Assert.That(camera, Does.Contain("public bool OverviewActive"));
            Assert.That(camera, Does.Contain("public float SmoothedDistance"));
            Assert.That(camera, Does.Contain("_overviewHideDistance = 32f"));
            Assert.That(camera, Does.Contain("_overviewShowDistance = 28f"));
            Assert.That(camera, Does.Not.Contain("_floraHideDistance"));
            Assert.That(camera, Does.Not.Contain("TryPickOverviewPerson"));
            Assert.That(camera, Does.Not.Contain("TryGetExploredTileCenter"));
            Assert.That(camera, Does.Not.Contain("if (OverviewActive)"));
            Assert.That(camera, Does.Contain("_worldRenderer?.SetOverviewGrassHidden"));
            Assert.That(camera, Does.Contain("PruneInvisibleSelection(snapshot)"));
            Assert.That(camera, Does.Not.Contain("TacticalMapActive"));
            Assert.That(camera, Does.Not.Contain("TryPickTacticalMapPoint"));
            Assert.That(renderer, Does.Contain("IsPlayerOwned(npc)"));
            Assert.That(renderer, Does.Contain("public bool PlayerVisibilityReady"));
            Assert.That(renderer, Does.Contain(
                "public void SetOverviewGrassHidden(bool hidden)"));
            Assert.That(renderer, Does.Contain("renderer.forceRenderingOff = true"));
            Assert.That(renderer, Does.Contain("_overviewSavedForceRenderingOff"));
            Assert.That(renderer, Does.Contain("_overviewGrassRenderers"));
            Assert.That(renderer, Does.Not.Contain("_overviewFloraRenderers"));
            Assert.That(renderer, Does.Not.Contain("BeginOverviewPortraitReveal"));
            Assert.That(renderer, Does.Contain("The distant profile deliberately contains grass only"));
            Assert.That(renderer, Does.Not.Contain("SetTacticalMapMode"));
            Assert.That(renderer, Does.Not.Contain("TacticalHexSprite"));
            Assert.That(renderer, Does.Not.Contain("TacticalMapPlaneY"));
            Assert.That(renderer, Does.Not.Contain("Tactical Map Sprites"));
            Assert.That(roster, Does.Contain("_worldRenderer.IsNpcPickable"));
            Assert.That(bootstrap, Does.Contain("new GameObject(\"HexLive Tactical Map\")"));
            Assert.That(bootstrap, Does.Contain("tacticalMap.SetPortraitCache(portraitCache)"));
            Assert.That(input, Does.Contain("new SetManualControlCommand(actor, true)"));
            Assert.That(input, Does.Contain("new MoveToCommand(actor, point, run)"));
        });

        var leftClickStart = camera.IndexOf(
            "private void TryHandleLeftClick", System.StringComparison.Ordinal);
        var manualClick = camera.IndexOf(
            "TryHandleManualClick(mousePosition)",
            leftClickStart,
            System.StringComparison.Ordinal);
        Assert.That(leftClickStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(manualClick, Is.GreaterThan(leftClickStart));
        Assert.That(camera.Substring(leftClickStart, manualClick - leftClickStart),
            Does.Not.Contain("OverviewActive"),
            "Camera distance must not intercept clicks before normal commands.");
    }

    private static string Read(params string[] path)
    {
        var fullPath = RepoPaths.Root;
        for (var i = 0; i < path.Length; i++)
        {
            fullPath = Path.Combine(fullPath, path[i]);
        }

        return File.ReadAllText(fullPath);
    }
}

}
