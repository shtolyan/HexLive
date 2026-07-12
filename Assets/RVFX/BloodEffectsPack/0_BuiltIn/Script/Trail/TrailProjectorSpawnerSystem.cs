using System.Collections.Generic;
using UnityEngine;

namespace BloodEffectsPack
{
    public class TrailProjectorSpawnerSystem : MonoBehaviour
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

        [HideInInspector] public int ignoreLayerMask;
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
        public BloodModifierSettings bloodSettings = new BloodModifierSettings();

        [SerializeField]
        private BloodModifierSettings appliedBloodSettings = new BloodModifierSettings();

        public BloodPreset[] decalPresets;
        public BloodPreset[] splashPresets;

        private class SpawnTask
        {
            public float lifetime;
            public float startProjectorSize;
            public float startRotation;

            public float lifetimeCounter = 0.0f;

            public GameObject currentPrefab;
            public Projector currentProjector;
            public BloodModifier currentBloodModifier;

            public bool prefabSpawned = false;
            public bool finished = false;
        }

        private readonly List<SpawnTask> activeTasks = new List<SpawnTask>();

        public BloodModifierSettings BloodSettings => bloodSettings;
        public BloodModifierSettings AppliedBloodSettings => appliedBloodSettings;
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

            task.currentProjector = task.currentPrefab.GetComponent<Projector>();

            if (task.currentProjector == null)
            {
                Destroy(task.currentPrefab);
                task.finished = true;
                return;
            }

            task.currentProjector.enabled = false;
            task.currentProjector.enabled = true;

            if (task.currentProjector.material != null)
                task.currentProjector.material = Instantiate(task.currentProjector.material);

            task.currentProjector.ignoreLayers = ignoreLayerMask;
            task.currentBloodModifier = task.currentPrefab.GetComponent<BloodModifier>();

            if (projectorSpawnerGrp != null)
                task.currentPrefab.transform.SetParent(projectorSpawnerGrp.transform);
            else
                task.currentPrefab.transform.SetParent(transform);

            task.currentPrefab.transform.position = transform.position;
            task.currentPrefab.transform.localPosition += startPosOffset;

            task.currentPrefab.transform.localScale = Vector3.one;
            task.currentPrefab.transform.localEulerAngles = new Vector3(90f, 0f, 0.0f);
            task.currentPrefab.transform.RotateAround(transform.position, Vector3.up, task.startRotation);

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

            if (task.currentProjector.material != null)
            {
                task.currentProjector.material.SetFloat("_Frame", frameValue);
                task.currentProjector.material.SetFloat("_Opacity", opacityValue);
            }

            task.currentProjector.orthographicSize = scaleValue * task.startProjectorSize;

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
                    BloodModifierUtility.ApplySettingsToGameObjectRuntime(task.currentPrefab, appliedBloodSettings);
#if UNITY_EDITOR
                else
                    BloodModifierUtility.ApplySettingsToGameObjectShared(task.currentPrefab, appliedBloodSettings);
#endif
            }
        }

        private void CleanupTask(SpawnTask task)
        {
            if (task.currentPrefab != null)
                Destroy(task.currentPrefab);
        }

        public void ResetAndInitialize(int value)
        {
            ignoreLayerMask = value;

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
            BloodPreset[] presets = bloodSettings.effectType == BloodModifier.EffectType.Decal
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

        public void OverrideBloodSettings(BloodModifierSettings overrideSettings)
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

            BloodModifier modifier = sourcePrefab.GetComponent<BloodModifier>();
            if (modifier != null)
            {
                modifier.OverrideSettings(appliedBloodSettings);
                modifier.ApplyToSharedMaterialsInEditor();
                return;
            }

            BloodModifierUtility.ApplySettingsToGameObjectShared(sourcePrefab, appliedBloodSettings);
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