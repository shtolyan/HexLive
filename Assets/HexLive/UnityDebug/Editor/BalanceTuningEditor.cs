using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Config;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// §59: сторож дрифта баланс-ассетов + захват живых значений.
    ///
    /// Validate Tuning Coverage — рефлексией перечисляет ВСЕ тюнинг-статики
    /// (BalanceReflection: SimBalance, Spec49/50/53/57/62, HexHopTuning) и
    /// проверяет, что каждый покрыт ровно одним полем ровно одного конфиг-
    /// ассета (SimConfigMirror-разметка). Непокрытый статик или двойное
    /// покрытие = ошибка: новая константа больше не может молча избежать
    /// ассет-слоя. Вызывается и из Export Sim Data.
    ///
    /// Capture Live Tuning — читает живые статики обратно во все балансовые
    /// ассеты (+ HexTuningConfig) и сохраняет их: «сфотографировать» значения,
    /// накрученные в рантайме.
    /// </summary>
    public static class BalanceTuningEditor
    {
        [MenuItem("HexLive/Validate Tuning Coverage")]
        public static void ValidateMenu()
        {
            // Console only — a modal box here holds Unity's main thread, and
            // therefore the MCP bridge, until a human clicks it. See the same
            // note in ExportSimData.
            var errors = Validate();
            if (errors.Count == 0)
            {
                Debug.Log("[BalanceTuning] Coverage OK — каждый тюнинг-статик покрыт ровно одним конфигом.");
            }
            else
            {
                Debug.LogError("[BalanceTuning] Coverage FAILED:\n" + string.Join("\n", errors));
            }
        }

        /// <summary>Пустой список = покрытие полное. Иначе — список ошибок.
        /// Кидает, если сама разметка конфига битая (unmapped/ambiguous поле).</summary>
        public static List<string> Validate()
        {
            var errors = new List<string>();

            // Who covers what: config assets on disk (the boot set) + the
            // HexTuningConfig asset (hop block).
            var covered = new Dictionary<string, string>(); // static key -> config type
            var configs = BalanceTuning.LoadAll();
            // §152: HexTuningConfig переехал из Resources в атомарный контент
            // (RuntimeSource) — прямой Resources.Load отдаёт null, и гейт
            // покрытия ложно валил экспорт simdata на всех hop/swim ручках.
            // В редакторе ассет ищется как у BalanceTuning.LoadAll — через
            // AssetDatabase.
            var hexConfig = Resources.Load<HexTuningConfig>(HexTuning.ResourcePath);
            if (hexConfig == null)
            {
                foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:HexTuningConfig"))
                {
                    hexConfig = UnityEditor.AssetDatabase.LoadAssetAtPath<HexTuningConfig>(
                        UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                    if (hexConfig != null) break;
                }
            }
            if (hexConfig != null)
            {
                configs.Add(hexConfig);
            }

            foreach (var config in configs)
            {
                foreach (var (_, staticField) in SimConfigMirror.ResolveMappings(config.GetType()))
                {
                    var key = staticField.DeclaringType?.Name + "." + staticField.Name;
                    if (covered.TryGetValue(key, out var other))
                    {
                        errors.Add($"{key} покрыт ДВАЖДЫ: {other} и {config.GetType().Name} — второй Apply затирает первый.");
                    }
                    else
                    {
                        covered[key] = config.GetType().Name;
                    }
                }
            }

            foreach (var pair in BalanceReflection.EnumerateFields())
            {
                if (!covered.ContainsKey(pair.Key))
                {
                    errors.Add($"{pair.Key} НЕ покрыт ни одним конфиг-ассетом (тип {pair.Value.FieldType.Name}).");
                }
            }

            if (configs.Count == 0)
            {
                errors.Add($"В Resources/{BalanceTuning.ResourceFolder} нет ни одного баланс-ассета.");
            }

            return errors;
        }

        [MenuItem("HexLive/Capture Live Tuning")]
        public static void CaptureAll()
        {
            var configs = BalanceTuning.LoadAll();
            foreach (var config in configs)
            {
                Undo.RecordObject(config, "Capture Live Tuning");
                SimConfigMirror.Capture(config);
                EditorUtility.SetDirty(config);
            }

            var hexConfig = Resources.Load<HexTuningConfig>(HexTuning.ResourcePath);
            if (hexConfig != null)
            {
                Undo.RecordObject(hexConfig, "Capture Live Tuning");
                HexTuning.Capture(hexConfig);
                EditorUtility.SetDirty(hexConfig);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[BalanceTuning] Captured live statics into {configs.Count} balance config(s) + HexTuningConfig.");
        }
    }
}
