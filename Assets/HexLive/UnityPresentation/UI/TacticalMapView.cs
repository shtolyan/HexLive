#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §150: compact procedural minimap. It owns no simulation state: the
    /// controller supplies a point-in-time frame and this element only paints
    /// it and performs the exact inverse map transform for clicks. The distant
    /// map is rendered in world space by HexWorldRenderer, not by this UI.
    /// </summary>
    internal sealed class TacticalMapView : VisualElement
    {
        private const float IslandPadding = 8f;

        private TacticalMapFrame? _frame;

        public TacticalMapView()
        {
            pickingMode = PickingMode.Position;
            generateVisualContent += OnGenerateVisualContent;
        }

        public void SetFrame(TacticalMapFrame frame)
        {
            _frame = frame;
            MarkDirtyRepaint();
        }

        public bool TryMapToWorld(Vector2 localPosition, out Float2 point)
        {
            point = Float2.Zero;
            if (_frame == null || !_frame.HasBounds ||
                !TryGetTransform(contentRect, _frame, out var map))
            {
                return false;
            }

            if (localPosition.x < map.OffsetX || localPosition.x > map.OffsetX + map.Width ||
                localPosition.y < map.OffsetY || localPosition.y > map.OffsetY + map.Height)
            {
                return false;
            }

            var worldX = _frame.MinX + (localPosition.x - map.OffsetX) / map.Scale;
            var worldZ = _frame.MaxZ - (localPosition.y - map.OffsetY) / map.Scale;
            var world = new Float2(worldX, worldZ);
            if (!_frame.TileCoords.Contains(HexSpatialMath.WorldToTile(world)))
            {
                return false;
            }

            point = world;
            return true;
        }

        private void OnGenerateVisualContent(MeshGenerationContext context)
        {
            var frame = _frame;
            var rect = contentRect;
            if (rect.width < 2f || rect.height < 2f)
            {
                return;
            }

            var painter = context.painter2D;
            // §150.1: ocean is one continuous field. Water hexes neither set
            // the island scale nor expose the rectangular world-grid bounds.
            painter.fillColor = TacticalMapPalette.Ocean;
            painter.BeginPath();
            TraceRect(painter, rect);
            painter.Fill();

            if (frame == null || !frame.HasBounds ||
                !TryGetTransform(rect, frame, out var map))
            {
                return;
            }

            var hexRadius = HexSpatialMath.HexRadius * map.Scale;
            var drawLines = hexRadius >= 2.8f;
            painter.lineJoin = LineJoin.Round;
            painter.lineWidth = Mathf.Clamp(hexRadius * 0.075f, 0.45f, 1.15f);

            for (var i = 0; i < frame.Tiles.Count; i++)
            {
                var tile = frame.Tiles[i];
                if (tile.Water)
                {
                    continue;
                }

                var center = ToUi(tile.Center, frame, map);
                painter.fillColor = TacticalMapPalette.TileColor(
                    tile.Water, tile.Explored, tile.Visible, tile.Elevation);
                painter.strokeColor = TacticalMapPalette.TileLine;
                painter.BeginPath();
                TracePointyHex(painter, center, hexRadius);
                painter.Fill();
                if (drawLines)
                {
                    painter.Stroke();
                }
            }

            DrawMarkers(painter, frame, map, hexRadius);
            DrawPeople(painter, frame, map, hexRadius);
            DrawCameraFootprint(painter, frame, map);
        }

        private void DrawMarkers(
            Painter2D painter, TacticalMapFrame frame, MapTransform map, float hexRadius)
        {
            var size = Mathf.Clamp(hexRadius * 0.38f, 1.25f, 5f);
            for (var i = 0; i < frame.Markers.Count; i++)
            {
                var marker = frame.Markers[i];
                var center = ToUi(marker.Center, frame, map);
                var alpha = marker.Live ? 1f : 0.38f;
                if (marker.Kind == TacticalMapMarkerKind.Palm)
                {
                    painter.strokeColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.Palm, alpha);
                    painter.lineCap = LineCap.Round;
                    painter.lineWidth = Mathf.Max(1f, size * 0.42f);
                    painter.BeginPath();
                    painter.MoveTo(center + new Vector2(0f, size));
                    painter.LineTo(center - new Vector2(0f, size));
                    painter.MoveTo(center);
                    painter.LineTo(center + new Vector2(-size, -size * 0.55f));
                    painter.MoveTo(center);
                    painter.LineTo(center + new Vector2(size, -size * 0.55f));
                    painter.Stroke();
                }
                else
                {
                    painter.fillColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.Resource, alpha);
                    painter.BeginPath();
                    painter.MoveTo(center + new Vector2(0f, -size));
                    painter.LineTo(center + new Vector2(size, 0f));
                    painter.LineTo(center + new Vector2(0f, size));
                    painter.LineTo(center + new Vector2(-size, 0f));
                    painter.ClosePath();
                    painter.Fill();
                }
            }
        }

        private void DrawPeople(
            Painter2D painter, TacticalMapFrame frame, MapTransform map, float hexRadius)
        {
            var radius = Mathf.Clamp(hexRadius * 0.34f, 2.2f, 7f);
            for (var i = 0; i < frame.People.Count; i++)
            {
                var person = frame.People[i];
                var center = ToUi(person.Position, frame, map);
                painter.fillColor = TacticalMapPalette.PersonColor(
                    person.Owned, person.Hostile);
                painter.BeginPath();
                painter.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Fill();

                if (!person.Selected)
                {
                    continue;
                }

                painter.strokeColor = TacticalMapPalette.Selected;
                painter.lineWidth = Mathf.Clamp(radius * 0.42f, 1.1f, 2.5f);
                painter.BeginPath();
                painter.Arc(center, radius * 1.65f, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Stroke();
            }
        }

        private void DrawCameraFootprint(
            Painter2D painter, TacticalMapFrame frame, MapTransform map)
        {
            if (frame.CameraFootprint.Count < 3)
            {
                return;
            }

            painter.strokeColor = TacticalMapPalette.CameraFrame;
            painter.lineWidth = 1f;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            painter.MoveTo(ToUi(frame.CameraFootprint[0], frame, map));
            for (var i = 1; i < frame.CameraFootprint.Count; i++)
            {
                painter.LineTo(ToUi(frame.CameraFootprint[i], frame, map));
            }

            painter.ClosePath();
            painter.Stroke();
        }

        private static Vector2 ToUi(
            Float2 world, TacticalMapFrame frame, MapTransform map) =>
            new(
                map.OffsetX + (world.X - frame.MinX) * map.Scale,
                map.OffsetY + (frame.MaxZ - world.Y) * map.Scale);

        private static bool TryGetTransform(
            Rect rect, TacticalMapFrame frame, out MapTransform map)
        {
            map = default;
            var worldWidth = frame.MaxX - frame.MinX;
            var worldHeight = frame.MaxZ - frame.MinZ;
            var availableWidth = Mathf.Max(0f, rect.width - IslandPadding * 2f);
            var availableHeight = Mathf.Max(0f, rect.height - IslandPadding * 2f);
            if (worldWidth <= 0.001f || worldHeight <= 0.001f ||
                availableWidth <= 0.001f || availableHeight <= 0.001f)
            {
                return false;
            }

            var scale = Mathf.Min(availableWidth / worldWidth, availableHeight / worldHeight);
            var width = worldWidth * scale;
            var height = worldHeight * scale;
            map = new MapTransform(
                scale,
                rect.x + (rect.width - width) * 0.5f,
                rect.y + (rect.height - height) * 0.5f,
                width,
                height);
            return true;
        }

        private static void TracePointyHex(Painter2D painter, Vector2 center, float radius)
        {
            for (var i = 0; i < 6; i++)
            {
                var radians = (30f + 60f * i) * Mathf.Deg2Rad;
                var point = center + new Vector2(
                    Mathf.Cos(radians) * radius,
                    -Mathf.Sin(radians) * radius);
                if (i == 0) painter.MoveTo(point);
                else painter.LineTo(point);
            }

            painter.ClosePath();
        }

        private static void TraceRect(Painter2D painter, Rect rect)
        {
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
        }

        private readonly struct MapTransform
        {
            public readonly float Scale;
            public readonly float OffsetX;
            public readonly float OffsetY;
            public readonly float Width;
            public readonly float Height;

            public MapTransform(float scale, float offsetX, float offsetY, float width, float height)
            {
                Scale = scale;
                OffsetX = offsetX;
                OffsetY = offsetY;
                Width = width;
                Height = height;
            }
        }
    }

    /// <summary>Shared visual language for the HUD minimap and the world-space
    /// sprite map. Keeping palette and object classification here prevents the
    /// two representations from drifting apart again.</summary>
    internal static class TacticalMapPalette
    {
        public static readonly Color Ocean = new(0.055f, 0.16f, 0.21f, 1f);
        public static readonly Color UnknownLand = new(0.055f, 0.072f, 0.068f, 0.98f);
        public static readonly Color RememberedLand = new(0.25f, 0.29f, 0.25f, 0.98f);
        public static readonly Color LowLand = new(0.34f, 0.55f, 0.31f, 0.98f);
        public static readonly Color HighLand = new(0.70f, 0.66f, 0.42f, 0.98f);
        public static readonly Color TileLine = new(0.08f, 0.12f, 0.10f, 0.62f);
        public static readonly Color Palm = new(0.35f, 0.88f, 0.43f, 0.98f);
        public static readonly Color Resource = new(1f, 0.69f, 0.25f, 0.98f);
        public static readonly Color OwnNpc = new(0.22f, 0.85f, 1f, 1f);
        public static readonly Color FriendlyNpc = new(0.87f, 0.91f, 0.86f, 1f);
        public static readonly Color HostileNpc = new(1f, 0.31f, 0.30f, 1f);
        public static readonly Color Selected = new(1f, 0.80f, 0.31f, 1f);
        public static readonly Color CameraFrame = new(0.92f, 0.96f, 1f, 0.82f);

        public static Color TileColor(
            bool water, bool explored, bool visible, int elevation)
        {
            if (water) return Ocean;
            if (visible)
            {
                return Color.Lerp(LowLand, HighLand,
                    Mathf.InverseLerp(-1f, 5f, elevation));
            }

            return explored ? RememberedLand : UnknownLand;
        }

        public static Color PersonColor(bool owned, bool hostile) =>
            owned ? OwnNpc : hostile ? HostileNpc : FriendlyNpc;

        public static Color WithAlpha(Color color, float alpha) =>
            new(color.r, color.g, color.b, color.a * alpha);

        public static bool TryClassify(
            string definitionId, out TacticalMapMarkerKind kind)
        {
            if (definitionId.StartsWith("tree.", StringComparison.Ordinal) ||
                definitionId.StartsWith("plant.", StringComparison.Ordinal))
            {
                kind = TacticalMapMarkerKind.Palm;
                return true;
            }

            if (definitionId.StartsWith("resource.", StringComparison.Ordinal) ||
                definitionId.StartsWith("food.", StringComparison.Ordinal) ||
                definitionId.StartsWith("tool.", StringComparison.Ordinal) ||
                definitionId.StartsWith("item.", StringComparison.Ordinal))
            {
                kind = TacticalMapMarkerKind.Resource;
                return true;
            }

            kind = default;
            return false;
        }
    }

    internal sealed class TacticalMapFrame
    {
        public readonly List<TacticalMapTile> Tiles = new();
        public readonly HashSet<TileCoord> TileCoords = new();
        public readonly HashSet<TileCoord> VisibleTiles = new();
        public readonly List<TacticalMapMarker> Markers = new();
        public readonly List<TacticalMapPerson> People = new();
        public readonly List<Float2> CameraFootprint = new();

        public float MinX { get; private set; }
        public float MaxX { get; private set; }
        public float MinZ { get; private set; }
        public float MaxZ { get; private set; }
        public bool HasBounds { get; private set; }

        public void Clear()
        {
            Tiles.Clear();
            TileCoords.Clear();
            VisibleTiles.Clear();
            Markers.Clear();
            People.Clear();
            CameraFootprint.Clear();
            HasBounds = false;
        }

        public void AddTile(TacticalMapTile tile)
        {
            Tiles.Add(tile);
            TileCoords.Add(tile.Coord);
            if (tile.Visible) VisibleTiles.Add(tile.Coord);

            // The surrounding ocean is an infinite visual field for the HUD.
            // Only the land silhouette decides how large the island appears.
            if (tile.Water)
            {
                return;
            }

            var halfWidth = HexSpatialMath.HexRadius * HexSpatialMath.HexApothemFactor;
            var minX = tile.Center.X - halfWidth;
            var maxX = tile.Center.X + halfWidth;
            var minZ = tile.Center.Y - HexSpatialMath.HexRadius;
            var maxZ = tile.Center.Y + HexSpatialMath.HexRadius;
            if (!HasBounds)
            {
                MinX = minX;
                MaxX = maxX;
                MinZ = minZ;
                MaxZ = maxZ;
                HasBounds = true;
                return;
            }

            MinX = Mathf.Min(MinX, minX);
            MaxX = Mathf.Max(MaxX, maxX);
            MinZ = Mathf.Min(MinZ, minZ);
            MaxZ = Mathf.Max(MaxZ, maxZ);
        }
    }

    internal readonly struct TacticalMapTile
    {
        public readonly TileCoord Coord;
        public readonly Float2 Center;
        public readonly bool Water;
        public readonly bool Explored;
        public readonly bool Visible;
        public readonly int Elevation;

        public TacticalMapTile(
            TileCoord coord, bool water, bool explored, bool visible, int elevation)
        {
            Coord = coord;
            Center = HexSpatialMath.TileToWorld(coord);
            Water = water;
            Explored = explored;
            Visible = visible;
            Elevation = elevation;
        }
    }

    internal enum TacticalMapMarkerKind
    {
        Palm,
        Resource
    }

    internal readonly struct TacticalMapMarker
    {
        public readonly Float2 Center;
        public readonly TacticalMapMarkerKind Kind;
        public readonly bool Live;

        public TacticalMapMarker(TileCoord tile, TacticalMapMarkerKind kind, bool live)
        {
            Center = HexSpatialMath.TileToWorld(tile);
            Kind = kind;
            Live = live;
        }
    }

    internal readonly struct TacticalMapPerson
    {
        public readonly Float2 Position;
        public readonly bool Owned;
        public readonly bool Hostile;
        public readonly bool Selected;

        public TacticalMapPerson(
            Float2 position, bool owned, bool hostile, bool selected)
        {
            Position = position;
            Owned = owned;
            Hostile = hostile;
            Selected = selected;
        }
    }
}
