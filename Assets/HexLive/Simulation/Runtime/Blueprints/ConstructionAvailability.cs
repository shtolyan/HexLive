using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime.Blueprints
{
    public enum ConstructionAvailability
    {
        Ready,
        WaitingForFloor,
        WaitingForSupports,
        WaitingForResources,
        InProgress,
        Complete,
        Invalid
    }

    /// <summary>
    /// Immutable dependency cache for one blueprint evaluation pass. The AI
    /// builds it when construction state changes and then checks candidates in
    /// O(footprint) instead of rescanning every element for every NPC (§120).
    /// Resource delivery/progress are separate gates owned by the build-site.
    /// </summary>
    public sealed class ConstructionDependencyIndex
    {
        private readonly BuildingBlueprintDraft _draft;
        private readonly HashSet<string> _completedIds;
        private readonly HashSet<FloorSectorKey> _completedFloors;
        private readonly HashSet<HexBuildNodeKey> _completedSupports;

        public ConstructionDependencyIndex(
            BuildingBlueprintDraft draft, IEnumerable<string> completedElementIds)
        {
            _draft = draft ?? throw new ArgumentNullException(nameof(draft));
            _completedIds = new HashSet<string>(
                completedElementIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            _completedFloors = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.FloorSector && _completedIds.Contains(element.Id))
                .Select(element => element.FloorSector).ToHashSet();
            _completedSupports = draft.Elements.Where(element =>
                    element.Kind == BlueprintElementKind.Support && _completedIds.Contains(element.Id))
                .Select(element => element.Node).ToHashSet();
        }

        public ConstructionAvailability ForElement(string elementId)
        {
            if (string.IsNullOrWhiteSpace(elementId)) return ConstructionAvailability.Invalid;
            if (_completedIds.Contains(elementId)) return ConstructionAvailability.Complete;

            var architecture = _draft.Elements.FirstOrDefault(element => element.Id == elementId);
            if (architecture != null)
            {
                if (architecture.Kind != BlueprintElementKind.RoofSector)
                    return ConstructionAvailability.Ready;
                return BlueprintGeometry.RoofSupports(architecture.RoofSector)
                    .All(_completedSupports.Contains)
                    ? ConstructionAvailability.Ready
                    : ConstructionAvailability.WaitingForSupports;
            }

            var furniture = _draft.Furniture.FirstOrDefault(item => item.Id == elementId);
            if (furniture == null) return ConstructionAvailability.Invalid;
            if (!BuildCatalogDefinition.TryGet(furniture.DefinitionId, out var catalogEntry) ||
                catalogEntry.RequiresCompletedFloor)
            {
                return BlueprintFurnitureFootprints.OccupiedJunctions(furniture)
                    .All(junction => BlueprintGeometry.IsSupportedByFloor(junction, _completedFloors))
                    ? ConstructionAvailability.Ready
                    : ConstructionAvailability.WaitingForFloor;
            }

            return ConstructionAvailability.Ready;
        }
    }
}
