using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityDebug.Editor
{
    public static class DebugPanelSettingsGenerator
    {
        private const string AssetPath = "Assets/Resources/HexLive/DebugPanelSettings.asset";
        private const string ThemeGuid = "2a22d1b6d73ef4d40a824e87e5e4e717";

        [InitializeOnLoadMethod]
        private static void EnsureAssetExists()
        {
            if (AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetPath) != null)
            {
                return;
            }

            CreateAsset();
        }

        [MenuItem("HexLive/Regenerate Debug Panel Settings")]
        private static void CreateAsset()
        {
            var folder = System.IO.Path.GetDirectoryName(AssetPath);
            if (!AssetDatabase.IsValidFolder(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }

            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            panelSettings.sortingOrder = 200;
            panelSettings.targetDisplay = 0;

            var themePath = AssetDatabase.GUIDToAssetPath(ThemeGuid);
            if (!string.IsNullOrEmpty(themePath))
            {
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                if (theme != null)
                {
                    panelSettings.themeStyleSheet = theme;
                }
            }

            if (panelSettings.themeStyleSheet == null)
            {
                var guids = AssetDatabase.FindAssets("UnityDefaultRuntimeTheme t:ThemeStyleSheet");
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(path);
                    if (theme != null)
                    {
                        panelSettings.themeStyleSheet = theme;
                        break;
                    }
                }
            }

            AssetDatabase.CreateAsset(panelSettings, AssetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[HexLive] Debug PanelSettings created at {AssetPath} (theme: {(panelSettings.themeStyleSheet != null ? panelSettings.themeStyleSheet.name : "NONE")})");
        }
    }
}
