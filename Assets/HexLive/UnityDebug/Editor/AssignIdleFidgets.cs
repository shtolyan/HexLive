using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// §81: раскладывает новые клипы Mixamo по слотам NpcAnimSet — сценки
    /// безделья и такты сцены абьюза. Идемпотентно, можно жать сколько угодно.
    /// Меню: <b>HexLive ▸ Actors ▸ Assign Idle Fidgets</b>.
    ///
    /// Отдельный шаг, а не ручное перетаскивание в инспекторе: слотов семь, имена
    /// файлов длинные и похожие, и промах здесь виден не как ошибка, а как
    /// «персонаж почему-то приседает вместо того, чтобы плакать».
    /// </summary>
    public static class AssignIdleFidgets
    {
        private const string AnimDir = "Assets/ImportedActors/AnimLibrary/";
        private const string SetPath = "Assets/Resources/HexLive/NpcAnimSet.asset";

        private static readonly string[] Fidgets =
        {
            "X Bot@Salsa Dancing",
            "X Bot@Air Squat",
            "X Bot@Jumping Jacks",
            "X Bot@Searching Pockets",
        };

        [MenuItem("HexLive/Actors/Assign Idle Fidgets")]
        public static void Assign()
        {
            if (Application.productName != "HexLive")
            {
                return;
            }

            var set = AssetDatabase.LoadAssetAtPath<NpcAnimSet>(SetPath);
            if (set == null)
            {
                Debug.LogError($"[IdleFidgets] NpcAnimSet не найден: {SetPath}");
                return;
            }

            var found = new List<AnimationClip>();
            var missing = new List<string>();
            foreach (var take in Fidgets)
            {
                var clip = Clip(take);
                if (clip != null)
                {
                    found.Add(clip);
                }
                else
                {
                    missing.Add(take);
                }
            }

            set.idleFidgets = found.ToArray();
            set.rejected = Clip("X Bot@Rejected");
            set.crying = Clip("X Bot@Crying");
            set.sadWalk = Clip("X Bot@Sad Walk");

            if (set.rejected == null) missing.Add("X Bot@Rejected");
            if (set.crying == null) missing.Add("X Bot@Crying");
            if (set.sadWalk == null) missing.Add("X Bot@Sad Walk");

            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            Debug.Log($"[IdleFidgets] Безделье: {found.Count} клипов. " +
                $"Сцена: rejected={set.rejected != null} crying={set.crying != null} " +
                $"sadWalk={set.sadWalk != null}." +
                (missing.Count > 0 ? $" НЕ НАЙДЕНЫ: {string.Join(", ", missing)}" : string.Empty));

            if (missing.Count > 0)
            {
                Debug.LogWarning("[IdleFidgets] Ненайденный клип обычно значит, что FBX ещё не " +
                    "импортирован либо импортирован НЕ как Humanoid — проверь Rig у файла в " +
                    AnimDir);
            }
        }

        private static AnimationClip Clip(string takeName)
        {
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(AnimDir + takeName + ".fbx"))
            {
                if (a is AnimationClip c && !c.name.StartsWith("__preview"))
                {
                    return c;
                }
            }

            return null;
        }
    }
}
