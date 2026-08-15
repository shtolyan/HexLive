#nullable enable
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Shared preview factory for catalog furniture. It resolves the same
    /// authored assemblies used by the ordinary world renderer; the catalog
    /// never owns a prefab or a correction transform (§120).
    /// </summary>
    public static class BuildCatalogFurnitureFactory
    {
        public static GameObject? Build(string definitionId)
        {
            if (definitionId == ContentIds.BedBasic) return HutFurnitureFactory.BuildBed();
            if (definitionId == "furniture.hearth") return HutFurnitureFactory.BuildHearth();
            if (definitionId == ContentIds.Wardrobe) return WardrobeAssembly.BuildFinished();
            return BedAssembly.IsAssembled(definitionId)
                ? BedAssembly.BuildFinished(definitionId)
                : null;
        }
    }
}
