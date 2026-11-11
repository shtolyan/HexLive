#nullable enable
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Shared preview factory for catalog furniture. It resolves the same
    /// authored assemblies used by the ordinary world renderer; the catalog
    /// never owns a prefab or a correction transform (§120).
    ///
    /// §120.13: это ЕДИНСТВЕННАЯ фабрика призрака режима стройки — кровать,
    /// очаг, шкаф, станции, грядка и современная мебель рисуются одним путём.
    /// </summary>
    public static class BuildCatalogFurnitureFactory
    {
        public static GameObject? Build(string definitionId)
        {
            if (PlantGrowthCatalog.TryGetBySeed(definitionId, out _)) return PlantingBlueprintMarker.Create();
            if (definitionId == ContentIds.BedBasic) return HutFurnitureFactory.BuildBed();
            if (definitionId == "furniture.hearth") return HutFurnitureFactory.BuildHearth();
            if (definitionId == ContentIds.Wardrobe) return WardrobeAssembly.BuildFinished();
            if (PortalWorld.IsFurniture(definitionId))
            {
                var prefab = ModernApartmentView.LoadPrefab(definitionId);
                return prefab != null ? Object.Instantiate(prefab) : null;
            }

            return BedAssembly.IsAssembled(definitionId)
                ? BedAssembly.BuildFinished(definitionId)
                : null;
        }
    }
}
