using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// §70 / §31B.1a — довести материалы ГЛАЗ Kshishtof до рабочего URP-вида.
    ///
    /// Кожа из molly_copy приехала уже нормальным URP Lit, а глаза — нет: у них
    /// вовсе отсутствуют свойства поверхности (_Surface, _Blend, _AlphaClip).
    /// Роговица и глазная влага — это ПРОЗРАЧНЫЕ оболочки поверх радужки; без
    /// _Surface = 1 URP рисует их сплошными, и глаз становится белым бельмом.
    ///
    /// Состояние рендера переносится с эталонного актёра (Jolly, у неё глаза
    /// работают) — целиком: float-свойства, ключевые слова шейдера и очередь.
    /// ТЕКСТУРЫ И ЦВЕТ при этом остаются его собственные, иначе он получил бы
    /// её карие глаза вместо своих. Исключение — роговица и влага: они
    /// полностью прозрачны, поэтому у них переносится и _BaseColor (там важна
    /// именно альфа 0).
    ///
    /// Идемпотентно: жать можно сколько угодно раз.
    /// </summary>
    public static class FixKshishtofEyes
    {
        private const string TargetDir = "Assets/ImportedActors/Daz3D/Kshishtof/Genesis3Male";
        private const string ReferenceDir = "Assets/ImportedActors/Actors/Jolly/Materials";

        // Свойства, задающие РЕЖИМ рисования (а не внешность).
        private static readonly string[] RenderState =
        {
            "_Surface", "_Blend", "_AlphaClip", "_Cutoff", "_ZWrite", "_ZWriteControl",
            "_SrcBlend", "_DstBlend", "_AlphaToMask", "_QueueOffset", "_BlendModePreserveSpecular",
        };

        // Прозрачные оболочки — им переносим ещё и цвет (нужна альфа 0).
        private static readonly HashSet<string> Shells = new() { "Cornea", "EyeMoisture" };

        private static readonly string[] Slots =
        {
            "Cornea", "EyeMoisture", "Sclera", "Irises", "Pupils", "Eyelashes",
        };

        [MenuItem("HexLive/Actors/Fix Kshishtof Eyes")]
        public static void Fix()
        {
            if (Application.productName != "HexLive")
            {
                Debug.LogWarning("[KshishtofEyes] Не тот проект — выходим.");
                return;
            }

            var report = new List<string>();
            var touched = 0;

            foreach (var slot in Slots)
            {
                var target = AssetDatabase.LoadAssetAtPath<Material>($"{TargetDir}/{slot}.mat");
                var reference = AssetDatabase.LoadAssetAtPath<Material>($"{ReferenceDir}/{slot}.mat");

                if (target == null)
                {
                    report.Add($"  {slot}: нет материала у Kshishtof — пропуск");
                    continue;
                }

                if (reference == null)
                {
                    report.Add($"  {slot}: нет эталона у Jolly — пропуск");
                    continue;
                }

                if (target.shader != reference.shader)
                {
                    // Оба должны быть URP Lit; иначе перенос свойств бессмыслен.
                    report.Add($"  {slot}: РАЗНЫЕ шейдеры " +
                               $"({target.shader.name} vs {reference.shader.name}) — пропуск");
                    continue;
                }

                var before = $"_Surface={Read(target, "_Surface")} queue={target.renderQueue}";

                foreach (var prop in RenderState)
                {
                    if (reference.HasProperty(prop) && target.HasProperty(prop))
                    {
                        target.SetFloat(prop, reference.GetFloat(prop));
                    }
                }

                if (Shells.Contains(slot) && reference.HasProperty("_BaseColor") &&
                    target.HasProperty("_BaseColor"))
                {
                    target.SetColor("_BaseColor", reference.GetColor("_BaseColor"));
                }

                // Ключевые слова решают, какой вариант шейдера соберётся
                // (_SURFACE_TYPE_TRANSPARENT, _ALPHATEST_ON): без них float-ы
                // выставлены, а рисуется по-прежнему непрозрачно.
                target.shaderKeywords = reference.shaderKeywords;
                target.renderQueue = reference.renderQueue;

                EditorUtility.SetDirty(target);
                touched++;
                report.Add($"  {slot}: {before}  ->  " +
                           $"_Surface={Read(target, "_Surface")} queue={target.renderQueue}");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[KshishtofEyes] Обновлено материалов: {touched}\n" + string.Join("\n", report));
        }

        private static string Read(Material material, string prop) =>
            material.HasProperty(prop) ? material.GetFloat(prop).ToString("0") : "нет";
    }
}
