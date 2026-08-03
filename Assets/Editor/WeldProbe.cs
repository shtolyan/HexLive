using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Answers one question about a garment mesh: which bones does it actually
/// weight, and how much of it hangs off each.
/// </summary>
/// <remarks>
/// Bind-pose COUNT proves nothing — a mesh can carry 17 bind poses and still put
/// every vertex on bone 0, which looks in game exactly like a pair welded onto
/// one wrist. The weights are in the mesh's binary vertex stream, so this has to
/// be asked of Unity rather than read out of the YAML.
///
/// Writes to a file, not a dialog: the bridge caps a command at 30 s and a modal
/// would own the main thread (see CLAUDE.md).
/// </remarks>
public static class WeldProbe
{
    private const string Report = "Temp/weldprobe.txt";

    [MenuItem("HexLive/Wear/Probe Weld Weights")]
    private static void Run()
    {
        if (Application.productName != "HexLive")
        {
            return;
        }

        var log = new System.Text.StringBuilder();
        foreach (var guid in AssetDatabase.FindAssets(
                     "t:Mesh", new[] { "Assets/ImportedActors/Wear" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.Contains("Anarchy") || !path.EndsWith("Jolly.mesh"))
            {
                continue;
            }

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                continue;
            }

            var perBone = new Dictionary<int, int>();
            foreach (var w in mesh.boneWeights)
            {
                Count(perBone, w.boneIndex0, w.weight0);
                Count(perBone, w.boneIndex1, w.weight1);
                Count(perBone, w.boneIndex2, w.weight2);
                Count(perBone, w.boneIndex3, w.weight3);
            }

            var top = perBone.OrderByDescending(p => p.Value).Take(6)
                .Select(p => $"#{p.Key}:{p.Value}");
            log.AppendLine(
                $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))}: " +
                $"вершин {mesh.vertexCount}, bind-поз {mesh.bindposes.Length}, " +
                $"костей с весом {perBone.Count} — {string.Join(", ", top)}");
        }

        File.WriteAllText(Report, log.ToString());
        Debug.Log($"[WeldProbe] {Report}\n{log}");
    }

    private static void Count(Dictionary<int, int> into, int bone, float weight)
    {
        if (weight <= 0f)
        {
            return;
        }

        into[bone] = into.TryGetValue(bone, out var n) ? n + 1 : 1;
    }
}

