using HexLive.Simulation.Content;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Spec §59.3: HexLive ▸ Export Sim Data (JSON). Applies every tuning
    /// asset layer (the same loaders the game runs at boot) and writes the
    /// resulting catalogs to <c>SimData/simdata.json</c> via the sim-side
    /// serializer (SimDataFile.ExportJson — ONE schema, one writer). Headless
    /// probes call SimDataFile.Require(path) first thing; running on code
    /// defaults is forbidden. Re-export after tuning assets.
    /// </summary>
    public static class ExportSimData
    {
        [MenuItem("HexLive/Export Sim Data (JSON)")]
        public static void Export()
        {
            string path = null;
            try
            {
                // §59: the coverage gate runs first — export refuses to write
                // a file while any tuning static is missing from the asset layer.
                var coverage = BalanceTuningEditor.Validate();
                if (coverage.Count > 0)
                {
                    throw new System.InvalidOperationException(
                        "Tuning coverage FAILED:\n" + string.Join("\n", coverage));
                }

                HexLive.UnityPresentation.Config.MobTuning.LoadAndApply();
                HexLive.UnityPresentation.Config.GearTuning.LoadAndApply();
                HexLive.UnityPresentation.Config.ObjectTuning.LoadAndApply();

                path = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", SimDataFile.DefaultRelativePath));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var json = SimDataFile.ExportJson();

                // Round-trip guard: the file is useless if the sim-side reader
                // cannot parse what we just wrote.
                if (!SimDataFile.ApplyJson(json))
                {
                    throw new System.InvalidOperationException(
                        "Export produced JSON that SimDataFile.ApplyJson cannot parse — file NOT usable by headless probes.");
                }

                System.IO.File.WriteAllText(path, json);

                var summary =
                    $"mobs: {Count(json, "\"maxHealth\"")}, gear: {Count(json, "\"meleePriority\"")}, " +
                    $"worldObjects: {Count(json, "\"displayName\"")}, recipes: {Count(json, "\"output\"")}";
                Debug.Log($"[ExportSimData] OK — written {path} ({summary})");
                EditorUtility.DisplayDialog(
                    "Export Sim Data",
                    $"Успешно выгружено:\n{path}\n\n{summary}",
                    "OK");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ExportSimData] FAILED: {e}");
                EditorUtility.DisplayDialog(
                    "Export Sim Data — ОШИБКА",
                    $"Экспорт НЕ выполнен{(path != null ? $" ({path})" : "")}:\n\n{e.Message}",
                    "OK");
            }
        }

        private static int Count(string json, string marker)
        {
            var count = 0;
            for (var i = json.IndexOf(marker, System.StringComparison.Ordinal); i >= 0;
                 i = json.IndexOf(marker, i + marker.Length, System.StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
