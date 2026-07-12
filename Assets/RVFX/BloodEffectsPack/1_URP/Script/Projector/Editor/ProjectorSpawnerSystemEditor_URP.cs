using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BloodEffectsPack
{
    [CustomEditor(typeof(ProjectorSpawnerSystem_URP))]
    public class ProjectorSpawnerSystemEditor_URP : Editor
    {
        private int selectedTab = 0;

        private static readonly string[] TabNames =
        {
            "Projector",
            "Color"
        };

        private SerializedProperty renderingLayerMaskProp;
        private SerializedProperty isLoopProp;
        private SerializedProperty spawnerLifetimeProp;
        private SerializedProperty sourcePrefabProp;

        private SerializedProperty lifetimeMinProp;
        private SerializedProperty lifetimeMaxProp;
        private SerializedProperty startPosOffsetProp;
        private SerializedProperty startSizeMinProp;
        private SerializedProperty startSizeMaxProp;
        private SerializedProperty startRotationMinProp;
        private SerializedProperty startRotationMaxProp;
        private SerializedProperty frameCurveProp;
        private SerializedProperty scaleCurveProp;
        private SerializedProperty opacityCurveProp;
        private SerializedProperty spawnOptionsProp;

        private SerializedProperty bloodSettingsProp;
        private SerializedProperty appliedBloodSettingsProp;
        private SerializedProperty decalPresetsProp;
        private SerializedProperty splashPresetsProp;

        private void OnEnable()
        {
            CacheProperties();
        }

        private void OnDisable()
        {
            ClearCachedProperties();
        }

        private void CacheProperties()
        {
            if (serializedObject == null || serializedObject.targetObject == null)
                return;

            renderingLayerMaskProp = serializedObject.FindProperty("renderingLayerMask");
            isLoopProp = serializedObject.FindProperty("isLoop");
            spawnerLifetimeProp = serializedObject.FindProperty("spawnerLifetime");
            sourcePrefabProp = serializedObject.FindProperty("sourcePrefab");

            lifetimeMinProp = serializedObject.FindProperty("lifetime_min");
            lifetimeMaxProp = serializedObject.FindProperty("lifetime_max");
            startPosOffsetProp = serializedObject.FindProperty("startPosOffset");
            startSizeMinProp = serializedObject.FindProperty("startSize_min");
            startSizeMaxProp = serializedObject.FindProperty("startSize_max");
            startRotationMinProp = serializedObject.FindProperty("startRotation_min");
            startRotationMaxProp = serializedObject.FindProperty("startRotation_max");
            frameCurveProp = serializedObject.FindProperty("frameCurve");
            scaleCurveProp = serializedObject.FindProperty("scaleCurve");
            opacityCurveProp = serializedObject.FindProperty("opacityCurve");
            spawnOptionsProp = serializedObject.FindProperty("spawnOptions");

            bloodSettingsProp = serializedObject.FindProperty("bloodSettings");
            appliedBloodSettingsProp = serializedObject.FindProperty("appliedBloodSettings");
            decalPresetsProp = serializedObject.FindProperty("decalPresets");
            splashPresetsProp = serializedObject.FindProperty("splashPresets");
        }

        private void ClearCachedProperties()
        {
            renderingLayerMaskProp = null;
            isLoopProp = null;
            spawnerLifetimeProp = null;
            sourcePrefabProp = null;

            lifetimeMinProp = null;
            lifetimeMaxProp = null;
            startPosOffsetProp = null;
            startSizeMinProp = null;
            startSizeMaxProp = null;
            startRotationMinProp = null;
            startRotationMaxProp = null;
            frameCurveProp = null;
            scaleCurveProp = null;
            opacityCurveProp = null;
            spawnOptionsProp = null;

            bloodSettingsProp = null;
            appliedBloodSettingsProp = null;
            decalPresetsProp = null;
            splashPresetsProp = null;
        }

        public override void OnInspectorGUI()
        {
            if (target == null || serializedObject == null || serializedObject.targetObject == null)
                return;

            CacheProperties();

            try
            {
                serializedObject.UpdateIfRequiredOrScript();
            }
            catch
            {
                return;
            }

            ProjectorSpawnerSystem_URP spawner = target as ProjectorSpawnerSystem_URP;
            if (spawner == null)
                return;

            EditorGUILayout.Space();
            selectedTab = GUILayout.Toolbar(selectedTab, TabNames);
            EditorGUILayout.Space();

            switch (selectedTab)
            {
                case 0:
                    DrawProjectorTab();
                    break;

                case 1:
                    DrawColorTab(spawner);
                    break;
            }

            try
            {
                serializedObject.ApplyModifiedProperties();
            }
            catch
            {
                // Ignore disposed serialized object errors during inspector teardown / playmode switch.
            }
        }

        private void DrawProjectorTab()
        {
            DrawRenderingLayerMaskField(renderingLayerMaskProp, "Rendering Layer Mask");

            EditorGUILayout.Space();

            DrawPropertySafe(isLoopProp);
            DrawPropertySafe(spawnerLifetimeProp);
            DrawPropertySafe(sourcePrefabProp);
            DrawPropertySafe(lifetimeMinProp);
            DrawPropertySafe(lifetimeMaxProp);
            DrawPropertySafe(startPosOffsetProp);
            DrawPropertySafe(startSizeMinProp);
            DrawPropertySafe(startSizeMaxProp);
            DrawPropertySafe(startRotationMinProp);
            DrawPropertySafe(startRotationMaxProp);
            DrawPropertySafe(frameCurveProp);
            DrawPropertySafe(scaleCurveProp);
            DrawPropertySafe(opacityCurveProp);

            EditorGUILayout.Space();

            if (spawnOptionsProp != null)
                EditorGUILayout.PropertyField(spawnOptionsProp, true);
        }

        private void DrawColorTab(ProjectorSpawnerSystem_URP spawner)
        {
            if (spawner == null)
                return;

            EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);

            DrawPresetArray(decalPresetsProp, "Decal Presets");
            DrawPresetArray(splashPresetsProp, "Splash Presets");

            EditorGUILayout.Space(8);

            if (bloodSettingsProp == null)
            {
                EditorGUILayout.HelpBox("bloodSettings property not found.", MessageType.Error);
                return;
            }

            BloodModifier_URP.EffectType currentEffectType = GetCurrentEffectTypeFromProperty();
            BloodPreset[] currentPresets = currentEffectType == BloodModifier_URP.EffectType.Decal
                ? spawner.DecalPresets
                : spawner.SplashPresets;

            BloodModifierEditorDrawer_URP.DrawPresetButtons(
                new BloodModifierSettings_URP { effectType = currentEffectType },
                currentEffectType == BloodModifier_URP.EffectType.Decal ? currentPresets : spawner.DecalPresets,
                currentEffectType == BloodModifier_URP.EffectType.Splash ? currentPresets : spawner.SplashPresets,
                preset =>
                {
                    if (preset == null)
                        return;

                    Undo.RecordObject(target, "Load Projector Spawner Blood Preset Values URP");
                    LoadPresetValuesToSerializedSettings(preset, bloodSettingsProp, currentEffectType);

                    try
                    {
                        serializedObject.ApplyModifiedProperties();
                    }
                    catch
                    {
                    }

                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                    Repaint();
                });

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Edit Settings (Not Applied Yet)", EditorStyles.boldLabel);
            if (bloodSettingsProp != null)
                BloodModifierEditorDrawer_URP.DrawSettings(bloodSettingsProp);
            else
                EditorGUILayout.HelpBox("bloodSettings property not found.", MessageType.Error);

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Currently Applied Settings", EditorStyles.boldLabel);

                if (appliedBloodSettingsProp != null)
                    BloodModifierEditorDrawer_URP.DrawSettings(appliedBloodSettingsProp);
                else
                    EditorGUILayout.HelpBox("appliedBloodSettings property not found.", MessageType.Warning);
            }

            EditorGUILayout.Space(12);

            DrawSpawnerApplySection(spawner);
        }

        private void DrawSpawnerApplySection(ProjectorSpawnerSystem_URP spawner)
        {
            EditorGUILayout.LabelField("Spawner Apply", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Preset buttons only change Edit Settings. Actual spawned decal or prefab material changes happen only when an Apply button is pressed.\n\n" +
                "For runtime color changes after spawn, use LoadPresetValuesByIndexForCurrentEffectType(int presetIndex) and then ApplyCurrentBloodSettingsToAllActiveOptions() to update materials.\n\n" +
                "For runtime preset updates by code, you can also use ApplyPresetByIndexAndApplyToRuntime(int presetIndex).",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(
                Application.isPlaying ||
                sourcePrefabProp == null ||
                sourcePrefabProp.objectReferenceValue == null))
            {
                if (GUILayout.Button("Apply To Source Prefab (Edit Mode Only)"))
                {
                    try
                    {
                        serializedObject.ApplyModifiedProperties();
                    }
                    catch
                    {
                    }

                    spawner.ApplyCurrentBloodSettingsToSourcePrefabEditorOnly();
                    EditorUtility.SetDirty(spawner);

                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                }
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Apply To Active Spawned Decals (Play Mode Only)"))
                {
                    try
                    {
                        serializedObject.ApplyModifiedProperties();
                    }
                    catch
                    {
                    }

                    spawner.ApplyCurrentBloodSettingsToAllActiveOptions();
                    EditorUtility.SetDirty(spawner);

                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                }
            }
        }

        private BloodModifier_URP.EffectType GetCurrentEffectTypeFromProperty()
        {
            if (bloodSettingsProp == null)
                return BloodModifier_URP.EffectType.Splash;

            SerializedProperty effectTypeProp = bloodSettingsProp.FindPropertyRelative("effectType");
            if (effectTypeProp == null)
                return BloodModifier_URP.EffectType.Splash;

            return (BloodModifier_URP.EffectType)effectTypeProp.enumValueIndex;
        }

        private void LoadPresetValuesToSerializedSettings(
            BloodPreset preset,
            SerializedProperty settingsProp,
            BloodModifier_URP.EffectType effectType)
        {
            if (preset == null || settingsProp == null)
                return;

            SerializedProperty effectTypeProp = settingsProp.FindPropertyRelative("effectType");
            SerializedProperty colorProp = settingsProp.FindPropertyRelative("color");
            SerializedProperty colorIntensityProp = settingsProp.FindPropertyRelative("colorIntensity");
            SerializedProperty albedoPowerProp = settingsProp.FindPropertyRelative("albedoPower");
            SerializedProperty ambientColorIntensityProp = settingsProp.FindPropertyRelative("ambientColorIntensity");
            SerializedProperty hueShiftProp = settingsProp.FindPropertyRelative("hueShift");
            SerializedProperty smoothnessProp = settingsProp.FindPropertyRelative("smoothness");
            SerializedProperty useSpecularityProp = settingsProp.FindPropertyRelative("useSpecularity");
            SerializedProperty gravityScaleProp = settingsProp.FindPropertyRelative("gravityScale");

            if (effectTypeProp != null) effectTypeProp.enumValueIndex = (int)effectType;
            if (colorProp != null) colorProp.colorValue = preset.color;
            if (colorIntensityProp != null) colorIntensityProp.floatValue = preset.colorIntensity;
            if (albedoPowerProp != null) albedoPowerProp.floatValue = preset.albedoPower;
            if (ambientColorIntensityProp != null) ambientColorIntensityProp.floatValue = preset.ambientColorIntensity;
            if (hueShiftProp != null) hueShiftProp.floatValue = preset.hueshift;
            if (smoothnessProp != null) smoothnessProp.floatValue = preset.smoothness;
            if (useSpecularityProp != null) useSpecularityProp.boolValue = preset.useSpecularity;
            if (gravityScaleProp != null) gravityScaleProp.floatValue = preset.gravityScale;
        }

        private void DrawPresetArray(SerializedProperty arrayProp, string label)
        {
            if (arrayProp == null)
                return;

            EditorGUILayout.BeginVertical("box");

            arrayProp.isExpanded = EditorGUILayout.Foldout(arrayProp.isExpanded, label, true);

            if (arrayProp.isExpanded)
            {
                EditorGUI.indentLevel++;

                int newSize = EditorGUILayout.IntField("Size", arrayProp.arraySize);
                if (newSize != arrayProp.arraySize)
                    arrayProp.arraySize = Mathf.Max(0, newSize);

                for (int i = 0; i < arrayProp.arraySize; i++)
                {
                    SerializedProperty element = arrayProp.GetArrayElementAtIndex(i);
                    EditorGUILayout.PropertyField(element, new GUIContent($"Element {i}"));
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRenderingLayerMaskField(SerializedProperty prop, string label)
        {
            if (prop == null)
                return;

            string[] renderingLayerNames = GetRenderingLayerNames();
            if (renderingLayerNames == null || renderingLayerNames.Length == 0)
            {
                EditorGUILayout.PropertyField(prop, new GUIContent(label));
                return;
            }

            int currentMask = prop.intValue;
            int displayedMask = 0;

            for (int i = 0; i < renderingLayerNames.Length; i++)
            {
                int bit = 1 << i;
                if ((currentMask & bit) != 0)
                    displayedMask |= bit;
            }

            EditorGUI.BeginChangeCheck();
            displayedMask = EditorGUILayout.MaskField(label, displayedMask, renderingLayerNames);
            if (EditorGUI.EndChangeCheck())
            {
                int newMask = 0;

                for (int i = 0; i < renderingLayerNames.Length; i++)
                {
                    int bit = 1 << i;
                    if ((displayedMask & bit) != 0)
                        newMask |= bit;
                }

                prop.intValue = newMask;
            }
        }

        private string[] GetRenderingLayerNames()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null)
            {
#if UNITY_2022_1_OR_NEWER
                string[] names = pipeline.renderingLayerMaskNames;
                if (names != null && names.Length > 0)
                    return names;
#endif
            }

            List<string> fallback = new List<string>();
            for (int i = 0; i < 8; i++)
                fallback.Add("Rendering Layer " + i);

            return fallback.ToArray();
        }

        private void DrawPropertySafe(SerializedProperty prop)
        {
            if (prop != null)
                EditorGUILayout.PropertyField(prop);
        }
    }
}