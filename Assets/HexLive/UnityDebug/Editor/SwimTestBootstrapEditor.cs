using HexLive.UnityPresentation.SwimTest;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Adds "Сохранить настройки" / "Загрузить из конфига" buttons under the
    /// SwimTest component so tuning done with the sliders can be committed to
    /// the shared HexTuningConfig asset (which the game reads at startup) —
    /// no more editing code constants by hand.
    /// </summary>
    [CustomEditor(typeof(SwimTestBootstrap))]
    public sealed class SwimTestBootstrapEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var boot = (SwimTestBootstrap)target;
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(boot.TuningConfig == null))
            {
                if (GUILayout.Button("💾  Сохранить настройки в конфиг", GUILayout.Height(28f)))
                {
                    Undo.RecordObject(boot.TuningConfig, "Save Hex Tuning");
                    boot.WriteSlidersToConfig();
                    EditorUtility.SetDirty(boot.TuningConfig);
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[HexTuning] Настройки сохранены в «{boot.TuningConfig.name}».");
                }

                if (GUILayout.Button("↩  Загрузить из конфига в слайдеры"))
                {
                    Undo.RecordObject(boot, "Load Hex Tuning");
                    boot.ReadSlidersFromConfig();
                    EditorUtility.SetDirty(boot);
                }
            }

            if (boot.TuningConfig == null)
            {
                EditorGUILayout.HelpBox(
                    "Назначьте ассет HexTuningConfig в поле «Конфиг», чтобы включить кнопки. " +
                    "Создать: правый клик в Project → Create → HexLive → Tuning Config.",
                    MessageType.Info);
            }
        }
    }
}
