using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Wire the skin relief (`_BumpMap`) on the actors' body materials.
/// </summary>
/// <remarks>
/// Measured before this existed: of the five actors only Marta had any skin
/// normal at all — Jolly, Molly, Jana and Kshishtof had none on any of the 17
/// materials each. The maps were not missing, only unconnected: Jana and
/// Kshishtof already carried theirs in the project (`ACErin*B.jpg`,
/// `coilin_*_bumb_base.jpg`), and Jolly's and Molly's were sitting in the DAZ
/// library (`G3G8CS71*B.jpg`, `ACSabrina*B.jpg`, copied in by
/// Tools/wardrobe/... — see the session notes).
///
/// ⭐ BUMP IS NOT A NORMAL MAP, and this is the whole reason the job is not a
/// one-line SetTexture. Marta's map is a real tangent-space normal (its average
/// pixel is (128,127,255) — the flat-facing blue). Every other actor's map is a
/// GREYSCALE HEIGHT field (average pixel r=g=b, 100% of pixels neutral). Handing
/// a height field to `_BumpMap` as-is reads the height as a direction vector and
/// produces relief that is not merely wrong but wrong QUIETLY — it looks like
/// weak, oddly-lit skin, not like a broken texture. Unity converts height to
/// normal at import time (`convertToNormalMap`), so the importer is forced here
/// rather than trusted, exactly as NewWearExtractor.ApplyNormalMap does for
/// garments.
///
/// Zones come from the DIFFUSE each material already uses, not from a guessed
/// table: DAZ splits Genesis skin into face/arms/legs/torso and the small parts
/// share a big map (Lips and Ears sample Face, Fingernails sample Arms,
/// Toenails sample Legs). Reading that off the built material means a product
/// with its own naming still lands correctly.
/// </remarks>
public static class ActorSkinNormals
{
    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
    private static readonly int BumpScale = Shader.PropertyToID("_BumpScale");

    /// <summary>⭐ The single strength knob. Raise for coarser skin, lower for smoother.</summary>
    /// <remarks>
    /// ONE value for everyone, deliberately — an earlier version scaled it per
    /// map and was worse, in a way worth recording so it is not retried.
    ///
    /// The source maps really are far apart: mean height step on the torso is
    /// 0.0677 for Kshishtof against 0.0028 for Molly, a 24x spread that holds
    /// at 1, 4 and 16 texels apart, so it is structure and not JPEG noise. The
    /// obvious move is to divide each map's import scale by its own roughness
    /// so they meet in the middle. That was done, and the result ranked EXACTLY
    /// by the assigned heightScale: Molly (0.145) and Jana (0.065) came out
    /// worst, Jolly (0.021) bad, Kshishtof (0.0059) mildest — the reverse of
    /// what the maps' own roughness predicted.
    ///
    /// The reason is that Unity renormalises the height range before
    /// differentiating it, so how much contrast the vendor baked in does not
    /// reach the output; heightScale alone sets the amplitude. Any scheme that
    /// "compensates" for source contrast therefore un-equalises what Unity had
    /// already equalised. Measure the source all you like — this particular
    /// number is not something the source can tell you.
    /// </remarks>
    /// Left with headroom on purpose: this is the IMPORT scale and changing it
    /// costs a 4K reimport, so it is set once to a middle value and the actual
    /// tuning happens on _BumpScale, which is live. Too low a value here would
    /// cap how rough the slider can ever go.
    internal const float HeightRelief = 0.01f;

    /// <summary>Starting _BumpScale for a converted height map.</summary>
    /// <remarks>
    /// 0.01 x 0.4 lands just under Kshishtof's 0.0059, the mildest of the four
    /// and the only one described as merely "medium" rather than bad. Tune it
    /// live in HexLive ▸ Actors ▸ Skin Relief rather than editing this.
    /// </remarks>
    internal const float DefaultBumpScale = 0.4f;

