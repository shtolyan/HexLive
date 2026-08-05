using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// §105: раскладывает позы СНА по слоту <c>NpcAnimSet.sleep</c>.
    /// Идемпотентно, можно жать сколько угодно.
    /// Меню: <b>HexLive ▸ Actors ▸ Assign Sleep Poses</b>.
    ///
    /// <para>
    /// Зачем варианты: четыре тела в одной и той же позе у костра читаются как
    /// копипаста. Вариант выбирается по id колонистки и держится всю жизнь —
    /// это не украшение кадра, а её привычка спать.
    /// </para>
    ///
    /// <para>
    /// Первым в списке идёт авторский <c>Sleep</c> — тот же клип, что стоит в
    /// состоянии контроллера. Так «вариантов не назначили» и «выпал первый
    /// вариант» дают ОДИН И ТОТ ЖЕ кадр, и пустой ассет остаётся рабочим
    /// состоянием, а не дырой.
    /// </para>
    /// </summary>
    public static class AssignSleepPoses
    {
        private const string AnimDir = "Assets/ImportedActors/AnimLibrary/";
        private const string SetPath = "Assets/Resources/HexLive/NpcAnimSet.asset";

        // (файл FBX, имя клипа). Обычно совпадают, но у второй позы клип —
        // «Bed»-вариант из того же FBX: он довёрнут orientationOffsetY так,
        // чтобы лежать в ту же сторону, что конец LieDown / Sleep / старт
        // GetUp (баг #6 — «легла в одном повороте, спит в другом»). Оригинал
        // без доворота остаётся в файле: на нём стоит состояние FallenIdle,
        // выровненное под StandUp.
        private static readonly (string file, string clip)[] Poses =
        {
            ("Sleep", "Sleep"),      // авторский — на нём стоит состояние Sleep
            ("X Bot@Sleeping Idle", "X Bot@Sleeping Idle Bed"), // §105: вторая поза
        };

        [MenuItem("HexLive/Actors/Assign Sleep Poses")]
        public static void Assign()
        {
            if (Application.productName != "HexLive")
            {
                return;
            }

            var set = AssetDatabase.LoadAssetAtPath<NpcAnimSet>(SetPath);
            if (set == null)
            {
                Debug.LogError($"[SleepPoses] NpcAnimSet не найден: {SetPath}");
                return;
            }

            var found = new List<AnimationClip>();
            var missing = new List<string>();
            foreach (var take in Poses)
            {
                var clip = Clip(take.file, take.clip);
                if (clip != null)
                {
                    found.Add(clip);
                }
                else
                {
                    missing.Add(take.clip);
                }
            }

            set.sleep = found.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            Debug.Log($"[SleepPoses] Поз сна: {found.Count} " +
                $"({string.Join(", ", found.ConvertAll(c => c.name))})." +
                (missing.Count > 0 ? $" НЕ НАЙДЕНЫ: {string.Join(", ", missing)}" : string.Empty));

            if (missing.Count > 0)
            {
                Debug.LogWarning("[SleepPoses] Ненайденный клип обычно значит, что FBX ещё не " +
                    "импортирован либо импортирован НЕ как Humanoid — проверь Rig у файла в " +
                    AnimDir);
            }
        }

        private static AnimationClip Clip(string fileName, string clipName)
        {
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(AnimDir + fileName + ".fbx"))
            {
                if (a is AnimationClip c && c.name == clipName)
                {
                    return c;
                }
            }

            return null;
        }
    }
}
