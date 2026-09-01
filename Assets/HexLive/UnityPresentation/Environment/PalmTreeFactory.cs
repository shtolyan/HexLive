#nullable enable
using System;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: the standing palm is the approved <c>palm_final</c>
    /// composition introduced by b3b47b4a: the Kenney trunk from the first
    /// in-game palm plus the separately authored pinnate-frond crown. The
    /// native FBX is a build-safe mirror of that exact GLB, not another model.
    /// It is authored at 1:1 world size and must not be fit-scaled.
    /// </summary>
    public static class PalmTreeFactory
    {
        public static bool IsPalm(string definitionId) =>
            definitionId is "tree.palm";

        public static GameObject? Build(string definitionId)
        {
            var prefab = WorldPropResources.Load(definitionId);
            if (prefab == null || !ObjectFit.HasRenderableGeometry(prefab))
            {
                return null;
            }

            var palm = UnityEngine.Object.Instantiate(prefab);
            palm.name = $"Palm {definitionId}";
            StandingPalmCrownVisibility.Apply(palm);
            ConfigureTrunkPicking(palm);
            return palm;
        }

        // §121: активатором наведения/клика служит только ствол. Крона — сабмеш
        // LeafGreen того же рендерера, поэтому её нельзя исключить рендерером:
        // пикинг переопределяется сабмешем WoodBark — его габарит отбирает
        // кандидатов, попадание решают его треугольники. Если ствольный
        // сабмеш не нашёлся (другая раскладка меша), компонент не ставится и
        // пальма пикается по-старому — целиком, но не становится некликабельной.
        private static void ConfigureTrunkPicking(GameObject palm)
        {
            foreach (var renderer in palm.GetComponentsInChildren<Renderer>(true))
            {
                Mesh? mesh = null;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = skinned.sharedMesh;
                }
                else if (renderer.TryGetComponent<MeshFilter>(out var filter))
                {
                    mesh = filter.sharedMesh;
                }

                if (mesh == null)
                {
                    continue;
                }

                Views.WorldObjectPickBounds? pick = null;
                var materials = renderer.sharedMaterials;
                var slots = Mathf.Min(materials.Length, mesh.subMeshCount);
                for (var i = 0; i < slots; i++)
                {
                    if (!IsTrunkSurface(materials[i]))
                    {
                        continue;
                    }

                    var bounds = mesh.GetSubMesh(i).bounds;
                    if (bounds.size.sqrMagnitude <= Mathf.Epsilon)
                    {
                        continue;
                    }

                    if (pick == null)
                    {
                        pick = renderer.gameObject
                            .AddComponent<Views.WorldObjectPickBounds>();
                    }

                    pick.AddSubmesh(bounds, i);
                }
            }
        }

        private static bool IsTrunkSurface(Material? material)
        {
            if (material == null)
            {
                return false;
            }

            var surface = material.name.Replace(" (Instance)", string.Empty);
            return string.Equals(surface, "WoodBark", StringComparison.Ordinal);
        }
    }
}
