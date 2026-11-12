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
    /// очаг, шкаф, станции, грядка, факел и современная мебель рисуются одним
    /// путём.
    ///
    /// ⭐ Возвращается НЕЙТРАЛЬНЫЙ корень (#722). Вызывающий пишет в него позицию
    /// и поворот предмета, а авторская поза модели (импортная обёртка FBX
    /// X=-90, собственный поворот префаба) живёт в дочернем узле — ровно как у
    /// мирового рендера. Раньше поворот footprint писался прямо в экземпляр
    /// префаба и затирал его авторскую позу: современная мебель в превью лежала
    /// на боку, а после установки вставала правильно.
    /// </summary>
    public static class BuildCatalogFurnitureFactory
    {
        public static GameObject? Build(string definitionId)
        {
            var model = BuildModel(definitionId);
            if (model == null) return null;
            var root = new GameObject($"Catalog preview {definitionId}");
            model.transform.SetParent(root.transform, false);
            return root;
        }

        private static GameObject? BuildModel(string definitionId)
        {
            if (PlantGrowthCatalog.TryGetBySeed(definitionId, out _)) return PlantingBlueprintMarker.Create();
            if (definitionId == ContentIds.BedBasic) return HutFurnitureFactory.BuildBed();
            if (definitionId == "furniture.hearth") return HutFurnitureFactory.BuildHearth();
            if (definitionId == ContentIds.Wardrobe) return WardrobeAssembly.BuildFinished();
            if (definitionId == ContentIds.HeadPike)
            {
                var model = WorldPropResources.Build(definitionId);
                if (model == null) return null;
                var root = new GameObject($"Object {definitionId}");
                model.transform.SetParent(root.transform, false);
                return root;
            }
            if (definitionId == ContentIds.StorageChest)
            {
                // §151.6 r2: the same authored chest the world renderer draws,
                // shown closed.
                var prefab = ModernApartmentView.LoadPrefab(definitionId);
                if (prefab == null) return null;
                var chest = Object.Instantiate(prefab);
                BlueprintArchitectureFactory.ConfigureAuthoredDoor(chest, startOpen: false);
                return chest;
            }
            if (PortalWorld.IsFurniture(definitionId))
            {
                var prefab = ModernApartmentView.LoadPrefab(definitionId);
                return prefab != null ? Object.Instantiate(prefab) : null;
            }

            if (BedAssembly.IsAssembled(definitionId)) return BedAssembly.BuildFinished(definitionId);
            return BuildWorldProp(definitionId);
        }

        /// <summary>Предмет без собственной сборки (факел): та же модель и тот
        /// же размер ObjectFit, что у лежащего/воткнутого в мире, поставленная
        /// низом на точку.</summary>
        private static GameObject? BuildWorldProp(string definitionId)
        {
            var prefab = WorldPropResources.Load(definitionId);
            if (prefab == null) return null;
            var holder = new GameObject("prop");
            var instance = Object.Instantiate(prefab, holder.transform);
            if (!ObjectFit.HasRenderableGeometry(instance))
            {
                Object.Destroy(holder);
                return null;
            }

            instance.transform.localScale *= ObjectFit.FitScaleFactor(instance, definitionId);
            instance.transform.localPosition = Vector3.zero;
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                instance.transform.localPosition += Vector3.up * (holder.transform.position.y - bounds.min.y);
            }

            return holder;
        }
    }
}
