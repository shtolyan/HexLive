using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BloodEffectsPack
{
    [CustomEditor(typeof(TrailProjectorSpawnerSystem))]
    public class TrailProjectorSpawnerSystemEditor : Editor
    {
        private int selectedTab = 0;

        private static readonly string[] TabNames =
        {
            "Trail",
            "Projector",
            "Color"
        };

        private SerializedProperty ignoreLayerMaskProp;
        private SerializedProperty sourcePrefabProp;

        private SerializedProperty spawnDistanceMinProp;
        private SerializedProperty spawnDistanceMaxProp;
        private SerializedProperty durationProp;

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

        private SerializedProperty bloodSettingsProp;
        private SerializedProperty appliedBloodSettingsProp;
        private SerializedProperty decalPresetsProp;
        private SerializedProperty splashPresetsProp;

        private void OnEnable()
        {
            ignoreLayerMaskProp = serializedObject.FindProperty("ignoreLayerMask");
            sourcePrefabProp = serializedObject.FindProperty("sourcePrefab");

            spawnDistanceMinProp = serializedObject.FindProperty("spawnDistance_min");
            spawnDistanceMaxProp = serializedObject.FindProperty("spawnDistance_max");
            durationProp = serializedObject.FindProperty("duration");

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

            bloodSettingsProp = serializedObject.FindProperty("bloodSettings");
            appliedBloodSettingsProp = serializedObject.FindProperty("appliedBloodSettings");
            decalPresetsProp = serializedObject.FindProperty("decalPresets");
            splashPresetsProp = serializedObject.FindProperty("splashPresets");
        }

        public override void OnInspectorGUI()
        {
            if (serializedObject == null || target == null)
                return;

            serializedObject.UpdateIfRequiredOrScript();

            TrailProjectorSpawnerSystem spawner = (TrailProjectorSpawnerSystem)target;

            EditorGUILayout.Space();
            selectedTab = GUILayout.Toolbar(selectedTab, TabNames);
            EditorGUILayout.Space();

            switch (selectedTab)
            {
                case 0:
                    DrawTrailTab();
                    break;

                case 1:
                    DrawProjectorTab();
                    break;

                case 2:
                    DrawColorTab(spawner);
                    break;
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawTrailTab()
        {
            EditorGUILayout.PropertyField(spawnDistanceMinProp);
            EditorGUILayout.PropertyField(spawnDistanceMaxProp);
            EditorGUILayout.PropertyField(durationProp);
        }

        private void DrawProjectorTab()
        {
            DrawLayerMaskProperty(ignoreLayerMaskProp, "Ignore Layer Mask");

            EditorGUILayout.Space();

            EditorGUILayout.PropertyField(sourcePrefabProp);
            EditorGUILayout.PropertyField(lifetimeMinProp);
            EditorGUILayout.PropertyField(lifetimeMaxProp);
            EditorGUILayout.PropertyField(startPosOffsetProp);
            EditorGUILayout.PropertyField(startSizeMinProp);
            EditorGUILayout.PropertyField(startSizeMaxProp);
            EditorGUILayout.PropertyField(startRotationMinProp);
            EditorGUILayout.PropertyField(startRotationMaxProp);
            EditorGUILayout.PropertyField(frameCurveProp);
            EditorGUILayout.PropertyField(scaleCurveProp);
            EditorGUILayout.PropertyField(opacityCurveProp);
        }

        private void DrawColorTab(TrailProjectorSpawnerSystem spawner)
        {
            EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);

            DrawPresetArray(decalPresetsProp, "Decal Presets");
            DrawPresetArray(splashPresetsProp, "Splash Presets");

            EditorGUILayout.Space(8);

            BloodModifier.EffectType currentEffectType = GetCurrentEffectTypeFromProperty();
            BloodPreset[] currentPresets = currentEffectType == BloodModifier.EffectType.Decal
                ? spawner.DecalPresets
                : spawner.SplashPresets;

            BloodModifierEditorDrawer.DrawPresetButtons(
                new BloodModifierSettings { effectType = currentEffectType },
                currentEffectType == BloodModifier.EffectType.Decal ? currentPresets : spawner.DecalPresets,
                currentEffectType == BloodModifier.EffectType.Splash ? currentPresets : spawner.SplashPresets,
                preset =>
                {
                    if (preset == null)
                        return;

                    Undo.RecordObject(target, "Load Trail Blood Preset Values");
                    LoadPresetValuesToSerializedSettings(preset, bloodSettingsProp, currentEffectType);
                    serializedObject.ApplyModifiedProperties();
                    Repaint();
                });

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Edit Settings (Not Applied Yet)", EditorStyles.boldLabel);
            BloodModifierEditorDrawer.DrawSettings(bloodSettingsProp);

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Currently Applied Settings", EditorStyles.boldLabel);
                BloodModifierEditorDrawer.DrawSettings(appliedBloodSettingsProp);
            }

            EditorGUILayout.Space(12);

            DrawSpawnerApplySection(spawner);
        }

        private void DrawSpawnerApplySection(TrailProjectorSpawnerSystem spawner)
        {
            EditorGUILayout.LabelField("Spawner Apply", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Preset buttons only change Edit Settings. Actual projector or prefab material changes happen only when an Apply button is pressed.\n\n" +
                "For runtime color changes after spawn, use ApplyPresetByIndexAndApplyToRuntime(int presetIndex) to update materials.",
                MessageType.Info);

#if UNITY_EDITOR
            using (new EditorGUI.DisabledScope(
                Application.isPlaying ||
                sourcePrefabProp == null ||
                sourcePrefabProp.objectReferenceValue == null))
            {
                if (GUILayout.Button("Apply To Source Prefab (Edit Mode Only)"))
                {
                    serializedObject.ApplyModifiedProperties();
                    spawner.ApplyCurrentBloodSettingsToSourcePrefabEditorOnly();
                    EditorUtility.SetDirty(spawner);
                }
            }
#endif

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Apply To Active Spawned Projectors (Play Mode Only)"))
                {
                    serializedObject.ApplyModifiedProperties();
                    spawner.ApplyCurrentBloodSettingsToAllActiveTasks();
                    EditorUtility.SetDirty(spawner);
                }
            }
        }

        private BloodModifier.EffectType GetCurrentEffectTypeFromProperty()
        {
            SerializedProperty effectTypeProp = bloodSettingsProp.FindPropertyRelative("effectType");
            if (effectTypeProp == null)
                return BloodModifier.EffectType.Splash;

            return (BloodModifier.EffectType)effectTypeProp.enumValueIndex;
        }

        private void LoadPresetValuesToSerializedSettings(
            BloodPreset preset,
            SerializedProperty settingsProp,
            BloodModifier.EffectType effectType)
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

        private void DrawLayerMaskProperty(SerializedProperty intMaskProp, string label)
        {
            if (intMaskProp == null)
                return;

            string[] layerNames = GetLayerNames();
            int[] layerNumbers = GetLayerNumbers();

            int currentMask = intMaskProp.intValue;
            int displayedMask = 0;

            for (int i = 0; i < layerNumbers.Length; i++)
            {
                if ((currentMask & (1 << layerNumbers[i])) != 0)
                    displayedMask |= 1 << i;
            }

            EditorGUI.BeginChangeCheck();
            displayedMask = EditorGUILayout.MaskField(label, displayedMask, layerNames);
            if (EditorGUI.EndChangeCheck())
            {
                int newMask = 0;
                for (int i = 0; i < layerNumbers.Length; i++)
                {
                    if ((displayedMask & (1 << i)) != 0)
                        newMask |= 1 << layerNumbers[i];
                }

                intMaskProp.intValue = newMask;
            }
        }

        private string[] GetLayerNames()
        {
            List<string> names = new List<string>();

            for (int i = 0; i < 32; i++)
            {
                string layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                    names.Add(layerName);
            }

            return names.ToArray();
        }

        private int[] GetLayerNumbers()
        {
            List<int> numbers = new List<int>();

            for (int i = 0; i < 32; i++)
            {
                string layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                    numbers.Add(i);
            }

            return numbers.ToArray();
        }
    }
}