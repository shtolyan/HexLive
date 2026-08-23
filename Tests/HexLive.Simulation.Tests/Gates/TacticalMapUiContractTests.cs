using System;
using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§150 r5: the collapsible HUD minimap, world-space sprite map,
/// personal visibility and RTS commands keep sharing the same seams.</summary>
public sealed class TacticalMapUiContractTests
{
    [Test]
    public void TacticalSpriteMaskContainsCanonicalPointyTopPrismVertices()
    {
        const float sqrt3 = 1.7320508f;
        const float apothem = sqrt3 * 0.5f;
        for (var i = 0; i < 6; i++)
        {
            var angle = MathF.PI / 180f * (60f * i - 30f);
            var x = MathF.Cos(angle);
            var y = MathF.Sin(angle);
            var edge = MathF.Abs(y) + MathF.Abs(x) / sqrt3;
            Assert.That(MathF.Abs(x), Is.LessThanOrEqualTo(apothem + 0.0001f));
            Assert.That(edge, Is.LessThanOrEqualTo(1.0001f));
        }
    }

    [Test]
    public void TacticalMapUsesCanonicalHexMathVisibilityAndCommandQueue()
    {
        var view = Read("Assets", "HexLive", "UnityPresentation", "UI", "TacticalMapView.cs");
        var panel = Read("Assets", "HexLive", "UnityPresentation", "UI", "TacticalMapPanel.cs");
        var camera = Read("Assets", "HexLive", "UnityPresentation", "Input", "RtsCameraController.cs");
        var input = Read("Assets", "HexLive", "UnityPresentation", "Input", "SimulationInputAdapter.cs");
        var renderer = Read("Assets", "HexLive", "UnityPresentation", "Rendering", "HexWorldRenderer.cs");
        var roster = Read("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.cs");
        var uxml = Read("Assets", "Resources", "HexLive", "UI", "TacticalMapPanel.uxml");
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
            Assert.That(panel, Does.Contain("ItemIcons.Load(marker.DefinitionId)"));
            Assert.That(panel, Does.Contain("snapshot.Mobs.Count"));
            Assert.That(panel, Does.Contain("snapshot.Crabs.Count"));
            Assert.That(panel, Does.Contain("snapshot.Sharks.Count"));
            Assert.That(panel, Does.Contain("_storedGarmentJunctions"));
            Assert.That(panel, Does.Contain("_emittedMarkerKeys.Clear()"));
            Assert.That(panel, Does.Contain("if (!visible && !homeCamp)"));
            Assert.That(view, Does.Contain("DrawCamp(painter"));
            Assert.That(view, Does.Contain("marker.Live || marker.AlwaysKnown"));
            Assert.That(view, Does.Contain("frame.DroppedItems"));
            Assert.That(view, Does.Contain("Mathf.Min(4, Mathf.CeilToInt(Mathf.Sqrt(count)))"));
            Assert.That(view, Does.Contain("context.Allocate(4, 6, texture)"));
            Assert.That(view, Does.Contain("Mathf.Clamp(hexRadius * 0.82f, 7f, 12f)"));
            Assert.That(view, Does.Contain("TacticalMapMobKind.Shark"));
            Assert.That(uxml, Does.Contain("miniMapExpandTab"));
            Assert.That(uxml, Does.Not.Contain("worldMapOverlay"));
            Assert.That(panel, Does.Contain("_worldRenderer.PlayerVisibilityReady"));
            Assert.That(panel, Does.Contain("IsTileVisibleToPlayer(tile.Coord)"));
            Assert.That(panel, Does.Contain("_cameraController?.MoveToMapPoint(point)"));
            Assert.That(panel, Does.Not.Contain("TryMoveSelectionFromMap(point"));
            Assert.That(camera, Does.Contain("public void MoveToMapPoint(Float2 point)"));
            Assert.That(camera, Does.Contain("MoveToMapPoint(mapPoint)"));
            Assert.That(camera, Does.Not.Contain("TryMoveSelectionFromMap(mapPoint"));
            Assert.That(camera, Does.Contain("_orbitMaxDistance = 260f"));
            Assert.That(camera, Does.Contain("TacticalMapActive"));
            Assert.That(camera, Does.Contain("TryPickTacticalMapPoint"));
            Assert.That(camera, Does.Contain("PruneInvisibleSelection(snapshot)"));
            Assert.That(renderer, Does.Contain("IsPlayerOwned(npc)"));
            Assert.That(renderer, Does.Contain("public bool PlayerVisibilityReady"));
            Assert.That(renderer, Does.Contain("public void SetTacticalMapMode(bool enabled)"));
            Assert.That(renderer, Does.Contain("renderer.forceRenderingOff = true"));
            Assert.That(renderer, Does.Contain("go.AddComponent<SpriteRenderer>()"));
            Assert.That(renderer, Does.Contain("TacticalHexSprite()"));
            Assert.That(renderer, Does.Contain("TacticalCampSprite()"));
            Assert.That(renderer, Does.Contain("if (!homeCamp && !IsTileVisibleToPlayer"));
            Assert.That(renderer, Does.Contain(
                "Mathf.Abs(py) + Mathf.Abs(px) / HexSpatialMath.Sqrt3"));
            Assert.That(renderer, Does.Contain(
                "Mathf.Abs(px) <= HexSpatialMath.HexApothemFactor"));
            Assert.That(roster, Does.Contain("_worldRenderer.IsNpcPickable"));
            Assert.That(bootstrap, Does.Contain("new GameObject(\"HexLive Tactical Map\")"));
            Assert.That(bootstrap, Does.Contain("tacticalMap.SetPortraitCache(portraitCache)"));
            Assert.That(input, Does.Contain("new SetManualControlCommand(actor, true)"));
            Assert.That(input, Does.Contain("new MoveToCommand(actor, point, run)"));
        });
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
