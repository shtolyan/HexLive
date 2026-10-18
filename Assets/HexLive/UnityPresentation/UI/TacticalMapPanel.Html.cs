#nullable enable
using System.Globalization;
using System.Text;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §174.7: the HUD map as the page's «map» screen. RefreshMapFrame keeps
    /// building the same visibility-safe frame; on the web it goes to the page
    /// instead of TacticalMapView (whose hex tessellation the browser would pay
    /// for on the main thread). The island relief is static per world and goes
    /// once through HtmlUi.Asset — a model is last-one-wins, an asset is kept;
    /// every frame names its key and carries only fog and what moves.
    /// </summary>
    public sealed partial class TacticalMapPanel
    {
        private const string HtmlScreen = "map";
        private string? _terrainKey;
        private string _fog = string.Empty;
        private string? _lastHtmlJson;

        private void HideToolkitMapUnderHtml()
        {
            if (!HtmlUi.Active) return;
            if (_miniCard != null) _miniCard.style.display = DisplayStyle.None;
            if (_miniExpandTab != null) _miniExpandTab.style.display = DisplayStyle.None;
        }

        /// <summary>Hand the frame to its painter: the page on the web, TacticalMapView elsewhere.</summary>
        private void PublishFrame(bool full)
        {
            if (!HtmlUi.Active)
            {
                _miniMap?.SetFrame(_frame);
                return;
            }

            HideToolkitMapUnderHtml();
            if (full || _terrainKey == null) PublishTerrainAndFog();
            PushHtml();
        }

        private void PublishTerrainAndFog()
        {
            // The relief (water, elevation) is worldgen output: one asset per world.
            var key = $"terrain:{_lastSeed}:{_frame.Tiles.Count}";
            if (key != _terrainKey && !HtmlUi.HasAsset(key))
            {
                var tiles = new JArray();
                foreach (var tile in _frame.Tiles)
                    tiles.Add(new JArray(Round(tile.Center.X), Round(tile.Center.Y), tile.Water ? 1 : 0, tile.Elevation));
                var terrain = new JObject { ["radius"] = HexSpatialMath.HexRadius, ["tiles"] = tiles };
                HtmlUi.Asset(key, terrain.ToString(Newtonsoft.Json.Formatting.None));
            }
            _terrainKey = key;

            // Fog in the same tile order: unknown, remembered, in sight.
            var fog = new StringBuilder(_frame.Tiles.Count);
            foreach (var tile in _frame.Tiles) fog.Append(tile.Visible ? '2' : tile.Explored ? '1' : '0');
            _fog = fog.ToString();
        }

        private void PushHtml()
        {
            if (_terrainKey == null) return;
            var builder = _runner != null ? CharacterPanel.ItemInformationBuilderFor(_runner) : null;
            var markers = new JArray();
            foreach (var marker in _frame.Markers)
            {
                markers.Add(new JObject
                {
                    ["kind"] = marker.Kind switch
                    {
                        TacticalMapMarkerKind.Camp => "camp", TacticalMapMarkerKind.Palm => "palm",
                        TacticalMapMarkerKind.Resource => "resource", _ => "item",
                    },
                    ["x"] = Round(marker.Center.X), ["z"] = Round(marker.Center.Y), ["live"] = marker.Live, ["lit"] = marker.Lit,
                    ["home"] = marker.AlwaysKnown, // her own camp: never dimmed, as TacticalMapView draws it
                });
            }
            foreach (var item in _frame.DroppedItems)
            {
                markers.Add(new JObject
                {
                    ["kind"] = "item", ["x"] = Round(item.Center.X), ["z"] = Round(item.Center.Y), ["live"] = item.Live,
                    ["icon"] = builder?.HtmlIcon(item.DefinitionId),
                });
            }

            var people = new JArray();
            foreach (var person in _frame.People)
            {
                people.Add(new JObject
                {
                    ["id"] = person.NpcId, ["x"] = Round(person.Position.X), ["z"] = Round(person.Position.Y),
                    ["owned"] = person.Owned, ["hostile"] = person.Hostile, ["selected"] = person.Selected,
                    ["dead"] = person.Dead, ["controlled"] = person.Controlled, ["face"] = builder?.HtmlFace(person.NpcId),
                });
            }

            var strangers = new JArray();
            foreach (var unknown in _frame.UnknownPeople)
                strangers.Add(new JObject { ["x"] = Round(unknown.Position.X), ["z"] = Round(unknown.Position.Y) });

            var mobs = new JArray();
            foreach (var mob in _frame.Mobs)
            {
                mobs.Add(new JObject
                {
                    ["x"] = Round(mob.Position.X), ["z"] = Round(mob.Position.Y),
                    ["kind"] = mob.Kind switch { TacticalMapMobKind.Wolf => "wolf", TacticalMapMobKind.Crab => "crab", _ => "other" },
                });
            }

            JArray? footprint = null;
            if (_frame.CameraFootprint.Count > 2)
            {
                footprint = new JArray();
                foreach (var point in _frame.CameraFootprint) footprint.Add(new JArray(Round(point.X), Round(point.Y)));
            }

            var model = new JObject
            {
                ["collapsed"] = _miniCollapsed, ["terrain"] = _terrainKey, ["fog"] = _fog, ["markers"] = markers,
                ["people"] = people, ["strangers"] = strangers, ["mobs"] = mobs, ["footprint"] = footprint,
            };
            var json = model.ToString(Newtonsoft.Json.Formatting.None);
            if (json == _lastHtmlJson) return;
            _lastHtmlJson = json;
            HtmlUi.Show(HtmlScreen, model);
        }

        private static float Round(float value) =>
            float.Parse(value.ToString("0.##", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        /// <summary>The map lives only while a world is on screen.</summary>
        private void UpdateHtmlVisibility()
        {
            if (!HtmlUi.Active) return;
            if (_runner != null && _runner.IsReady && !LoadingScreen.IsActive) return;
            HideHtml();
        }

        private void HideHtml()
        {
            if (_lastHtmlJson == null) return;
            _lastHtmlJson = null;
            _terrainKey = null; // a reconnected world may be another island
            HtmlUi.Hide(HtmlScreen);
        }

        private void OnHtmlCommand(string command, JObject args)
        {
            switch (command)
            {
                case "collapse": SetMiniCollapsed(true); break;
                case "expand": SetMiniCollapsed(false); break;
                case "click":
                    var point = new Float2((float?)args["x"] ?? 0f, (float?)args["z"] ?? 0f);
                    // As TryMapToWorld: only a point on the island moves the camera.
                    if (_frame.TileCoords.Contains(HexSpatialMath.WorldToTile(point)))
                        _cameraController?.MoveToMapPoint(point);
                    break;
            }
            _lastHtmlJson = null; // answer the click at once
            PushHtml();
        }
    }
}
