using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BloodEffectsPack
{
    public class TrailProjectorSpawnerSystem_URP : MonoBehaviour
    {
        [Header("Spawn")]
        public float spawnDistance_min = 1.0f;
        public float spawnDistance_max = 1.0f;

        [Header("Duration")]
        public float duration = Mathf.Infinity;
        private float timeCounter = 0.0f;

        [Header("Size")]
        public float startSize_min = 1.0f;
        public float startSize_max = 1.0f;

        private Vector3 lastPosition;
        private float distanceTraveled = 0.0f;
        private GameObject projectorSpawnerGrp = null;

        [HideInInspector] public int renderingLayerMask = 1;
        public GameObject sourcePrefab;

        [Header("Lifetime")]
        public float lifetime_min = 1.0f;
        public float lifetime_max = 1.0f;

        [Header("StartPos")]
        public Vector3 startPosOffset = Vector3.zero;

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

        private class SpawnTask
        {
            public float lifetime;
            public float startProjectorSize;
            public float startRotation;

            public float lifetimeCounter = 0.0f;

            public GameObject currentPrefab;
            public DecalProjector currentProjector;
            public ProjectorSpriteController_URP currentSpriteController;
            public ProjectorPrioritySetter_URP currentPrioritySetter;
            public BloodModifier_URP currentBloodModifier;

            public bool prefabSpawned = false;
            public bool finished = false;
        }

        private readonly List<SpawnTask> activeTasks = new List<SpawnTask>();

        public BloodModifierSettings_URP BloodSettings => bloodSettings;
        public BloodModifierSettings_URP AppliedBloodSettings => appliedBloodSettings;
        public BloodPreset[] DecalPresets => decalPresets;
        public BloodPreset[] SplashPresets => splashPresets;

        private void Start()
        {
            projectorSpawnerGrp = new GameObject(gameObject.name + "_ProjectorSpawner_GRP");
            projectorSpawnerGrp.AddComponent<KillEffect_Trail_Projector>();
        }

        private void OnEnable()
        {
            timeCounter = 0.0f;
            distanceTraveled = 0.0f;
            lastPosition = transform.position;
        }

        private void Update()
        {
            UpdateTrailSpawner();
            UpdateSpawnTasks();
        }

        private void UpdateTrailSpawner()
        {
            timeCounter += Time.deltaTime;
            distanceTraveled += Vector3.Distance(transform.position, lastPosition);
            lastPosition = transform.position;

            float distanceThreshold = Random.Range(spawnDistance_min, spawnDistance_max);
            if (distanceTraveled >= distanceThreshold)
            {
                if (timeCounter >= duration)
                    return;

                SpawnProjector();
                distanceTraveled = 0f;
            }
        }

        private void SpawnProjector()
        {
            if (sourcePrefab == null)
                return;

            SpawnTask task = new SpawnTask
            {
                lifetime = Random.Range(lifetime_min, lifetime_max),
                startProjectorSize = Random.Range(startSize_min, startSize_max),
                startRotation = Random.Range(startRotation_min, startRotation_max)
            };

            activeTasks.Add(task);
        }

        private void UpdateSpawnTasks()
        {
            for (int i = activeTasks.Count - 1; i >= 0; i--)
            {
                SpawnTask task = activeTasks[i];

                if (task.finished)
                {
                    CleanupTask(task);
                    activeTasks.RemoveAt(i);
                    continue;
                }

                if (!task.prefabSpawned)
                {
                    CreatePrefab(task);
                    continue;
                }

                UpdateSingleTask(task);

                if (task.finished)
                {
                    CleanupTask(task);
                    activeTasks.RemoveAt(i);
                }
            }

            if (timeCounter >= duration && activeTasks.Count == 0)
                Destroy(gameObject);
        }

        private void CreatePrefab(SpawnTask task)
        {
            task.currentPrefab = Instantiate(sourcePrefab);

            if (task.currentPrefab == null)
            {
                task.finished = true;
                return;
            }

            task.currentProjector = task.currentPrefab.GetComponent<DecalProjector>();

            if (task.currentProjector == null)
            {
                Destroy(task.currentPrefab);
                task.finished = true;
                return;
            }

            Material sourceMat = task.currentProjector.material;
            if (sourceMat != null)
                task.currentProjector.material = Instantiate(sourceMat);

            task.currentProjector.renderingLayerMask = (uint)renderingLayerMask;

            task.currentSpriteController = task.currentPrefab.GetComponent<ProjectorSpriteController_URP>();
            task.currentPrioritySetter = task.currentPrefab.GetComponent<ProjectorPrioritySetter_URP>();
            task.currentBloodModifier = task.currentPrefab.GetComponent<BloodModifier_URP>();

            if (projectorSpawnerGrp != null)
                task.currentPrefab.transform.SetParent(projectorSpawnerGrp.transform);
            else
                task.currentPrefab.transform.SetParent(transform);

            task.currentPrefab.transform.position = transform.position;
            task.currentPrefab.transform.localPosition += startPosOffset;

            task.currentPrefab.transform.localScale = Vector3.one;
            task.currentPrefab.transform.localEulerAngles = new Vector3(90f, 0f, 0.0f);
            task.currentPrefab.transform.RotateAround(transform.position, Vector3.up, task.startRotation);

            if (task.currentPrioritySetter != null)
                task.currentPrioritySetter.SetPriority();

            ApplyAppliedBloodSettingsToTask(task);

            task.prefabSpawned = true;
        }

        private void UpdateSingleTask(SpawnTask task)
        {
            if (task.currentPrefab == null || task.currentProjector == null)
            {
                task.finished = true;
                return;
            }

            task.lifetimeCounter += Time.deltaTime;

            float samplePos = task.lifetime > 0.0f
                ? Mathf.Clamp01(task.lifetimeCounter / task.lifetime)
                : 1.0f;

            float scaleValue = scaleCurve != null ? scaleCurve.Evaluate(samplePos) : 1.0f;
            int frameValue = frameCurve != null ? Mathf.FloorToInt(frameCurve.Evaluate(samplePos)) : 0;
            float opacityValue = opacityCurve != null ? opacityCurve.Evaluate(samplePos) : 1.0f;

            if (task.currentSpriteController != null)
                task.currentSpriteController.SetFrameIndex(frameValue);

            task.currentProjector.fadeFactor = opacityValue;
            task.currentProjector.size = new Vector3(
                scaleValue * task.startProjectorSize,
                scaleValue * task.startProjectorSize,
                10.0f);
            task.currentProjector.pivot = new Vector3(0f, 0f, 5f);

            if (task.lifetimeCounter > task.lifetime)
                task.finished = true;
        }

        private void ApplyAppliedBloodSettingsToTask(SpawnTask task)
        {
            if (task == null || task.currentPrefab == null)
                return;

            if (task.currentBloodModifier != null)
            {
                task.currentBloodModifier.OverrideSettings(appliedBloodSettings);

                if (Application.isPlaying)
                    task.currentBloodModifier.ApplyToMaterialInstancesInRuntime();
#if UNITY_EDITOR
                else
                    task.currentBloodModifier.ApplyToSharedMaterialsInEditor();
#endif
            }
            else
            {
                if (Application.isPlaying)
                    BloodModifierUtility_URP.ApplySettingsToGameObjectRuntime(task.currentPrefab, appliedBloodSettings);
#if UNITY_EDITOR
                else
                    BloodModifierUtility_URP.ApplySettingsToGameObjectShared(task.currentPrefab, appliedBloodSettings);
#endif
            }
        }

        private void CleanupTask(SpawnTask task)
        {
            if (task == null)
                return;

            if (task.currentPrefab != null)
                Destroy(task.currentPrefab);
        }

        public void ResetAndInitialize(int value)
        {
            renderingLayerMask = value;

            timeCounter = 0.0f;
            distanceTraveled = 0.0f;
            lastPosition = transform.position;

            for (int i = activeTasks.Count - 1; i >= 0; i--)
                CleanupTask(activeTasks[i]);

            activeTasks.Clear();
            enabled = true;
        }

        public void LoadPresetValuesToSpawnerSettings(BloodPreset preset)
        {
            if (preset == null)
                return;

            bloodSettings.CopyFromPreset(preset, bloodSettings.effectType);
        }

        public void ApplyPresetByIndexAndApplyToRuntime(int presetIndex)
        {
            if (LoadPresetValuesByIndexForCurrentEffectType(presetIndex))
                ApplyCurrentBloodSettingsToAllActiveTasks();
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

        public void ApplyCurrentBloodSettingsToAllActiveTasks()
        {
            CommitBloodSettingsForApply();

            for (int i = 0; i < activeTasks.Count; i++)
            {
                SpawnTask task = activeTasks[i];
                if (task == null || task.currentPrefab == null)
                    continue;

                ApplyAppliedBloodSettingsToTask(task);
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
            for (int i = activeTasks.Count - 1; i >= 0; i--)
                CleanupTask(activeTasks[i]);

            activeTasks.Clear();
        }
    }
}