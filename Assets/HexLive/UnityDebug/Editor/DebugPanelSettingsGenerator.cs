using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityDebug.Editor
{
    public static class DebugPanelSettingsGenerator
    {
        private const string AssetPath = "Assets/Resources/HexLive/DebugPanelSettings.asset";
        private const string ThemeGuid = "7db5674d95ca44cc596bc9431f9ae4df"; // UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss

        [InitializeOnLoadMethod]
        private static void EnsureAssetExists()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetPath);
            if (existing != null)
            {
                // Self-heal: a wiped themeStyleSheet reference kills EVERY
                // runtime panel ("No Theme Style Sheet set ... UI will not
                // render properly") including the loading screen — the game
                // silently never shows its menu. Re-link instead of shipping
                // a broken asset.
                if (existing.themeStyleSheet == null)
                {
                    var theme = FindTheme();
                    if (theme != null)
                    {
                        existing.themeStyleSheet = theme;
                        EditorUtility.SetDirty(existing);
                        AssetDatabase.SaveAssets();
                        Debug.LogWarning(
                            $"[HexLive] DebugPanelSettings had NO theme (UI would not render) — re-linked '{theme.name}'.");
                    }
                    else
                    {
                        Debug.LogError(
                            "[HexLive] DebugPanelSettings has NO theme and no ThemeStyleSheet was found in the project — runtime UI will not render.");
                    }
                }

                return;
            }

            CreateAsset();
        }

        private static ThemeStyleSheet FindTheme()
        {
            var themePath = AssetDatabase.GUIDToAssetPath(ThemeGuid);
            if (!string.IsNullOrEmpty(themePath))
            {
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
                if (theme != null)
                {
                    return theme;
                }
            }

            foreach (var guid in AssetDatabase.FindAssets("t:ThemeStyleSheet"))
            {
                var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (theme != null)
                {
                    return theme;
                }
            }

            return null;
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

            panelSettings.themeStyleSheet = FindTheme();

            AssetDatabase.CreateAsset(panelSettings, AssetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[HexLive] Debug PanelSettings created at {AssetPath} (theme: {(panelSettings.themeStyleSheet != null ? panelSettings.themeStyleSheet.name : "NONE")})");
        }
    }
}
