using HexLive.UnityPresentation.HexStepJumpTest;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    [InitializeOnLoad]
    internal static class HexStepJumpTestEditorBridge
    {
        static HexStepJumpTestEditorBridge()
        {
            HexStepJumpTestBootstrap.SaveConfigAssetInEditor = Save;
        }

        private static void Save(HexLive.UnityPresentation.Config.HexTuningConfig config)
        {
            Undo.RecordObject(config, "Save jump tuning from play mode");
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log($"[HexStepJumpTest] Настройки прыжка сохранены в «{config.name}».");
        }
    }
}
