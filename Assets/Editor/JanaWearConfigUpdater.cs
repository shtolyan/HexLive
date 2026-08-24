#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using HexLive.UnityPresentation.Wearing;

// ---------------------------------------------------------------------------
//  Jana wardrobe updater.
//
//  Every garment's Wear component has a per-actor `configs` list (actorName ->
//  fitted mesh). Imported garments often carry Molly/Jolly/Marta configs but
//  no Jana (=5), so Jana falls back to the default mesh and looks wrong.
//
//  This walks every Wear prefab under HexLiveContent/Wear and, where a Jana
//  config is missing, adds one:
//    * mesh = the sibling "Jana.mesh" next to an existing config's mesh
//             (that's the mesh we pulled from jana all wear.fbx), or
//    * fallback = reuse an existing config's mesh (Jolly/Molly fit) so the
//      garment at least wears correctly on Jana.
//
//  Idempotent: re-running skips garments that already have a Jana config.
//  Menu:  HexLive/Wear/Add Jana To All Garments
// ---------------------------------------------------------------------------
public static class JanaWearConfigUpdater
{
    const int JanaActor = 5;            // ActorName.Jana
    const string WearRoot = "Assets/HexLiveContent/Wear";

    [MenuItem("HexLive/Wear/Add Jana To All Garments")]
    static void Apply()
    {
        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { WearRoot });
        int added = 0, already = 0, skipped = 0, usedJanaMesh = 0, usedFallback = 0;
        var log = new System.Text.StringBuilder();

        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var wear = root.GetComponentInChildren<Wear>(true);
                if (wear == null) { skipped++; continue; }

                var so = new SerializedObject(wear);
                var configs = so.FindProperty("configs");
                if (configs == null || !configs.isArray) { skipped++; continue; }

                // Scan existing configs: has Jana already? grab a source mesh/scale.
                var hasJana = false;
                Mesh srcMesh = null;
                var srcScale = 1f;
                for (var i = 0; i < configs.arraySize; i++)
                {
                    var e = configs.GetArrayElementAtIndex(i);
                    if (e.FindPropertyRelative("actorName").enumValueIndex == JanaActor) { hasJana = true; break; }
                    var m = e.FindPropertyRelative("mesh").objectReferenceValue as Mesh;
                    if (srcMesh == null && m != null)
                    {
                        srcMesh = m;
                        srcScale = e.FindPropertyRelative("scale").floatValue;
                    }
                }

                if (hasJana) { already++; continue; }
                if (srcMesh == null) { skipped++; log.AppendLine("  no source mesh: " + Path.GetFileName(path)); continue; }

                // Prefer a real Jana-fitted mesh sitting next to the others.
                var dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(srcMesh));
                var janaMesh = AssetDatabase.LoadAssetAtPath<Mesh>(dir + "/Jana.mesh");
                if (janaMesh != null) usedJanaMesh++; else { janaMesh = srcMesh; usedFallback++; }

                // Append a Jana config.
                configs.arraySize++;
                var ne = configs.GetArrayElementAtIndex(configs.arraySize - 1);
                ne.FindPropertyRelative("actorName").enumValueIndex = JanaActor;
                ne.FindPropertyRelative("scale").floatValue = srcScale;
                ne.FindPropertyRelative("mesh").objectReferenceValue = janaMesh;
                so.ApplyModifiedProperties();

                PrefabUtility.SaveAsPrefabAsset(root, path);
                added++;
                log.AppendLine($"  +Jana {Path.GetFileNameWithoutExtension(path)} -> {janaMesh.name} ({(janaMesh == srcMesh ? "fallback" : "Jana.mesh")})");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[JanaWear] added={added} (Jana.mesh={usedJanaMesh}, fallback={usedFallback}), alreadyHad={already}, skipped={skipped}\n{log}");
    }
}
#endif
