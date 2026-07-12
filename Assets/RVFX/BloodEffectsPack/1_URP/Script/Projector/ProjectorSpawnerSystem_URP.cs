using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BloodEffectsPack
{
    public class ProjectorSpawnerSystem_URP : MonoBehaviour
    {
        [HideInInspector] public int renderingLayerMask = 1;

        [Header("Loop")]
        public bool isLoop = false;
        public float spawnerLifetime = Mathf.Infinity;
        private float spawnerLifetimeCounter = 0.0f;
        private bool stopSpawning = false;

        public GameObject sourcePrefab;

        [Header("Lifetime")]
        public float lifetime_min = 1.0f;
        public float lifetime_max = 1.0f;

        [Header("StartPos")]
        public Vector3 startPosOffset = Vector3.zero;

        [Header("Size")]
        public float startSize_min = 1.0f;
        public float startSize_max = 1.0f;

        [Header("Rotation")]
        public float startRotation_min = 0.0f;
        public float startRotation_max = 0.0f;

        [Header("CurveControl")]
        public AnimationCurve frameCurve;
        public AnimationCurve scaleCurve;
        public AnimationCurve opacityCurve;

        [Header("Blood Modifier")]
        public BloodModifierSettings_URP bloodSettings = new BloodModifierSettings_URP();

        [SerializeField]
        private BloodModifierSettings_URP appliedBloodSettings = new BloodModifierSettings_URP();

        public BloodPreset[] decalPresets;
        public BloodPreset[] splashPresets;

        [System.Serializable]
        public class SpawnOption
        {
            [HideInInspector] public GameObject currentPrefab;
            [HideInInspector] public DecalProjector currentProjector;
            [HideInInspector] public ProjectorSpriteController_URP currentSpriteController;
            [HideInInspector] public ProjectorPrioritySetter_URP currentPrioritySetter;
            [HideInInspector] public BloodModifier_URP currentBloodModifier;

            [Header("Delay")]
            public float delay_min = 0.0f;
            public float delay_max = 0.0f;

            [Header("Lifetime")]
            [HideInInspector] public float lifetimeCounter = 0.0f;

            [HideInInspector] public bool isRunning = false;
        }

        private readonly List<Coroutine> spawnCoroutines = new List<Coroutine>();

        [Header("Spawn Options")]
        public List<SpawnOption> spawnOptions = new List<SpawnOption>();

        public BloodModifierSettings_URP BloodSettings => bloodSettings;
        public BloodModifierSettings_URP AppliedBloodSettings => appliedBloodSettings;
        public BloodPreset[] DecalPresets => decalPresets;
        public BloodPreset[] SplashPresets => splashPresets;

        private void OnEnable()
        {
            StopAllCoroutines();

            spawnCoroutines.Clear();
            spawnerLifetimeCounter = 0.0f;
            stopSpawning = false;

            foreach (var option in spawnOptions)
            {
                if (option == null)
                    continue;

                option.lifetimeCounter = 0.0f;
                option.isRunning = false;

                if (option.currentPrefab != null)
                    Destroy(option.currentPrefab);

                option.currentPrefab = null;
                option.currentProjector = null;
                option.currentSpriteController = null;
                option.currentPrioritySetter = null;
                option.currentBloodModifier = null;
            }

            foreach (var option in spawnOptions)
            {
                if (option == null)
                    continue;

                Coroutine coroutine = StartCoroutine(Spawn(option));
                spawnCoroutines.Add(coroutine);
            }
        }

        private void Update()
        {
            if (!stopSpawning)
            {
                spawnerLifetimeCounter += Time.deltaTime;

                if (spawnerLifetimeCounter >= spawnerLifetime)
                    stopSpawning = true;
            }

            if (stopSpawning && AreAllSpawnOptionsFinished())
                Destroy(gameObject);
        }

        private IEnumerator Spawn(SpawnOption option)
        {
            if (option == null)
                yield break;

            option.isRunning = true;

            float delay = Random.Range(option.delay_min, option.delay_max);
            yield return new WaitForSeconds(delay);

            if (stopSpawning)
            {
                option.isRunning = false;
                yield break;
            }

            float lifetime = Random.Range(lifetime_min, lifetime_max);
            float startProjectorSize = Random.Range(startSize_min, startSize_max);
            float startRotation = Random.Range(startRotation_min, startRotation_max);

            option.currentPrefab = Instantiate(sourcePrefab);
            if (option.currentPrefab == null)
            {
                option.isRunning = false;
                yield break;
            }

            DecalProjector currentProjector = option.currentPrefab.GetComponent<DecalProjector>();
            if (currentProjector == null)
            {
                Destroy(option.currentPrefab);
                option.currentPrefab = null;
                option.isRunning = false;
                yield break;
            }

            option.currentProjector = currentProjector;
            option.currentSpriteController = option.currentPrefab.GetComponent<ProjectorSpriteController_URP>();
            option.currentPrioritySetter = option.currentPrefab.GetComponent<ProjectorPrioritySetter_URP>();
            option.currentBloodModifier = option.currentPrefab.GetComponent<BloodModifier_URP>();

            if (currentProjector.material != null)
                currentProjector.material = Instantiate(currentProjector.material);

            currentProjector.renderingLayerMask = (uint)renderingLayerMask;

            option.currentPrefab.transform.SetParent(transform);
            option.currentPrefab.transform.localPosition = startPosOffset;
            option.currentPrefab.transform.localScale = Vector3.one;
            option.currentPrefab.transform.localEulerAngles = new Vector3(90f, 0f, 0.0f);
            option.currentPrefab.transform.RotateAround(transform.position, Vector3.up, startRotation);

            if (option.currentPrioritySetter != null)
                option.currentPrioritySetter.SetPriority();

            ApplyAppliedBloodSettingsToOption(option);

            while (true)
            {
                option.lifetimeCounter += Time.deltaTime;

                float safeLifetime = Mathf.Max(0.0001f, lifetime);
                float samplePos = Mathf.Clamp01(option.lifetimeCounter / safeLifetime);

                float scaleValue = scaleCurve != null ? scaleCurve.Evaluate(samplePos) : 1.0f;
                int frameValue = frameCurve != null ? Mathf.FloorToInt(frameCurve.Evaluate(samplePos)) : 0;
                float opacityValue = opacityCurve != null ? opacityCurve.Evaluate(samplePos) : 1.0f;

                if (option.currentSpriteController != null)
                    option.currentSpriteController.SetFrameIndex(frameValue);

                if (currentProjector != null)
                {
                    currentProjector.fadeFactor = opacityValue;
                    currentProjector.size = new Vector3(
                        scaleValue * startProjectorSize,
                        scaleValue * startProjectorSize,
                        10.0f);
                    currentProjector.pivot = new Vector3(0f, 0f, 5f);
                }

                if (option.lifetimeCounter > lifetime)
                {
                    if (isLoop && !stopSpawning)
                    {
                        option.lifetimeCounter -= lifetime;

                        option.currentPrefab.transform.localPosition = startPosOffset;
                        startRotation = Random.Range(startRotation_min, startRotation_max);
                        option.currentPrefab.transform.localEulerAngles = new Vector3(90f, 0f, 0.0f);
                        option.currentPrefab.transform.RotateAround(transform.position, Vector3.up, startRotation);

                        if (option.currentPrioritySetter != null)
                            option.currentPrioritySetter.SetPriority();

                        ApplyAppliedBloodSettingsToOption(option);
                    }
                    else
                    {
                        break;
                    }
                }

                yield return null;
            }

            if (option.currentPrefab != null)
                Destroy(option.currentPrefab);

            option.currentPrefab = null;
            option.currentProjector = null;
            option.currentSpriteController = null;
            option.currentPrioritySetter = null;
            option.currentBloodModifier = null;
            option.isRunning = false;
        }

        private bool AreAllSpawnOptionsFinished()
        {
            for (int i = 0; i < spawnOptions.Count; i++)
            {
                SpawnOption option = spawnOptions[i];
                if (option != null && option.isRunning)
                    return false;
            }

            return true;
        }

        private void ApplyAppliedBloodSettingsToOption(SpawnOption option)
        {
            if (option == null || option.currentPrefab == null)
                return;

            if (option.currentBloodModifier != null)
            {
                option.currentBloodModifier.OverrideSettings(appliedBloodSettings);

                if (Application.isPlaying)
                    option.currentBloodModifier.ApplyToMaterialInstancesInRuntime();
#if UNITY_EDITOR
                else
                    option.currentBloodModifier.ApplyToSharedMaterialsInEditor();
#endif
            }
            else
            {
                if (Application.isPlaying)
                    BloodModifierUtility_URP.ApplySettingsToGameObjectRuntime(option.currentPrefab, appliedBloodSettings);
#if UNITY_EDITOR
                else
                    BloodModifierUtility_URP.ApplySettingsToGameObjectShared(option.currentPrefab, appliedBloodSettings);
#endif
            }
        }

        public void LoadPresetValuesToSpawnerSettings(BloodPreset preset)
        {
            if (preset == null)
                return;

            bloodSettings.CopyFromPreset(preset, bloodSettings.effectType);
        }

        public bool LoadPresetValuesByIndexForCurrentEffectType(int presetIndex)
        {
            BloodPreset[] presets = bloodSettings.effectType == BloodModifier_URP.EffectType.Decal
                ? decalPresets
                : splashPresets;

            if (presets == null || presets.Length == 0)
                return false;

            if (presetIndex < 0 || presetIndex >= presets.Length)
                return false;

            BloodPreset preset = presets[presetIndex];
            if (preset == null)
                return false;

            LoadPresetValuesToSpawnerSettings(preset);
            return true;
        }

        public void ApplyPresetByIndexAndApplyToRuntime(int presetIndex)
        {
            if (LoadPresetValuesByIndexForCurrentEffectType(presetIndex))
                ApplyCurrentBloodSettingsToAllActiveOptions();
        }

        public void ApplyCurrentBloodSettingsToAllActiveOptions()
        {
            CommitBloodSettingsForApply();

            for (int i = 0; i < spawnOptions.Count; i++)
            {
                SpawnOption option = spawnOptions[i];
                if (option == null || option.currentPrefab == null)
                    continue;

                ApplyAppliedBloodSettingsToOption(option);
            }
        }

        public void OverrideBloodSettings(BloodModifierSettings_URP overrideSettings)
        {
            if (overrideSettings == null)
                return;

            bloodSettings.CopyFrom(overrideSettings);
        }

        public void CommitBloodSettingsForApply()
        {
            appliedBloodSettings.CopyFrom(bloodSettings);
        }

#if UNITY_EDITOR
        public void ApplyCurrentBloodSettingsToSourcePrefabEditorOnly()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("ApplyCurrentBloodSettingsToSourcePrefabEditorOnly can only be used in Edit Mode.", this);
                return;
            }

            if (sourcePrefab == null)
                return;

            CommitBloodSettingsForApply();

            BloodModifier_URP modifier = sourcePrefab.GetComponent<BloodModifier_URP>();
            if (modifier != null)
            {
                modifier.OverrideSettings(appliedBloodSettings);
                modifier.ApplyToSharedMaterialsInEditor();
                return;
            }

            BloodModifierUtility_URP.ApplySettingsToGameObjectShared(sourcePrefab, appliedBloodSettings);
        }
#endif

        private void OnDestroy()
        {
            StopAllCoroutines();

            for (int i = 0; i < spawnOptions.Count; i++)
            {
                SpawnOption option = spawnOptions[i];
                if (option != null && option.currentPrefab != null)
                    Destroy(option.currentPrefab);
            }

            spawnCoroutines.Clear();
        }
    }
}