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
            HexLive.UnityPresentation.Config.MobTuning.LoadAndApply();
            HexLive.UnityPresentation.Config.GearTuning.LoadAndApply();
            HexLive.UnityPresentation.Config.ObjectTuning.LoadAndApply();

            var path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", SimDataFile.DefaultRelativePath));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, SimDataFile.ExportJson());
            Debug.Log($"[ExportSimData] Written {path}");
        }
    }
}
