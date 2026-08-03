using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Находит расцветки, которые отличаются от прототипа только на бумаге.
/// </summary>
/// <remarks>
/// Отсев по именам файлов ловит лишь точные совпадения, а вендор нередко даёт
/// расцветке СВОЙ атлас, отличающийся не там, где эта вещь его читает: у
/// рабочих ботинок «Clear» стоит `LNA_RiotM2_Denim_albedo.jpg` вместо
/// `..._Square_...`, и по данным это разные расцветки, а на девушке — одна и та
/// же. Разница, измеренная по СОБСТВЕННЫМ UV меша, там 14 из 255.
///
/// Поэтому сравнивается не имя файла, а то, что меш с него берёт: по вершинам
/// каждого подмеша, из текстур, прочитанных с диска (импортированные в игре
/// нечитаемы). Отчёт пишется файлом — решение, удалять или нет, остаётся за
/// человеком: два оттенка чёрного бывают и осмысленными.
/// </remarks>
internal static class VariantLookAlikeProbe
{
    private const string Report = "Temp/lookalike.txt";

    // 20 из 255 (~8%). Ниже — глаз на теле под общим светом уже не различает;
    // выше — начинается «тёмно-синий против чёрного», который различает.
    private const float Threshold = 20f;

    [MenuItem("HexLive/Wear/Probe Look-alike Variants")]
    private static void Run()
    {
        if (Application.productName != "HexLive")
        {
            return;
        }

        var art = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var folder in AssetDatabase.GetSubFolders("Assets/Resources/HexLive/Wear"))
        {
            var prefab = AssetDatabase.FindAssets("t:GameObject", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .FirstOrDefault(p => p != null && p.GetComponentInChildren<SkinnedMeshRenderer>(true) != null);
            var smr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (smr != null && smr.sharedMesh != null)
            {
                art[Path.GetFileName(folder)] = smr;
            }
        }

        var readable = new Dictionary<string, Texture2D>();
        var log = new System.Text.StringBuilder();
        var same = new List<string>();

        foreach (var def in AssetDatabase.FindAssets("t:GarmentDefinition")
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .Select(AssetDatabase.LoadAssetAtPath<GarmentDefinition>)
                     .Where(d => d != null && d.variantMaterials != null && d.variantMaterials.Length > 0)
                     .OrderBy(d => d.id))
        {
            if (!art.TryGetValue(def.ArtId, out var smr))
            {
                continue;
            }

            var worst = Difference(smr, def, readable);
            if (worst < 0f)
            {
                continue;
            }

            log.AppendLine($"{def.id}: {worst:0.0} из 255{(worst < Threshold ? "   <-- как прототип" : "")}");
            if (worst < Threshold)
            {
                same.Add(def.id);
            }
        }

        log.AppendLine();
        log.AppendLine(same.Count == 0
            ? "неотличимых расцветок нет"
            : $"неотличимы от прототипа ({same.Count}): {string.Join(" ", same)}");

        File.WriteAllText(Report, log.ToString());
        Debug.Log($"[LookAlike] {Report}\n{log}");
    }

    // Наибольшее расхождение по подмешам: вещь считается той же, только если
    // совпало ВСЁ — перекрашенная пряжка на общем фоне это тоже расцветка.
    private static float Difference(SkinnedMeshRenderer smr, GarmentDefinition def,
                                    Dictionary<string, Texture2D> readable)
    {
        var mesh = smr.sharedMesh;
        var uv = mesh.uv;
        if (uv == null || uv.Length == 0)
        {
            return -1f;
        }

        var worst = 0f;
        for (var sub = 0; sub < mesh.subMeshCount && sub < smr.sharedMaterials.Length; sub++)
        {
            var from = FromDisk(smr.sharedMaterials[sub], readable);
            var to = sub < def.variantMaterials.Length && def.variantMaterials[sub] != null
                ? FromDisk(def.variantMaterials[sub], readable)
                : from;
            if (from == null || to == null || from == to)
            {
                continue;
            }

            var tris = mesh.GetTriangles(sub);
            double sum = 0;
            var count = 0;
            for (var i = 0; i < tris.Length; i += 30)
            {
                var point = uv[tris[i]];
                var a = from.GetPixelBilinear(point.x, point.y);
                var b = to.GetPixelBilinear(point.x, point.y);
                sum += (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b)) / 3f;
                count++;
            }

            if (count > 0)
            {
                worst = Mathf.Max(worst, (float)(sum / count) * 255f);
            }
        }

        return worst;
    }

    // Импортированная текстура в игре нечитаема, а ставить всем галку Read/Write
    // — это удвоить память ради проверки. Файл с диска даёт то же самое и ничего
    // не меняет в проекте.
    private static Texture2D FromDisk(Material material, Dictionary<string, Texture2D> cache)
    {
        var texture = material != null ? material.GetTexture("_BaseMap") : null;
        var path = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (!cache.TryGetValue(path, out var loaded))
        {
            loaded = new Texture2D(2, 2);
            loaded.LoadImage(File.ReadAllBytes(path));
            cache[path] = loaded;
        }

        return loaded;
    }
}
