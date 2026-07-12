using System.Collections.Generic;
using UnityEngine;

namespace BloodEffectsPack
{
    public static class BloodModifierUtility
    {
        public static void ApplySettingsToMaterial(Material mat, BloodModifierSettings settings)
        {
            if (mat == null || settings == null)
                return;

            mat.SetColor("_Color", settings.color);
            mat.SetInt("_UseSpecularity", settings.useSpecularity ? 1 : 0);
            mat.SetFloat("_Smoothness", settings.smoothness);
            mat.SetFloat("_ColorIntensity", settings.colorIntensity);
            mat.SetFloat("_AlbedoPower", settings.albedoPower);
            mat.SetFloat("_AmbientColorIntensity", settings.ambientColorIntensity);
            mat.SetFloat("_HueShift", settings.hueShift);
        }

        public static void ApplySettingsToSharedMaterial(Material mat, BloodModifierSettings settings, HashSet<Material> processedMaterials)
        {
            if (mat == null || settings == null)
                return;

            if (processedMaterials != null && !processedMaterials.Add(mat))
                return;

            ApplySettingsToMaterial(mat, settings);
        }

        public static void ApplySettingsToGameObjectShared(GameObject root, BloodModifierSettings settings)
        {
            if (root == null || settings == null)
                return;

            HashSet<Material> processedMaterials = new HashSet<Material>();

            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            Projector[] projectors = root.GetComponentsInChildren<Projector>(true);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                var main = particleSystems[i].main;
                if (settings.effectType == BloodModifier.EffectType.Splash)
                    main.gravityModifierMultiplier = settings.gravityScale;

                ParticleSystemRenderer rend = particleSystems[i].GetComponent<ParticleSystemRenderer>();
                if (rend != null)
                    ApplySettingsToSharedMaterial(rend.sharedMaterial, settings, processedMaterials);
            }

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                ApplySettingsToSharedMaterial(meshRenderers[i].sharedMaterial, settings, processedMaterials);
            }

            for (int i = 0; i < projectors.Length; i++)
            {
                ApplySettingsToSharedMaterial(projectors[i].material, settings, processedMaterials);
            }
        }

        public static void ApplySettingsToGameObjectRuntime(GameObject root, BloodModifierSettings settings)
        {
            if (root == null || settings == null)
                return;

            ParticleSystem[] particleSystems = root.GetComponentsInChildren<ParticleSystem>(true);
            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            Projector[] projectors = root.GetComponentsInChildren<Projector>(true);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                var main = particleSystems[i].main;
                if (settings.effectType == BloodModifier.EffectType.Splash)
                    main.gravityModifierMultiplier = settings.gravityScale;

                ParticleSystemRenderer rend = particleSystems[i].GetComponent<ParticleSystemRenderer>();
                if (rend != null)
                    ApplySettingsToMaterial(rend.material, settings);
            }

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                ApplySettingsToMaterial(meshRenderers[i].material, settings);
            }

            for (int i = 0; i < projectors.Length; i++)
            {
                if (projectors[i] != null && projectors[i].material != null)
                    ApplySettingsToMaterial(projectors[i].material, settings);
            }
        }
    }
}