    /// <summary>DAZ authors the face at twice the body — `Kshishtof.dtu` has
    /// Bump Strength 1 on Face against 0.5 on Torso, Arms and Legs. Keeping the
    /// ratio means the face does not go doughy when the body is smoothed.</summary>
    internal const float FaceReliefRatio = 2f;

    /// <summary>Strength for actors whose product shipped a REAL normal map.</summary>
    /// <remarks>
    /// Only Marta. Her map is already an encoded direction, so there is no
    /// height to rescale at import and the strength has to be applied on the
    /// material instead. Unlike the four above this number is a judgement, not
    /// a measurement: an encoded normal's xy spread and a height field's
    /// gradient are not the same quantity, so they cannot be equalised
    /// arithmetically. She read as flat at 1.0 and still flat at 1.6, so this
    /// is a starting point to drag from, not an answer.
    /// </remarks>
    internal const float TrueNormalBumpScale = 2.5f;

    // Material name -> the UV zone its relief lives in.
    private static readonly Dictionary<string, string> Zones = new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "Face", "face" }, { "Lips", "face" }, { "Ears", "face" }, { "EyeSocket", "face" },
        { "Arms", "arm" }, { "Fingernails", "arm" },
        { "Legs", "leg" }, { "Toenails", "leg" },
        { "Torso", "torso" },
    };

    // Eyes, lashes, teeth and mouth are deliberately absent: they are wet or
    // hair-like surfaces, and skin-pore relief on a cornea reads as dirt.

    private static readonly string[] ActorRoots =
    {
        "Assets/ImportedActors/Actors",
        "Assets/ImportedActors/Daz3D",
    };

    [MenuItem("HexLive/Actors/Wire Skin Normal Maps")]
    private static void Run()
    {
        if (Application.productName != "HexLive")
        {
            return;
        }

        var wired = 0;
        var converted = 0;
        var skipped = new List<string>();

        foreach (var dir in ActorRoots.SelectMany(EnumerateMaterialDirs))
        {
            var textures = CandidateTextures(dir);
            if (textures.Count == 0)
            {
                continue;
            }

            foreach (var matPath in Directory.GetFiles(dir, "*.mat"))
            {
                var name = Path.GetFileNameWithoutExtension(matPath);
                if (!Zones.TryGetValue(name, out var zone))
                {
                    continue;
                }

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null || !mat.HasProperty(BumpMap))
                {
                    continue;
                }

                var texPath = Pick(textures, zone);
                if (texPath == null)
                {
                    skipped.Add($"{dir}/{name} (no {zone} relief)");
                    continue;
                }

                if (ForceNormalImport(texPath))
                {
                    converted++;
                }

                // Seed the strength only on FIRST wiring. Re-running this menu
                // must not undo tuning done in the Inspector or in the Skin
                // Relief window — the maps and the keyword are structural and
                // safe to re-apply, the strength is somebody's taste and is
                // not. (Same rule as --adopt on the garment side: never clobber
                // a value a human set on purpose.)
                var hadBump = mat.GetTexture(BumpMap) != null;
                mat.SetTexture(BumpMap, AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
                if (!hadBump)
                {
                    SetRelief(mat, name,
                        IsTrueNormal(texPath) ? TrueNormalBumpScale : DefaultBumpScale);
                }
                // URP Lit samples _BumpMap only with this on. The actors' skin
                // carried `_NORMALMAP_TANGENT_SPACE` instead — a Built-in-era
                // keyword URP does not read — which is why a map assigned by
                // hand appeared to do nothing.
                mat.EnableKeyword("_NORMALMAP");
                EditorUtility.SetDirty(mat);
                wired++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[SkinNormals] подключено материалов: {wired}, " +
                  $"текстур переимпортировано как нормали: {converted}" +
                  (skipped.Count > 0
                      ? $"\n  БЕЗ РЕЛЬЕФА {skipped.Count}:\n    " + string.Join("\n    ", skipped.Take(20))
                      : ""));
    }

    private static IEnumerable<string> EnumerateMaterialDirs(string root)
    {
        return Directory.Exists(root)
            ? Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Where(d => Directory.GetFiles(d, "*.mat").Length > 0)
            : Enumerable.Empty<string>();
    }

    /// <summary>Image files next to the materials, or in a sibling Textures/.</summary>
    private static List<string> CandidateTextures(string matDir)
    {
        var dirs = new List<string> { matDir };
        var parent = Path.GetDirectoryName(matDir)?.Replace('\\', '/');
        if (parent != null && Directory.Exists($"{parent}/Textures"))
        {
            dirs.Add($"{parent}/Textures");
        }

        return dirs
            .SelectMany(d => Directory.GetFiles(d))
            .Where(f => f.EndsWith(".jpg") || f.EndsWith(".png") || f.EndsWith(".jpeg"))
            .Select(f => f.Replace('\\', '/'))
            .ToList();
    }

    /// <summary>
    /// The relief map for a zone, preferring a true normal over a height field.
    /// </summary>
    /// <remarks>
    /// DAZ suffixes are not one convention: `…FaceN` is a normal, `…FaceB` and
    /// `…_face_bumb_base` are heights, and `…FaceS` is SPECULAR and must never
    /// be taken — it also contains "face" and would otherwise win on a plain
    /// substring match.
    ///
    /// ⭐ The HEIGHT map wins when a product ships both, which is the opposite
    /// of the obvious rule. Marta has both, was given her normal map on that
    /// obvious rule, and came out glass-smooth at every strength up to 5. The
    /// reason is in the files: her `TorsoN` measures 0.00028 of detail against
    /// 0.03910 in her own `TorsoB` — 140x — because the vendor baked the normal
    /// for large forms and left the pores in the bump. Nothing downstream can
    /// recover detail that is not in the map, so the choice has to be made
    /// here. A true normal is still used when it is all there is.
    /// </remarks>
    private static string Pick(List<string> textures, string zone)
    {
        string bump = null;
        string normal = null;
        foreach (var path in textures)
        {
            var f = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (!f.Contains(zone))
            {
                continue;
            }

            // Vendors bolt a tile index onto the suffix — Marta's normal is
            // `Vex3ds--Alydia--FaceN_1001`, so the meaningful last letter is
            // not the last character. Trim the trailing digits/underscores
            // before reading it.
            var stem = f.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');

            if (stem.EndsWith("b") || f.Contains("bump") || f.Contains("bumb"))
            {
                bump ??= path;
            }
            else if (f.Contains("normal") || stem.EndsWith("n"))
            {
                normal ??= path;
            }
        }

        return bump ?? normal;
    }

    /// <summary>Apply a body strength to one skin material, doubling the face.</summary>
    internal static void SetRelief(Material mat, string materialName, float bodyStrength)
    {
        if (mat == null || !mat.HasProperty(BumpScale))
        {
            return;
        }

        var isFace = Zones.TryGetValue(materialName, out var zone) && zone == "face";
        mat.SetFloat(BumpScale, bodyStrength * (isFace ? FaceReliefRatio : 1f));
        EditorUtility.SetDirty(mat);
    }

    /// <summary>Every actor folder that holds skin materials, with them.</summary>
    internal static IEnumerable<(string actor, string dir, List<Material> mats)> SkinMaterials()
    {
        foreach (var dir in ActorRoots.SelectMany(EnumerateMaterialDirs))
        {
            var mats = new List<Material>();
            foreach (var p in Directory.GetFiles(dir, "*.mat"))
            {
                if (!Zones.ContainsKey(Path.GetFileNameWithoutExtension(p)))
                {
                    continue;
                }

                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m != null && m.GetTexture(BumpMap) != null)
                {
                    mats.Add(m);
                }
            }

            if (mats.Count > 0)
            {
                // ".../Actors/Jolly/Materials" and ".../Daz3D/Jana/Genesis3Female"
                // both name the actor one level up from the material folder.
                var actor = Path.GetFileName(Path.GetDirectoryName(dir.Replace('\\', '/')));
                yield return (actor, dir.Replace('\\', '/'), mats);
            }
        }
    }

    /// <summary>An already-encoded normal map, i.e. one with nothing to convert.</summary>
    private static bool IsTrueNormal(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            return false;
        }

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        return !settings.convertToNormalMap;
    }

    /// <summary>Make Unity treat the file as a normal map, converting height if needed.</summary>
    private static bool ForceNormalImport(string path)
    {
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
        {
            return false;
        }

        var grey = IsHeightField(path);

        // ⭐ Go through TextureImporterSettings, NOT the importer properties.
        // `importer.convertToNormalmap = true` is validated against the
        // textureType the importer currently HAS, and `textureType` does not
        // take effect until the reimport — so assigning both in sequence
        // silently drops the conversion. Measured after the first run: all
        // sixteen maps came out `textureType: 1, convertToNormalMap: 0`, i.e.
        // a greyscale height field being read as an encoded normal, which is
        // the exact quiet-wrongness this method exists to prevent.
        // `heightScale` landed correctly, which is what made the failure look
        // like the code had worked.
        // One import scale for every map (see the note on HeightRelief). It is
        // set once and then left alone: the strength you actually tune lives on
        // the material as _BumpScale, which is live.
        var scale = grey ? HeightRelief : 0f;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);

        // heightmapScale is part of "already correct". Leaving it out of this
        // comparison would make retuning HeightRelief a no-op: the maps are
        // already NormalMap+convert from the previous run, the method would
        // report nothing to do, and the new strength would never be imported.
        if (settings.textureType == TextureImporterType.NormalMap &&
            settings.convertToNormalMap == grey &&
            (!grey || Mathf.Abs(settings.heightmapScale - scale) < 1e-4f))
        {
            return false;
        }

        settings.textureType = TextureImporterType.NormalMap;
        settings.convertToNormalMap = grey;   // height field -> generated normal
        if (grey)
        {
            settings.heightmapScale = scale;
        }

        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();
        return true;
    }

    /// <summary>
    /// True when the image is a height field rather than an encoded normal.
    /// </summary>
    /// <remarks>
    /// Decided from the PIXELS, not the filename. A tangent normal is dominated
    /// by its blue channel (flat areas are (128,128,255)); a height field has
    /// r == g == b everywhere.
    ///
    /// ⭐ The file is decoded FROM DISK, never through AssetDatabase. Once a map
    /// has been imported as a normal map Unity compresses it to DXT5nm, which
    /// moves X into alpha and leaves R and B as garbage — so a height field
    /// read back through the importer no longer looks grey, the check flips to
    /// "already a normal", and the conversion never gets switched on. That
    /// makes the method non-idempotent in the one direction that matters: it
    /// would refuse to repair its own bad first run.
    /// </remarks>
    internal static bool IsHeightField(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return true;
        }

        var probe = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!probe.LoadImage(bytes))
            {
                return true;
            }

            var pixels = probe.GetPixels32();
            var w = probe.width;
            var h = probe.height;

            // Greyness: a sparse grid is plenty to tell a height field from an
            // encoded normal.
            var neutral = 0;
            var counted = 0;
            for (var y = 0; y < h; y += Mathf.Max(1, h / 32))
            {
                for (var x = 0; x < w; x += Mathf.Max(1, w / 32))
                {
                    var p = pixels[y * w + x];
                    if (Mathf.Max(p.r, Mathf.Max(p.g, p.b)) -
                        Mathf.Min(p.r, Mathf.Min(p.g, p.b)) <= 8)
                    {
                        neutral++;
                    }

                    counted++;
                }
            }

            return counted == 0 || neutral > counted * 0.8f;
        }
        finally
        {
            Object.DestroyImmediate(probe);
        }
    }
}
