using System;
using UnityEditor;
using UnityEngine;

namespace BloodEffectsPack
{
    public static class BloodModifierEditorDrawer
    {
        public static void DrawSettings(SerializedProperty settingsProp)
        {
            if (settingsProp == null)
            {
                EditorGUILayout.HelpBox("Settings property is null.", MessageType.Error);
                return;
            }

            SerializedProperty effectTypeProp = settingsProp.FindPropertyRelative("effectType");
            SerializedProperty colorProp = settingsProp.FindPropertyRelative("color");
            SerializedProperty colorIntensityProp = settingsProp.FindPropertyRelative("colorIntensity");
            SerializedProperty albedoPowerProp = settingsProp.FindPropertyRelative("albedoPower");
            SerializedProperty ambientColorIntensityProp = settingsProp.FindPropertyRelative("ambientColorIntensity");
            SerializedProperty hueShiftProp = settingsProp.FindPropertyRelative("hueShift");
            SerializedProperty smoothnessProp = settingsProp.FindPropertyRelative("smoothness");
            SerializedProperty useSpecularityProp = settingsProp.FindPropertyRelative("useSpecularity");
            SerializedProperty gravityScaleProp = settingsProp.FindPropertyRelative("gravityScale");

            if (effectTypeProp != null) EditorGUILayout.PropertyField(effectTypeProp);
            if (colorProp != null) EditorGUILayout.PropertyField(colorProp);
            if (colorIntensityProp != null) EditorGUILayout.PropertyField(colorIntensityProp);
            if (albedoPowerProp != null) EditorGUILayout.PropertyField(albedoPowerProp);
            if (ambientColorIntensityProp != null) EditorGUILayout.PropertyField(ambientColorIntensityProp);
            if (hueShiftProp != null) EditorGUILayout.PropertyField(hueShiftProp);
            if (smoothnessProp != null) EditorGUILayout.PropertyField(smoothnessProp);
            if (useSpecularityProp != null) EditorGUILayout.PropertyField(useSpecularityProp);

            if (effectTypeProp != null &&
                effectTypeProp.enumValueIndex == (int)BloodModifier.EffectType.Splash &&
                gravityScaleProp != null)
            {
                EditorGUILayout.PropertyField(gravityScaleProp);
            }
        }

        public static void DrawPresetButtons(
            BloodModifierSettings settings,
            BloodPreset[] decalPresets,
            BloodPreset[] splashPresets,
            Action<BloodPreset> onPresetClicked)
        {
            if (settings == null)
                return;

            BloodPreset[] targetPresets =
                settings.effectType == BloodModifier.EffectType.Decal
                ? decalPresets
                : splashPresets;

            if (targetPresets == null || targetPresets.Length == 0)
            {
                EditorGUILayout.HelpBox("No presets available for current effect type.", MessageType.None);
                return;
            }

            EditorGUILayout.LabelField("Quick Preset Load", EditorStyles.boldLabel);

            const int columnCount = 3;
            int validCount = 0;

            for (int i = 0; i < targetPresets.Length; i++)
            {
                if (targetPresets[i] != null)
                    validCount++;
            }

            if (validCount == 0)
            {
                EditorGUILayout.HelpBox("No valid presets available.", MessageType.None);
                return;
            }

            int drawn = 0;

            while (drawn < validCount)
            {
                EditorGUILayout.BeginHorizontal();

                int rowDrawn = 0;
                while (drawn < validCount && rowDrawn < columnCount)
                {
                    BloodPreset preset = GetValidPresetByFilteredIndex(targetPresets, drawn);
                    drawn++;
                    rowDrawn++;

                    if (preset == null)
                        continue;

                    if (GUILayout.Button(preset.presetName, GUILayout.Height(24)))
                    {
                        onPresetClicked?.Invoke(preset);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private static BloodPreset GetValidPresetByFilteredIndex(BloodPreset[] presets, int filteredIndex)
        {
            int current = 0;

            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i] == null)
                    continue;

                if (current == filteredIndex)
                    return presets[i];

                current++;
            }

            return null;
        }
    }
}