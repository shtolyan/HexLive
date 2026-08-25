using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Boot-путь баланс-конфигов (§59): загружает все ассеты из
    /// Resources/HexLive/Balance и зеркалит их в статики через
    /// <see cref="SimConfigMirror"/>. Один ассет = один тематический блок
    /// (Character / ResourceLoop / Social / Threat); добавить ручку = поле в
    /// конфиге + одноимённый статик, сюда лезть не нужно.
    /// </summary>
    public static class BalanceTuning
    {
        public const string ResourceFolder = "HexLive/Balance";

        public static void LoadAndApply()
        {
            foreach (var config in LoadAll())
            {
                SimConfigMirror.Apply(config);
            }
        }

        /// <summary>Все зеркалируемые конфиги из папки (для Apply, Capture-меню
        /// и coverage-гейта).</summary>
        public static List<ScriptableObject> LoadAll()
        {
            var result = new List<ScriptableObject>();
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                foreach (var guid in UnityEditor.AssetDatabase.FindAssets(
                             "t:ScriptableObject",
                             new[] { "Assets/HexLiveContent/RuntimeSource/Balance" }))
                {
                    var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                        UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                    if (asset != null && SimConfigMirror.IsMirrorConfig(asset.GetType()))
                    {
                        result.Add(asset);
                    }
                }
                return result;
            }
#endif
            foreach (var asset in HexLive.UnityPresentation.Content.AtomicResources.LoadAll<ScriptableObject>(ResourceFolder))
            {
                if (SimConfigMirror.IsMirrorConfig(asset.GetType()))
                {
                    result.Add(asset);
                }
            }

            return result;
        }
    }
}
