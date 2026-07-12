using UnityEditor;
using UnityEngine;

namespace BloodEffectsPack
{
    [CustomEditor(typeof(BloodModifier))]
    public class BloodModifierEditor : Editor
    {
        private SerializedProperty settingsProp;
        private SerializedProperty decalPresetsProp;
        private SerializedProperty splashPresetsProp;

        private void OnEnable()
        {
            settingsProp = serializedObject.FindProperty("settings");
            decalPresetsProp = serializedObject.FindProperty("decalPresets");
            splashPresetsProp = serializedObject.FindProperty("splashPresets");
        }

        public override void OnInspectorGUI()
        {
            if (serializedObject == null || target == null)
                return;

            serializedObject.UpdateIfRequiredOrScript();

            BloodModifier bloodModifier = (BloodModifier)target;

            DrawPresetSection(bloodModifier);

            EditorGUILayout.Space(10);

            DrawSettingsSection();

            EditorGUILayout.Space(12);

            DrawEditorApplySection(bloodModifier);

            EditorGUILayout.Space(10);

            DrawRuntimeApplySection(bloodModifier);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawPresetSection(BloodModifier bloodModifier)
        {
            EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);

            DrawPresetArray(decalPresetsProp, "Decal Presets");
            DrawPresetArray(splashPresetsProp, "Splash Presets");

            EditorGUILayout.Space(8);

            if (settingsProp == null)
                return;

            BloodModifier.EffectType currentEffectType = GetCurrentEffectTypeFromProperty();
            BloodPreset[] currentPresets = currentEffectType == BloodModifier.EffectType.Decal
                ? bloodModifier.decalPresets
                : bloodModifier.splashPresets;

            BloodModifierEditorDrawer.DrawPresetButtons(
                new BloodModifierSettings { effectType = currentEffectType },
                currentEffectType == BloodModifier.EffectType.Decal ? currentPresets : bloodModifier.decalPresets,
                currentEffectType == BloodModifier.EffectType.Splash ? currentPresets : bloodModifier.splashPresets,
                preset =>
                {
                    if (preset == null)
                        return;

                    Undo.RecordObject(target, "Load Blood Preset Values");
                    LoadPresetValuesToSerializedSettings(preset, settingsProp, currentEffectType);
                    serializedObject.ApplyModifiedProperties();

                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                    Repaint();
                });
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.LabelField("Modifier Settings", EditorStyles.boldLabel);

            if (settingsProp == null)
            {
                EditorGUILayout.HelpBox("Settings property not found.", MessageType.Error);
                return;
            }

            BloodModifierEditorDrawer.DrawSettings(settingsProp);
        }

        private void DrawEditorApplySection(BloodModifier bloodModifier)
        {
            EditorGUILayout.LabelField("Editor Asset Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Preset buttons only load values into Settings. Shared materials are modified only when this Apply button is pressed.",
                MessageType.Warning);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Apply To Shared Materials (Editor Only)"))
                {
                    serializedObject.ApplyModifiedProperties();

                    Undo.RecordObject(bloodModifier, "Apply Blood Modifier To Shared Materials");
                    bloodModifier.ApplyToSharedMaterialsInEditor();
                    EditorUtility.SetDirty(bloodModifier);

                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                }
            }
        }

        private void DrawRuntimeApplySection(BloodModifier bloodModifier)
        {
            EditorGUILayout.LabelField("Runtime Instance Apply", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Preset buttons only load values into Settings. Runtime material instances are modified only when this Apply button is pressed.\n\n" +
                "For runtime preset updates by code, use ApplyPresetByIndexAndApplyToRuntime(int presetIndex).",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Apply To Material Instances (Play Mode Only)"))
                {
                    serializedObject.ApplyModifiedProperties();
                    bloodModifier.ApplyToMaterialInstancesInRuntime();

                    GUIUtility.keyboardControl = 0;
                    GUIUtility.hotControl = 0;
                }
            }
        }

        private BloodModifier.EffectType GetCurrentEffectTypeFromProperty()
        {
            if (settingsProp == null)
                return BloodModifier.EffectType.Splash;

            SerializedProperty effectTypeProp = settingsProp.FindPropertyRelative("effectType");
            if (effectTypeProp == null)
                return BloodModifier.EffectType.Splash;

            return (BloodModifier.EffectType)effectTypeProp.enumValueIndex;
        }

        private void LoadPresetValuesToSerializedSettings(
            BloodPreset preset,
            SerializedProperty targetSettingsProp,
            BloodModifier.EffectType effectType)
        {
            if (preset == null || targetSettingsProp == null)
                return;

            SerializedProperty effectTypeProp = targetSettingsProp.FindPropertyRelative("effectType");
            SerializedProperty colorProp = targetSettingsProp.FindPropertyRelative("color");
            SerializedProperty colorIntensityProp = targetSettingsProp.FindPropertyRelative("colorIntensity");
            SerializedProperty albedoPowerProp = targetSettingsProp.FindPropertyRelative("albedoPower");
            SerializedProperty ambientColorIntensityProp = targetSettingsProp.FindPropertyRelative("ambientColorIntensity");
            SerializedProperty hueShiftProp = targetSettingsProp.FindPropertyRelative("hueShift");
            SerializedProperty smoothnessProp = targetSettingsProp.FindPropertyRelative("smoothness");
            SerializedProperty useSpecularityProp = targetSettingsProp.FindPropertyRelative("useSpecularity");
            SerializedProperty gravityScaleProp = targetSettingsProp.FindPropertyRelative("gravityScale");

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
    }
}