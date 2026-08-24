#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §150: compact procedural minimap. It owns no simulation state: the
    /// controller supplies a point-in-time frame and this element only paints
    /// it and performs the exact inverse map transform for clicks. The same
    /// frame also feeds the non-intercepting distant-world marker canvas.
    /// </summary>
    internal sealed class TacticalMapView : VisualElement
    {
        private const float IslandPadding = 8f;
        private static readonly ushort[] QuadIndices = { 0, 1, 2, 2, 3, 0 };

        private TacticalMapFrame? _frame;
        private readonly Vertex[] _quadVertices = new Vertex[4];

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
            DrawDroppedItems(context, painter, frame, map, hexRadius);
            DrawMobs(painter, frame, map, hexRadius);
            DrawUnknownPeople(painter, frame, map, hexRadius);
            DrawPeople(context, painter, frame, map, hexRadius);
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
                var alpha = marker.Live || marker.AlwaysKnown ? 1f : 0.38f;
                if (marker.Kind == TacticalMapMarkerKind.Camp)
                {
                    DrawCamp(painter, center, Mathf.Max(size, 4.3f), alpha, marker.Lit);
                }
                else if (marker.Kind == TacticalMapMarkerKind.Palm)
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

        private static void DrawCamp(
            Painter2D painter, Vector2 center, float size, float alpha, bool lit)
        {
            painter.fillColor = TacticalMapPalette.WithAlpha(
                TacticalMapPalette.CampRing, alpha * 0.92f);
            painter.BeginPath();
            painter.Arc(center, size * 1.05f, Angle.Degrees(0f), Angle.Degrees(360f));
            painter.Fill();

            painter.fillColor = TacticalMapPalette.WithAlpha(
                lit ? TacticalMapPalette.CampFire : TacticalMapPalette.CampCold,
                alpha);
            painter.BeginPath();
            painter.MoveTo(center + new Vector2(0f, -size * 0.92f));
            painter.BezierCurveTo(
                center + new Vector2(size * 0.76f, -size * 0.12f),
                center + new Vector2(size * 0.54f, size * 0.78f),
                center + new Vector2(0f, size * 0.88f));
            painter.BezierCurveTo(
                center + new Vector2(-size * 0.72f, size * 0.55f),
                center + new Vector2(-size * 0.62f, -size * 0.20f),
                center + new Vector2(0f, -size * 0.92f));
            painter.Fill();
        }

        private void DrawDroppedItems(
            MeshGenerationContext context,
            Painter2D painter,
            TacticalMapFrame frame,
            MapTransform map,
            float hexRadius)
        {
            var index = 0;
            while (index < frame.DroppedItems.Count)
            {
                var first = frame.DroppedItems[index];
                var end = index + 1;
                while (end < frame.DroppedItems.Count &&
                       frame.DroppedItems[end].Tile.Equals(first.Tile))
                {
                    end++;
                }

                var count = end - index;
                var columns = Mathf.Min(4, Mathf.CeilToInt(Mathf.Sqrt(count)));
                var rows = Mathf.CeilToInt(count / (float)columns);
                var cell = Mathf.Clamp(hexRadius * 0.78f, 7f, 11f);
                const float gap = 1f;
                var width = columns * cell + (columns - 1) * gap;
                var height = rows * cell + (rows - 1) * gap;
                var origin = ToUi(first.Center, frame, map) -
                    new Vector2(width * 0.5f, height * 0.5f);

                for (var itemIndex = index; itemIndex < end; itemIndex++)
                {
                    var local = itemIndex - index;
                    var row = local / columns;
                    var column = local % columns;
                    var cellRect = new Rect(
                        origin.x + column * (cell + gap),
                        origin.y + row * (cell + gap),
                        cell,
                        cell);
                    var item = frame.DroppedItems[itemIndex];
                    var alpha = item.Live ? 1f : 0.48f;

                    painter.fillColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.ClothingPlate, alpha);
                    painter.BeginPath();
                    TraceRect(painter, cellRect);
                    painter.Fill();

                    if (item.Overflow)
                    {
                        painter.strokeColor = TacticalMapPalette.WithAlpha(
                            TacticalMapPalette.ClothingFallback, alpha);
                        painter.lineWidth = 1.1f;
                        for (var stack = 0; stack < 3; stack++)
                        {
                            var inset = cellRect.width * (0.18f + stack * 0.11f);
                            painter.BeginPath();
                            TraceRect(painter, new Rect(
                                cellRect.x + inset,
                                cellRect.y + inset,
                                cellRect.width - inset * 2f,
                                cellRect.height - inset * 2f));
                            painter.Stroke();
                        }
                    }
                    else if (item.Icon != null)
                    {
                        DrawSprite(context, item.Icon, cellRect, new Color(1f, 1f, 1f, alpha));
                    }
                    else
                    {
                        // The addressable icon is non-blocking. This tidy
                        // placeholder survives only until the next 4 Hz paint.
                        painter.fillColor = TacticalMapPalette.WithAlpha(
                            TacticalMapPalette.ClothingFallback, alpha);
                        var inset = cellRect.width * 0.22f;
                        painter.BeginPath();
                        TraceRect(painter, new Rect(
                            cellRect.x + inset,
                            cellRect.y + inset,
                            cellRect.width - inset * 2f,
                            cellRect.height - inset * 2f));
                        painter.Fill();
                    }

                    painter.strokeColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.ClothingBorder, alpha);
                    painter.lineWidth = 0.75f;
                    painter.BeginPath();
                    TraceRect(painter, cellRect);
                    painter.Stroke();
                }

                index = end;
            }
        }

        private static void DrawMobs(
            Painter2D painter, TacticalMapFrame frame, MapTransform map, float hexRadius)
        {
            var size = Mathf.Clamp(hexRadius * 0.62f, 4.2f, 8f);
            for (var i = 0; i < frame.Mobs.Count; i++)
            {
                var mob = frame.Mobs[i];
                var center = ToUi(mob.Position, frame, map);
                painter.fillColor = TacticalMapPalette.MobColor(mob.Kind);
                painter.strokeColor = TacticalMapPalette.MobColor(mob.Kind);
                painter.lineWidth = Mathf.Max(1f, size * 0.22f);
                painter.lineCap = LineCap.Round;
                painter.lineJoin = LineJoin.Round;

                if (mob.Kind == TacticalMapMobKind.Crab)
                {
                    painter.BeginPath();
                    painter.Arc(center, size * 0.48f, Angle.Degrees(0f), Angle.Degrees(360f));
                    painter.Fill();
                    painter.BeginPath();
                    painter.MoveTo(center - new Vector2(size * 0.45f, 0f));
                    painter.LineTo(center - new Vector2(size, size * 0.55f));
                    painter.MoveTo(center + new Vector2(size * 0.45f, 0f));
                    painter.LineTo(center + new Vector2(size, -size * 0.55f));
                    painter.Stroke();
                }
                else if (mob.Kind == TacticalMapMobKind.Shark)
                {
                    painter.BeginPath();
                    painter.MoveTo(center + new Vector2(0f, -size));
                    painter.LineTo(center + new Vector2(size * 0.82f, size * 0.72f));
                    painter.LineTo(center + new Vector2(-size * 0.48f, size * 0.42f));
                    painter.ClosePath();
                    painter.Fill();
                }
                else
                {
                    // Wolf/dog head: two ears make it distinct from a person.
                    painter.BeginPath();
                    painter.MoveTo(center + new Vector2(-size, -size * 0.82f));
                    painter.LineTo(center + new Vector2(-size * 0.35f, -size * 0.42f));
                    painter.LineTo(center + new Vector2(0f, -size * 0.72f));
                    painter.LineTo(center + new Vector2(size * 0.35f, -size * 0.42f));
                    painter.LineTo(center + new Vector2(size, -size * 0.82f));
                    painter.LineTo(center + new Vector2(size * 0.56f, size * 0.58f));
                    painter.LineTo(center + new Vector2(0f, size));
                    painter.LineTo(center + new Vector2(-size * 0.56f, size * 0.58f));
                    painter.ClosePath();
                    painter.Fill();
                }
            }
        }

        private static void DrawUnknownPeople(
            Painter2D painter, TacticalMapFrame frame, MapTransform map, float hexRadius)
        {
            var size = Mathf.Clamp(hexRadius * 0.62f, 4.2f, 8f);
            for (var i = 0; i < frame.UnknownPeople.Count; i++)
            {
                var center = ToUi(frame.UnknownPeople[i].Position, frame, map);
                painter.strokeColor = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.Selected, 0.72f);
                painter.lineWidth = Mathf.Max(1f, size * 0.18f);
                painter.lineCap = LineCap.Round;
                painter.BeginPath();
                painter.Arc(center - new Vector2(0f, size * 0.2f), size * 0.58f,
                    Angle.Degrees(200f), Angle.Degrees(520f));
                painter.MoveTo(center + new Vector2(0f, size * 0.28f));
                painter.LineTo(center + new Vector2(0f, size * 0.68f));
                painter.Stroke();
                painter.fillColor = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.Selected, 0.72f);
                painter.BeginPath();
                painter.Arc(center + new Vector2(0f, size * 0.95f), size * 0.12f,
                    Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Fill();
            }
        }

        private void DrawPeople(
            MeshGenerationContext context,
            Painter2D painter,
            TacticalMapFrame frame,
            MapTransform map,
            float hexRadius)
        {
            // Contacts remain legible even when an 8160-tile island makes one
            // hex only a couple of pixels wide.
            var radius = Mathf.Clamp(hexRadius * 0.82f, 7f, 12f);
            for (var i = 0; i < frame.People.Count; i++)
            {
                var person = frame.People[i];
                var center = ToUi(person.Position, frame, map);
                painter.fillColor = TacticalMapPalette.PersonColor(
                    person.Owned, person.Hostile);
                painter.BeginPath();
                painter.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Fill();

                if (person.Portrait != null)
                {
                    // PortraitCache photographs have transparent backgrounds;
                    // keeping the quad inside the coloured disc gives a round
                    // avatar without a per-contact VisualElement or mask.
                    var portraitSize = radius * 1.62f;
                    DrawTexture(
                        context,
                        person.Portrait,
                        new Rect(
                            center.x - portraitSize * 0.5f,
                            center.y - portraitSize * 0.5f,
                            portraitSize,
                            portraitSize),
                        new Rect(0f, 0f, 1f, 1f),
                        Color.white);
                }

                painter.strokeColor = TacticalMapPalette.PersonBorder;
                painter.lineWidth = Mathf.Clamp(radius * 0.16f, 1f, 1.8f);
                painter.BeginPath();
                painter.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Stroke();

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

        private void DrawSprite(
            MeshGenerationContext context, Sprite sprite, Rect rect, Color tint)
        {
            var texture = sprite.texture;
            if (texture == null || texture.width <= 0 || texture.height <= 0)
            {
                return;
            }

            var source = sprite.textureRect;
            var uv = new Rect(
                source.x / texture.width,
                source.y / texture.height,
                source.width / texture.width,
                source.height / texture.height);
            DrawTexture(context, texture, rect, uv, tint);
        }

        private void DrawTexture(
            MeshGenerationContext context,
            Texture texture,
            Rect rect,
            Rect uv,
            Color tint)
        {
            var mesh = context.Allocate(4, 6, texture);
#if !UNITY_2023_1_OR_NEWER
            var atlas = mesh.uvRegion;
            uv = new Rect(
                atlas.x + uv.x * atlas.width,
                atlas.y + uv.y * atlas.height,
                uv.width * atlas.width,
                uv.height * atlas.height);
#endif
            _quadVertices[0].position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ);
            _quadVertices[1].position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ);
            _quadVertices[2].position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ);
            _quadVertices[3].position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ);
            _quadVertices[0].uv = new Vector2(uv.xMin, uv.yMin);
            _quadVertices[1].uv = new Vector2(uv.xMin, uv.yMax);
            _quadVertices[2].uv = new Vector2(uv.xMax, uv.yMax);
            _quadVertices[3].uv = new Vector2(uv.xMax, uv.yMin);
            for (var i = 0; i < _quadVertices.Length; i++)
            {
                _quadVertices[i].tint = tint;
            }

            mesh.SetAllVertices(_quadVertices);
            mesh.SetAllIndices(QuadIndices);
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

    /// <summary>
    /// §150: one generated, input-transparent overlay for every distant-world
    /// contact. It owns no GameObjects and no child element per marker.
    /// </summary>
    internal sealed class DistantWorldMarkersView : VisualElement
    {
        private static readonly ushort[] QuadIndices = { 0, 1, 2, 2, 3, 0 };
        private readonly Vertex[] _quadVertices = new Vertex[4];
        private TacticalMapFrame? _frame;
        private Camera? _camera;
        private RtsCameraController? _controller;
        private HexWorldRenderer? _worldRenderer;
        private bool _wasVisible;

        public DistantWorldMarkersView()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerateVisualContent;
        }

        public void SetFrame(TacticalMapFrame frame)
        {
            _frame = frame;
            MarkDirtyRepaint();
        }

        public void SetPresentation(
            Camera? camera,
            RtsCameraController? controller,
            HexWorldRenderer? worldRenderer)
        {
            _camera = camera;
            _controller = controller;
            _worldRenderer = worldRenderer;
        }

        public void RefreshForCamera()
        {
            var visible = _controller != null &&
                (_controller.OverviewBlend > 0.001f || _controller.FloraBlend > 0.001f);
            if (visible || _wasVisible)
            {
                MarkDirtyRepaint();
            }
            _wasVisible = visible;
        }

        private void OnGenerateVisualContent(MeshGenerationContext context)
        {
            var frame = _frame;
            var controller = _controller;
            if (frame == null || controller == null || _camera == null ||
                _worldRenderer == null || panel == null)
            {
                return;
            }

            var overviewAlpha = controller.OverviewBlend;
            var floraAlpha = controller.FloraBlend;
            if (overviewAlpha <= 0.001f && floraAlpha <= 0.001f)
            {
                return;
            }

            var distanceT = Mathf.InverseLerp(
                32f, 260f, controller.SmoothedDistance);
            var detailScale = Mathf.Lerp(1f, 0.62f, distanceT);
            var painter = context.painter2D;

            DrawStaticMarkers(painter, frame, overviewAlpha, floraAlpha, detailScale);
            DrawItems(context, painter, frame, overviewAlpha, detailScale);
            DrawMobs(painter, frame, overviewAlpha, detailScale);
            DrawUnknownPeople(painter, frame, overviewAlpha, detailScale);
            DrawPeople(context, painter, frame, overviewAlpha, distanceT);
        }

        private void DrawStaticMarkers(
            Painter2D painter,
            TacticalMapFrame frame,
            float overviewAlpha,
            float floraAlpha,
            float scale)
        {
            for (var i = 0; i < frame.Markers.Count; i++)
            {
                var marker = frame.Markers[i];
                var alpha = marker.Kind == TacticalMapMarkerKind.Palm
                    ? floraAlpha
                    : overviewAlpha;
                alpha *= marker.Live || marker.AlwaysKnown ? 1f : 0.48f;
                if (alpha <= 0.001f ||
                    !TryProject(marker.Center, marker.Tile, 0.62f, out var center))
                {
                    continue;
                }

                if (marker.Kind == TacticalMapMarkerKind.Camp)
                {
                    DrawCamp(painter, center, 9.5f * scale, alpha, marker.Lit);
                }
                else if (marker.Kind == TacticalMapMarkerKind.Palm)
                {
                    var size = 10f * scale;
                    painter.strokeColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.Palm, alpha);
                    painter.lineCap = LineCap.Round;
                    painter.lineWidth = Mathf.Max(1.25f, 2.4f * scale);
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
                    var size = 7f * scale;
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

        private static void DrawCamp(
            Painter2D painter, Vector2 center, float size, float alpha, bool lit)
        {
            painter.fillColor = TacticalMapPalette.WithAlpha(
                TacticalMapPalette.CampRing, alpha * 0.92f);
            painter.BeginPath();
            painter.Arc(center, size * 1.12f, Angle.Degrees(0f), Angle.Degrees(360f));
            painter.Fill();

            painter.fillColor = TacticalMapPalette.WithAlpha(
                lit ? TacticalMapPalette.CampFire : TacticalMapPalette.CampCold, alpha);
            painter.BeginPath();
            painter.MoveTo(center + new Vector2(0f, -size));
            painter.BezierCurveTo(
                center + new Vector2(size * 0.78f, -size * 0.10f),
                center + new Vector2(size * 0.48f, size * 0.76f),
                center + new Vector2(0f, size * 0.9f));
            painter.BezierCurveTo(
                center + new Vector2(-size * 0.7f, size * 0.5f),
                center + new Vector2(-size * 0.6f, -size * 0.2f),
                center + new Vector2(0f, -size));
            painter.Fill();
        }

        private void DrawItems(
            MeshGenerationContext context,
            Painter2D painter,
            TacticalMapFrame frame,
            float overviewAlpha,
            float scale)
        {
            var index = 0;
            while (index < frame.DroppedItems.Count)
            {
                var first = frame.DroppedItems[index];
                var end = index + 1;
                while (end < frame.DroppedItems.Count &&
                       frame.DroppedItems[end].Tile.Equals(first.Tile))
                {
                    end++;
                }

                if (!TryProject(first.Center, first.Tile, 0.5f, out var center))
                {
                    index = end;
                    continue;
                }

                var count = end - index;
                var cell = 20f * scale;
                var gap = Mathf.Max(1f, 2f * scale);
                var columns = Mathf.Min(2, count);
                var rows = Mathf.CeilToInt(count / 2f);
                var origin = center - new Vector2(
                    (columns * cell + (columns - 1) * gap) * 0.5f,
                    (rows * cell + (rows - 1) * gap) * 0.5f);

                for (var i = index; i < end; i++)
                {
                    var item = frame.DroppedItems[i];
                    var local = i - index;
                    var alpha = overviewAlpha * (item.Live ? 1f : 0.48f);
                    var rect = new Rect(
                        origin.x + (local % 2) * (cell + gap),
                        origin.y + (local / 2) * (cell + gap),
                        cell,
                        cell);
                    painter.fillColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.ClothingPlate, alpha);
                    painter.BeginPath();
                    TraceRect(painter, rect);
                    painter.Fill();

                    if (item.Overflow)
                    {
                        painter.strokeColor = TacticalMapPalette.WithAlpha(
                            TacticalMapPalette.ClothingFallback, alpha);
                        painter.lineWidth = Mathf.Max(1f, 1.4f * scale);
                        for (var stack = 0; stack < 3; stack++)
                        {
                            var inset = rect.width * (0.16f + stack * 0.12f);
                            painter.BeginPath();
                            TraceRect(painter, new Rect(
                                rect.x + inset,
                                rect.y + inset,
                                rect.width - inset * 2f,
                                rect.height - inset * 2f));
                            painter.Stroke();
                        }
                    }
                    else if (item.Icon != null)
                    {
                        DrawSprite(context, item.Icon, rect, new Color(1f, 1f, 1f, alpha));
                    }
                    else
                    {
                        painter.fillColor = TacticalMapPalette.WithAlpha(
                            TacticalMapPalette.ClothingFallback, alpha);
                        painter.BeginPath();
                        painter.Arc(rect.center, rect.width * 0.22f,
                            Angle.Degrees(0f), Angle.Degrees(360f));
                        painter.Fill();
                    }

                    painter.strokeColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.ClothingBorder, alpha);
                    painter.lineWidth = 1f;
                    painter.BeginPath();
                    TraceRect(painter, rect);
                    painter.Stroke();
                }

                index = end;
            }
        }

        private void DrawMobs(
            Painter2D painter,
            TacticalMapFrame frame,
            float overviewAlpha,
            float scale)
        {
            var size = 8.5f * scale;
            for (var i = 0; i < frame.Mobs.Count; i++)
            {
                var mob = frame.Mobs[i];
                Vector2 center;
                if (mob.Id >= 0 && _worldRenderer != null &&
                    _worldRenderer.TryGetAnimalViewPosition(
                        mob.Id, mob.IsCrab, out var viewPosition))
                {
                    if (!TryProject(viewPosition + Vector3.up * 0.5f, out center))
                    {
                        continue;
                    }
                }
                else if (!TryProject(
                             mob.Position,
                             HexSpatialMath.WorldToTile(mob.Position),
                             0.5f,
                             out center))
                {
                    continue;
                }

                var color = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.MobColor(mob.Kind), overviewAlpha);
                painter.fillColor = color;
                painter.strokeColor = color;
                painter.lineWidth = Mathf.Max(1f, size * 0.2f);
                painter.lineCap = LineCap.Round;
                painter.lineJoin = LineJoin.Round;
                if (mob.Kind == TacticalMapMobKind.Crab)
                {
                    painter.BeginPath();
                    painter.Arc(center, size * 0.48f,
                        Angle.Degrees(0f), Angle.Degrees(360f));
                    painter.Fill();
                    painter.BeginPath();
                    painter.MoveTo(center - new Vector2(size * 0.45f, 0f));
                    painter.LineTo(center - new Vector2(size, size * 0.55f));
                    painter.MoveTo(center + new Vector2(size * 0.45f, 0f));
                    painter.LineTo(center + new Vector2(size, -size * 0.55f));
                    painter.Stroke();
                }
                else if (mob.Kind == TacticalMapMobKind.Shark)
                {
                    painter.BeginPath();
                    painter.MoveTo(center + new Vector2(0f, -size));
                    painter.LineTo(center + new Vector2(size * 0.82f, size * 0.72f));
                    painter.LineTo(center + new Vector2(-size * 0.48f, size * 0.42f));
                    painter.ClosePath();
                    painter.Fill();
                }
                else
                {
                    painter.BeginPath();
                    painter.MoveTo(center + new Vector2(-size, -size * 0.82f));
                    painter.LineTo(center + new Vector2(-size * 0.35f, -size * 0.42f));
                    painter.LineTo(center + new Vector2(0f, -size * 0.72f));
                    painter.LineTo(center + new Vector2(size * 0.35f, -size * 0.42f));
                    painter.LineTo(center + new Vector2(size, -size * 0.82f));
                    painter.LineTo(center + new Vector2(size * 0.56f, size * 0.58f));
                    painter.LineTo(center + new Vector2(0f, size));
                    painter.LineTo(center + new Vector2(-size * 0.56f, size * 0.58f));
                    painter.ClosePath();
                    painter.Fill();
                }
            }
        }

        private void DrawUnknownPeople(
            Painter2D painter,
            TacticalMapFrame frame,
            float overviewAlpha,
            float scale)
        {
            var size = 8f * scale;
            for (var i = 0; i < frame.UnknownPeople.Count; i++)
            {
                var unknown = frame.UnknownPeople[i];
                if (!TryProject(unknown.Position, unknown.Tile, 0.75f, out var center))
                {
                    continue;
                }

                painter.strokeColor = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.Selected, overviewAlpha * 0.72f);
                painter.lineWidth = Mathf.Max(1.2f, 2f * scale);
                painter.lineCap = LineCap.Round;
                painter.BeginPath();
                painter.Arc(center - new Vector2(0f, size * 0.25f), size * 0.62f,
                    Angle.Degrees(200f), Angle.Degrees(520f));
                painter.MoveTo(center + new Vector2(0f, size * 0.3f));
                painter.LineTo(center + new Vector2(0f, size * 0.72f));
                painter.Stroke();
                painter.fillColor = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.Selected, overviewAlpha * 0.72f);
                painter.BeginPath();
                painter.Arc(center + new Vector2(0f, size), size * 0.13f,
                    Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Fill();
            }
        }

        private void DrawPeople(
            MeshGenerationContext context,
            Painter2D painter,
            TacticalMapFrame frame,
            float overviewAlpha,
            float distanceT)
        {
            var diameter = Mathf.Lerp(26f, 16f, distanceT);
            var radius = diameter * 0.5f;
            for (var i = 0; i < frame.People.Count; i++)
            {
                var person = frame.People[i];
                var tile = HexSpatialMath.WorldToTile(person.Position);
                Vector2 center;
                if (_worldRenderer != null &&
                    _worldRenderer.TryGetNpcViewPosition(person.NpcId, out var viewPosition))
                {
                    if (!TryProject(
                            viewPosition + Vector3.up * SimulationUnityMapper.CameraTargetHeight,
                            out center))
                    {
                        continue;
                    }
                }
                else if (!TryProject(person.Position, tile,
                             SimulationUnityMapper.CameraTargetHeight, out center))
                {
                    continue;
                }

                var faction = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.PersonColor(person.Owned, person.Hostile),
                    overviewAlpha);
                painter.fillColor = faction;
                painter.BeginPath();
                painter.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Fill();

                if (person.Portrait != null)
                {
                    var portraitSize = diameter * 0.82f;
                    DrawTexture(
                        context,
                        person.Portrait,
                        new Rect(
                            center.x - portraitSize * 0.5f,
                            center.y - portraitSize * 0.5f,
                            portraitSize,
                            portraitSize),
                        new Rect(0f, 0f, 1f, 1f),
                        new Color(1f, 1f, 1f, overviewAlpha));
                }

                painter.strokeColor = TacticalMapPalette.WithAlpha(
                    TacticalMapPalette.PersonBorder, overviewAlpha);
                painter.lineWidth = Mathf.Max(1f, radius * 0.14f);
                painter.BeginPath();
                painter.Arc(center, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                painter.Stroke();

                if (person.Dead)
                {
                    var badge = center + new Vector2(radius * 0.72f, radius * 0.72f);
                    painter.strokeColor = TacticalMapPalette.WithAlpha(
                        Color.white, overviewAlpha);
                    painter.lineWidth = Mathf.Max(1.2f, radius * 0.18f);
                    painter.BeginPath();
                    painter.MoveTo(badge - Vector2.one * radius * 0.25f);
                    painter.LineTo(badge + Vector2.one * radius * 0.25f);
                    painter.MoveTo(badge + new Vector2(-1f, 1f) * radius * 0.25f);
                    painter.LineTo(badge + new Vector2(1f, -1f) * radius * 0.25f);
                    painter.Stroke();
                }

                if (person.Selected)
                {
                    painter.strokeColor = TacticalMapPalette.WithAlpha(
                        TacticalMapPalette.Selected, overviewAlpha);
                    painter.lineWidth = Mathf.Max(1.5f, radius * 0.24f);
                    painter.BeginPath();
                    painter.Arc(center, radius * 1.28f,
                        Angle.Degrees(0f), Angle.Degrees(360f));
                    painter.Stroke();
                }
            }
        }

        private bool TryProject(
            Float2 point, TileCoord tile, float lift, out Vector2 local) =>
            TryProject(new Vector3(
                point.X,
                (_worldRenderer?.GroundTopY(tile) ?? SimulationUnityMapper.TileHeight) + lift,
                point.Y), out local);

        private bool TryProject(Vector3 world, out Vector2 local)
        {
            local = Vector2.zero;
            if (_camera == null || panel == null)
            {
                return false;
            }

            var screen = _camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
            {
                return false;
            }

            var panelPosition = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(screen.x, screen.y));
            local = this.WorldToLocal(panelPosition);
            var rect = contentRect;
            return local.x >= rect.xMin - 32f && local.x <= rect.xMax + 32f &&
                local.y >= rect.yMin - 32f && local.y <= rect.yMax + 32f;
        }

        private void DrawSprite(
            MeshGenerationContext context, Sprite sprite, Rect rect, Color tint)
        {
            var texture = sprite.texture;
            if (texture == null || texture.width <= 0 || texture.height <= 0)
            {
                return;
            }

            var source = sprite.textureRect;
            DrawTexture(context, texture, rect, new Rect(
                source.x / texture.width,
                source.y / texture.height,
                source.width / texture.width,
                source.height / texture.height), tint);
        }

        private void DrawTexture(
            MeshGenerationContext context,
            Texture texture,
            Rect rect,
            Rect uv,
            Color tint)
        {
            var mesh = context.Allocate(4, 6, texture);
#if !UNITY_2023_1_OR_NEWER
            var atlas = mesh.uvRegion;
            uv = new Rect(
                atlas.x + uv.x * atlas.width,
                atlas.y + uv.y * atlas.height,
                uv.width * atlas.width,
                uv.height * atlas.height);
#endif
            _quadVertices[0].position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ);
            _quadVertices[1].position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ);
            _quadVertices[2].position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ);
            _quadVertices[3].position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ);
            _quadVertices[0].uv = new Vector2(uv.xMin, uv.yMin);
            _quadVertices[1].uv = new Vector2(uv.xMin, uv.yMax);
            _quadVertices[2].uv = new Vector2(uv.xMax, uv.yMax);
            _quadVertices[3].uv = new Vector2(uv.xMax, uv.yMin);
            for (var i = 0; i < _quadVertices.Length; i++)
            {
                _quadVertices[i].tint = tint;
            }
            mesh.SetAllVertices(_quadVertices);
            mesh.SetAllIndices(QuadIndices);
        }

        private static void TraceRect(Painter2D painter, Rect rect)
        {
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
        }
    }

    /// <summary>Shared visual language for the HUD minimap and distant-world
    /// markers. Keeping palette and object classification here prevents the
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
        public static readonly Color CampRing = new(0.28f, 0.18f, 0.10f, 0.98f);
        public static readonly Color CampFire = new(1f, 0.42f, 0.12f, 1f);
        public static readonly Color CampCold = new(0.72f, 0.54f, 0.36f, 1f);
        public static readonly Color ClothingPlate = new(0.08f, 0.11f, 0.13f, 0.94f);
        public static readonly Color ClothingBorder = new(0.78f, 0.88f, 0.92f, 0.92f);
        public static readonly Color ClothingFallback = new(0.72f, 0.47f, 0.82f, 0.96f);
        public static readonly Color OwnNpc = new(0.22f, 0.85f, 1f, 1f);
        public static readonly Color FriendlyNpc = new(0.87f, 0.91f, 0.86f, 1f);
        public static readonly Color HostileNpc = new(1f, 0.31f, 0.30f, 1f);
        public static readonly Color PersonBorder = new(0.94f, 0.98f, 1f, 0.96f);
        public static readonly Color Wolf = new(0.98f, 0.29f, 0.25f, 1f);
        public static readonly Color Crab = new(1f, 0.55f, 0.18f, 1f);
        public static readonly Color Shark = new(0.38f, 0.72f, 0.87f, 1f);
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

        public static Color MobColor(TacticalMapMobKind kind) => kind switch
        {
            TacticalMapMobKind.Crab => Crab,
            TacticalMapMobKind.Shark => Shark,
            _ => Wolf
        };

        public static Color WithAlpha(Color color, float alpha) =>
            new(color.r, color.g, color.b, color.a * alpha);

        public static bool TryClassify(
            string definitionId, out TacticalMapMarkerKind kind)
        {
            if (definitionId == "campfire.spot")
            {
                kind = TacticalMapMarkerKind.Camp;
                return true;
            }

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
        public readonly List<TacticalMapDroppedItem> DroppedItems = new();
        public readonly List<TacticalMapMob> Mobs = new();
        public readonly List<TacticalMapPerson> People = new();
        public readonly List<TacticalMapUnknownPerson> UnknownPeople = new();
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
            DroppedItems.Clear();
            Mobs.Clear();
            People.Clear();
            UnknownPeople.Clear();
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
        Resource,
        Camp,
        Item
    }

    internal readonly struct TacticalMapMarker
    {
        public readonly TileCoord Tile;
        public readonly Float2 Center;
        public readonly TacticalMapMarkerKind Kind;
        public readonly bool Live;
        public readonly bool Lit;
        public readonly bool AlwaysKnown;

        public TacticalMapMarker(
            TileCoord tile,
            Float2 anchor,
            TacticalMapMarkerKind kind,
            bool live,
            bool lit,
            bool alwaysKnown)
        {
            Tile = tile;
            Center = anchor;
            Kind = kind;
            Live = live;
            Lit = lit;
            AlwaysKnown = alwaysKnown;
        }
    }

    internal readonly struct TacticalMapDroppedItem
    {
        public readonly TileCoord Tile;
        public readonly Float2 Center;
        public readonly int ObjectId;
        public readonly string DefinitionId;
        public readonly Sprite? Icon;
        public readonly bool Live;
        public readonly int Importance;
        public readonly bool Overflow;

        public TacticalMapDroppedItem(
            TileCoord tile,
            int objectId,
            string definitionId,
            Sprite? icon,
            bool live,
            Float2 anchor,
            int importance,
            bool overflow = false)
        {
            Tile = tile;
            Center = anchor;
            ObjectId = objectId;
            DefinitionId = definitionId;
            Icon = icon;
            Live = live;
            Importance = importance;
            Overflow = overflow;
        }
    }

    internal enum TacticalMapMobKind
    {
        Wolf,
        Crab,
        Shark,
        Other
    }

    internal readonly struct TacticalMapMob
    {
        public readonly int Id;
        public readonly bool IsCrab;
        public readonly Float2 Position;
        public readonly TacticalMapMobKind Kind;

        public TacticalMapMob(
            int id, bool isCrab, Float2 position, TacticalMapMobKind kind)
        {
            Id = id;
            IsCrab = isCrab;
            Position = position;
            Kind = kind;
        }

        public static TacticalMapMobKind Classify(string mobId)
        {
            if (string.Equals(mobId, "crab", StringComparison.OrdinalIgnoreCase))
            {
                return TacticalMapMobKind.Crab;
            }

            if (string.Equals(mobId, "shark", StringComparison.OrdinalIgnoreCase))
            {
                return TacticalMapMobKind.Shark;
            }

            if (mobId.IndexOf("dog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mobId.IndexOf("wolf", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TacticalMapMobKind.Wolf;
            }

            return TacticalMapMobKind.Other;
        }
    }

    internal readonly struct TacticalMapPerson
    {
        public readonly int NpcId;
        public readonly Float2 Position;
        public readonly bool Owned;
        public readonly bool Hostile;
        public readonly bool Selected;
        public readonly Texture2D? Portrait;
        public readonly bool Dead;

        public TacticalMapPerson(
            int npcId,
            Float2 position,
            bool owned,
            bool hostile,
            bool selected,
            Texture2D? portrait,
            bool dead)
        {
            NpcId = npcId;
            Position = position;
            Owned = owned;
            Hostile = hostile;
            Selected = selected;
            Portrait = portrait;
            Dead = dead;
        }
    }

    internal readonly struct TacticalMapUnknownPerson
    {
        public readonly int NpcId;
        public readonly TileCoord Tile;
        public readonly Float2 Position;

        public TacticalMapUnknownPerson(int npcId, TileCoord tile)
        {
            NpcId = npcId;
            Tile = tile;
            Position = HexSpatialMath.TileToWorld(tile);
        }
    }
}
