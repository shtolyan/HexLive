using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace BloodEffectsPack
{
    public class BloodModifier_URP : MonoBehaviour
    {
        public enum EffectType
        {
            Splash,
            Decal
        }

        public BloodModifierSettings_URP settings = new BloodModifierSettings_URP();
        public BloodPreset[] decalPresets;
        public BloodPreset[] splashPresets;

        private readonly Dictionary<DecalProjector, Material> originalProjectorMaterials = new Dictionary<DecalProjector, Material>();
        private readonly Dictionary<DecalProjector, Material> runtimeProjectorMaterials = new Dictionary<DecalProjector, Material>();

        public void ApplyPreset(BloodPreset preset)
        {
            if (preset == null)
            {
                Debug.LogWarning("ApplyPreset failed because preset is null.", this);
                return;
            }

            settings.CopyFromPreset(preset, settings.effectType);

#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorUtility.SetDirty(this);
#endif
        }

        public void OverrideSettings(BloodModifierSettings_URP overrideSettings)
        {
            if (overrideSettings == null)
                return;

            settings.CopyFrom(overrideSettings);

#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorUtility.SetDirty(this);
#endif
        }

        public void ApplyToSharedMaterialsInEditor()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("ApplyToSharedMaterialsInEditor() should not be executed during Play Mode.", this);
                return;
            }

            BloodModifierUtility_URP.ApplySettingsToGameObjectShared(gameObject, settings);

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        public void ApplyToMaterialInstancesInRuntime()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("ApplyToMaterialInstancesInRuntime() can only be executed in Play Mode.", this);
                return;
            }

            ParticleSystem[] particleSystems = GetComponentsInChildren<ParticleSystem>(true);
            MeshRenderer[] meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
            DecalProjector[] decalProjectors = GetComponentsInChildren<DecalProjector>(true);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                var mainModule = particleSystems[i].main;
                if (settings.effectType == EffectType.Splash)
                    mainModule.gravityModifierMultiplier = settings.gravityScale;

                ParticleSystemRenderer rend = particleSystems[i].GetComponent<ParticleSystemRenderer>();
                if (rend == null)
                    continue;

                Material instanceMat = rend.material;
                BloodModifierUtility_URP.ApplySettingsToMaterial(instanceMat, settings);
            }

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                Material instanceMat = meshRenderers[i].material;
                BloodModifierUtility_URP.ApplySettingsToMaterial(instanceMat, settings);
            }

            for (int i = 0; i < decalProjectors.Length; i++)
            {
                DecalProjector projector = decalProjectors[i];
                Material runtimeMat = GetOrCreateRuntimeProjectorMaterial(projector);
                BloodModifierUtility_URP.ApplySettingsToMaterial(runtimeMat, settings);
            }
        }

        public bool ApplyPresetByIndexForCurrentEffectType(int presetIndex)
        {
            BloodPreset[] presets = settings.effectType == EffectType.Decal ? decalPresets : splashPresets;

            if (presets == null || presets.Length == 0)
                return false;

            if (presetIndex < 0 || presetIndex >= presets.Length)
                return false;

            BloodPreset preset = presets[presetIndex];
            if (preset == null)
                return false;

            ApplyPreset(preset);
            return true;
        }

        public bool ApplyPresetByIndexAndApplyToSharedMaterialsInEditor(int presetIndex)
        {
            if (!ApplyPresetByIndexForCurrentEffectType(presetIndex))
                return false;

            ApplyToSharedMaterialsInEditor();
            return true;
        }

        public bool ApplyPresetByIndexAndApplyToRuntime(int presetIndex)
        {
            if (!ApplyPresetByIndexForCurrentEffectType(presetIndex))
                return false;

            ApplyToMaterialInstancesInRuntime();
            return true;
        }

        private Material GetOrCreateRuntimeProjectorMaterial(DecalProjector projector)
        {
            if (projector == null)
                return null;

            if (runtimeProjectorMaterials.TryGetValue(projector, out Material runtimeMat) && runtimeMat != null)
                return runtimeMat;

            Material sourceMat = projector.material;
            if (sourceMat == null)
                return null;

            if (!originalProjectorMaterials.ContainsKey(projector))
                originalProjectorMaterials.Add(projector, sourceMat);

            runtimeMat = new Material(sourceMat);
            runtimeMat.name = sourceMat.name + " (Runtime Instance)";
            projector.material = runtimeMat;
            runtimeProjectorMaterials[projector] = runtimeMat;

            return runtimeMat;
        }

        private void RestoreRuntimeProjectorMaterials()
        {
            foreach (var pair in originalProjectorMaterials)
            {
                if (pair.Key != null)
                    pair.Key.material = pair.Value;
            }

            foreach (var pair in runtimeProjectorMaterials)
            {
                if (pair.Value == null)
                    continue;

                if (Application.isPlaying)
                    Destroy(pair.Value);
                else
                    DestroyImmediate(pair.Value);
            }

            originalProjectorMaterials.Clear();
            runtimeProjectorMaterials.Clear();
        }

        private void OnDisable()
        {
            RestoreRuntimeProjectorMaterials();
        }

        private void OnDestroy()
        {
            RestoreRuntimeProjectorMaterials();
        }
    }
}