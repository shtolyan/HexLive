using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BloodEffectsPack
{
    public static class BloodModifierUtility_URP
    {
        public static void ApplySettingsToMaterial(Material mat, BloodModifierSettings_URP settings)
        {
            if (mat == null || settings == null)
                return;

            mat.SetColor("_BaseColor", settings.color);
            mat.SetFloat("_Smoothness", settings.useSpecularity ? settings.smoothness : 0.0f);
            mat.SetFloat("_ColorIntensity", settings.colorIntensity);
            mat.SetFloat("_AlbedoPower", settings.albedoPower);
            mat.SetFloat("_AmbientColorIntensity", settings.ambientColorIntensity);
            mat.SetFloat("_HueShift", settings.hueShift);
        }

        public static void ApplySettingsToSharedMaterial(Material mat, BloodModifierSettings_URP settings, HashSet<Material> processedMaterials)
        {
            if (mat == null || settings == null)
                return;

            if (processedMaterials != null && !processedMaterials.Add(mat))
                return;

            ApplySettingsToMaterial(mat, settings);
        }

        public static void ApplySettingsToGameObjectShared(GameObject root, BloodModifierSettings_URP settings)
        {
            if (root == null || settings == null)
                return;

            HashSet<Material> processedMaterials = new HashSet<Material>();

            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            DecalProjector[] decalProjectors = root.GetComponentsInChildren<DecalProjector>(true);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                var main = particleSystems[i].main;
                if (settings.effectType == BloodModifier_URP.EffectType.Splash)
                    main.gravityModifierMultiplier = settings.gravityScale;

                ParticleSystemRenderer rend = particleSystems[i].GetComponent<ParticleSystemRenderer>();
                if (rend != null)
                    ApplySettingsToSharedMaterial(rend.sharedMaterial, settings, processedMaterials);
            }

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                ApplySettingsToSharedMaterial(meshRenderers[i].sharedMaterial, settings, processedMaterials);
            }

            for (int i = 0; i < decalProjectors.Length; i++)
            {
                ApplySettingsToSharedMaterial(decalProjectors[i].material, settings, processedMaterials);
            }
        }

        public static void ApplySettingsToGameObjectRuntime(GameObject root, BloodModifierSettings_URP settings)
        {
            if (root == null || settings == null)
                return;

            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            DecalProjector[] decalProjectors = root.GetComponentsInChildren<DecalProjector>(true);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                var main = particleSystems[i].main;
                if (settings.effectType == BloodModifier_URP.EffectType.Splash)
                    main.gravityModifierMultiplier = settings.gravityScale;

                ParticleSystemRenderer rend = particleSystems[i].GetComponent<ParticleSystemRenderer>();
                if (rend != null)
                    ApplySettingsToMaterial(rend.material, settings);
            }

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                ApplySettingsToMaterial(meshRenderers[i].material, settings);
            }

            for (int i = 0; i < decalProjectors.Length; i++)
            {
                if (decalProjectors[i] != null && decalProjectors[i].material != null)
                    ApplySettingsToMaterial(decalProjectors[i].material, settings);
            }
        }
    }
}