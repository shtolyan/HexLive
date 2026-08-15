using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public enum PlanningMarkerLayer
    {
        Architecture,
        Furniture
    }

    public readonly struct PlanningMarkerPoint
    {
        public readonly Float2 Position;
        public readonly PlanningMarkerLayer Layer;

        public PlanningMarkerPoint(Float2 position, PlanningMarkerLayer layer)
        {
            Position = position;
            Layer = layer;
        }
    }

    /// <summary>
    /// Pure marker layout shared by the editor and the future production site
    /// renderer. Markers are derived presentation, not saved world objects.
    /// </summary>
    public static class BlueprintPlanningMarkers
    {
        private const float FurniturePadding = 0.055f;
        private const float MinimumFurnitureHalfExtent = 0.18f;

        public static IReadOnlyList<PlanningMarkerPoint> ForElement(BlueprintElementData element)
        {
            if (element == null) return Array.Empty<PlanningMarkerPoint>();
            var definitionId = DefinitionId(element.Kind);
            if (!BuildCatalogDefinition.TryGet(definitionId, out var catalog) ||
                !catalog.ShowPlanningMarkers) return Array.Empty<PlanningMarkerPoint>();

            IEnumerable<HexBuildNodeKey> nodes = element.Kind switch
            {
                BlueprintElementKind.FloorSector => new[]
                {
                    BlueprintGeometry.HexCenter(element.FloorSector.Hex),
                    BlueprintGeometry.HexCorner(element.FloorSector.Hex, element.FloorSector.Sector),
                    BlueprintGeometry.HexCorner(element.FloorSector.Hex, element.FloorSector.Sector + 1)
                },
                BlueprintElementKind.Support => new[] { element.Node },
                BlueprintElementKind.RoofSector => BlueprintGeometry.RoofSupports(element.RoofSector),
                _ => new[] { element.Segment.A, element.Segment.B }
            };
            return nodes.Distinct().Select(node => new PlanningMarkerPoint(
                BlueprintGeometry.ToWorld(node), PlanningMarkerLayer.Architecture)).ToArray();
        }

        public static IReadOnlyList<PlanningMarkerPoint> ForFurniture(FurniturePlacementData item)
        {
            if (item == null ||
                !BuildCatalogDefinition.TryGet(item.DefinitionId, out var catalog) ||
                !catalog.ShowPlanningMarkers) return Array.Empty<PlanningMarkerPoint>();

            var localPoints = BlueprintFurnitureFootprints.LocalOffsets(item.DefinitionId)
                .Select(BlueprintGeometry.JunctionToWorld).ToArray();
            if (localPoints.Length == 0) return Array.Empty<PlanningMarkerPoint>();
            var minX = localPoints.Min(point => point.X);
            var maxX = localPoints.Max(point => point.X);
            var minY = localPoints.Min(point => point.Y);
            var maxY = localPoints.Max(point => point.Y);
            var centreX = (minX + maxX) * 0.5f;
            var centreY = (minY + maxY) * 0.5f;
            var halfX = MathF.Max(MinimumFurnitureHalfExtent, (maxX - minX) * 0.5f + FurniturePadding);
            var halfY = MathF.Max(MinimumFurnitureHalfExtent, (maxY - minY) * 0.5f + FurniturePadding);
            var primary = BlueprintGeometry.JunctionToWorld(item.PrimaryJunction);
            var radians = BlueprintGeometry.NormalizeSector(item.YawStep) * MathF.PI / 3f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);
            var corners = new[]
            {
                new Float2(centreX - halfX, centreY - halfY),
                new Float2(centreX + halfX, centreY - halfY),
                new Float2(centreX + halfX, centreY + halfY),
                new Float2(centreX - halfX, centreY + halfY)
            };
            return corners.Select(corner => new PlanningMarkerPoint(new Float2(
                    primary.X + corner.X * cos - corner.Y * sin,
                    primary.Y + corner.X * sin + corner.Y * cos),
                PlanningMarkerLayer.Furniture)).ToArray();
        }

        private static string DefinitionId(BlueprintElementKind kind) => kind switch
        {
            BlueprintElementKind.Support => "architecture.support.wood",
            BlueprintElementKind.FloorSector => "architecture.floor.board",
            BlueprintElementKind.Wall => "architecture.wall.wood",
            BlueprintElementKind.Window => "architecture.window.wood",
            BlueprintElementKind.Door => "architecture.door.wood",
            BlueprintElementKind.RoofSector => "architecture.roof.palm",
            _ => string.Empty
        };
    }
}
