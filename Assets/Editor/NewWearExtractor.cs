#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  New-wear extractor (spec §31B.4, "new wear" drop of 2026-07 and the
//  Sweet Jane / Fitness Idol drop of 2026-08).
//
//  Assets/Temp holds one DAZ FBX export per girl per drop, each wearing the
//  SAME garments fitted to her body. This tool pulls the garment meshes out of
//  every FBX and assembles the standard wear assets:
//
//    Assets/ImportedActors/Wear/<Folder>/Meshes/<Actor>.mesh   (per girl)
//    Assets/ImportedActors/Wear/<Folder>/Materials/<mat>.mat   (URP Lit)
//    Assets/ImportedActors/Wear/<Folder>/Textures/*            (copied earlier)
//    Assets/Resources/HexLive/Wear/<simId>/<Name>.prefab       (Wear prefab)
//
//  The prefab follows the molly_copy wear contract (spec §31B.2): root Wear
//  component + its own bone subtree under "hip" + one SkinnedMeshRenderer;
//  per-actor meshes live in WearConfig. Dirt / blood / wetness / tear need no
//  per-item work — NpcActorView drives them through Wear.SetGrime/SetWetness/
//  SetErosion on every equipped garment.
//
//  Runs automatically once after compile (only while some target prefab is
//  missing and the Temp FBXes exist), then rebuilds the GarmentCatalog and
//  re-exports SimData. Re-runs are idempotent; use the Force menu item to
//  stamp everything again after changing specs below.
//
//  Menu: HexLive/Wear/Extract New Wear (Temp FBX)
//        HexLive/Wear/Extract New Wear (Force Re-Extract)
// ---------------------------------------------------------------------------
public static class NewWearExtractor
{
    private const string ImportRoot = "Assets/ImportedActors/Wear";
    private const string WearRoot = "Assets/Resources/HexLive/Wear";

    // Where the automated pipeline drops its manifests — now the ONLY source of
    // garments. tools/wardrobe writes the JSON and the extractor picks it up, so
    // new clothing needs no C# at all.
    private const string DropRoot = "Assets/Editor/WearDrops";

    // FBX file -> which girl the garments are fitted to. One file per girl per
    // drop; a girl may appear more than once (different drops carry different
    // garments), so the renderer index is keyed by (actor, garment), not by file.
    // Empty on purpose. Every drop — including the 2026-07 one that used to be
    // hardcoded here — now lives in Assets/Editor/WearDrops/*.json. Keeping a
    // copy in C# was actively harmful: LoadDrop skips a garment whose simId is
    // already described, and the built-ins were seeded FIRST, so the JSON entry
    // lost. Those eight pieces kept being built from the old `* new wear.fbx`
    // exports, which is how Jana's re-fit silently missed them.
    private static readonly (string path, ActorName actor, string drop)[] BuiltInSources = { };

    // A DROP IS THE UNIT OF WORK. `tools/wardrobe` writes this file with the
    // name of the drop it just built, and everything below is then scoped to it:
    // that drop's FBX files are the only ones instantiated and its garments the
    // only ones (re)built. Without it every run touched the whole wardrobe —
    // one new pair of knickers re-stamped all 23 garments, re-imported every
    // girl's export, and rewrote materials other people had tuned by hand.
    //
    // Absent = no scope, i.e. everything. That is the deliberate manual case,
    // and it says so in the log rather than happening quietly.
    private const string ActiveDropFile = DropRoot + "/_active.txt";

    // Built-ins + every JSON drop, resolved once per domain reload.
    private static (string path, ActorName actor, string drop)[] _sources;
    private static GarmentSpec[] _garments;

    private static (string path, ActorName actor, string drop)[] Sources
    {
        get { EnsureLoaded(); return _sources; }
    }

    /// <summary>Name of the drop this run is limited to, or null for all.</summary>
    private static string ActiveDrop()
    {
        if (!File.Exists(ActiveDropFile))
        {
            return null;
        }

        var name = File.ReadAllText(ActiveDropFile).Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static GarmentSpec[] Garments
    {
        get { EnsureLoaded(); return _garments; }
    }

    private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");

    private sealed class MatSpec
    {
        public string Source;       // material name inside the FBX
        // Other FBX material names that should land on this same material. A
        // DAZ prop splits one texture atlas across dozens of named surfaces —
        // the headdress has eighteen for the helmet alone — and shipping a
        // submesh per surface would be eighteen draw calls for one hat.
        public string[] Aliases = { };
        public string Texture;      // file under <Folder>/Textures (null = plain color)
        public Color Color = Color.white;
        // Matte by default — the drop manifest is what normally sets this, and a
        // glossy garment mirrors the skybox instead of showing its own texture
        // (see the note beside `smoothness` in Tools/wardrobe/wardrobe/manifest.py).
        public float Smoothness;
        public float Metallic;
        public bool DoubleSided = true;
        public bool AlphaClip;
    }

    private sealed class GarmentSpec
    {
        public string Drop;         // which manifest described it
        public string SourceKey;    // mesh name inside the FBX
        // Set when the garment arrives as an ASSEMBLY of meshes rather than
        // one: every renderer whose name starts with one of these is welded
        // into a single skinned mesh under SourceKey. See MergeParts.
        public string[] SourceKeys;
        // Materials whose geometry is dropped from the built mesh — see the
        // note on DropGarment.dropMaterials.
        public string[] DropMaterials = { };
        public string Folder;       // ImportedActors/Wear/<Folder>
        public string Name;         // prefab + root GameObject name
        public string SimId;        // Resources/HexLive/Wear/<SimId>/
        public VisualWearLayer Layer;
        public VisualWearSlot[] Slots;
        public VisualWearSlot[] NoHide = { };
        public MatSpec[] Materials;
        // Colourways over this same geometry — see DropGarment.variants.
        public VariantSpec[] Variants = { };
        public HeelPose Heel;       // default = flat, which is almost everything
    }

    private sealed class VariantSpec
    {
        public string Name;
        // FBX surface -> the texture file this colourway puts on it. Only the
        // surfaces it actually changes: a variant may recolour the cloth and
        // leave the buckles as the prototype has them.
        public Dictionary<string, string> Textures = new Dictionary<string, string>();
        // FBX surface -> the tint this colourway paints over that texture.
        // Half the vendors ship colourways this way and change no picture at
        // all: the Autumn Jacket has eighteen, every one of them the same
        // `AUTUMN_Texture01.jpg` under a different diffuse colour.
        public Dictionary<string, string> Colors = new Dictionary<string, string>();
    }

    // Empty for the same reason as BuiltInSources: every garment is described in
    // a drop manifest. Kept as a seam rather than deleted so EnsureLoaded still
    // reads as "built-ins plus drops" if something ever has to be hardcoded again.
    private static readonly GarmentSpec[] BuiltInGarments = { };

    // --- JSON drops ---------------------------------------------------------
    //  `tools/wardrobe` writes one of these per batch of new clothing, so the
    //  pipeline never has to edit this file. Shape (see wardrobe/manifest.py):
    //
    //    { "drop": "sweetjane",
    //      "sources":  [ { "fbx": "Assets/Temp/jana sweetjane.fbx", "actor": "Jana" } ],
    //      "garments": [ { "sourceKey": "...", "folder": "...", "name": "...",
    //                      "simId": "clothing.x", "layer": "Wear",
    //                      "slots": ["Chest"], "noHide": [],
    //                      "materials": [ { "source": "tank", "texture": "a.jpg",
    //                                       "smoothness": 0.3, "alphaClip": false } ] } ] }
    //
    //  Unknown enum names and malformed files are reported and skipped rather
    //  than throwing — one bad drop must not take the whole wardrobe down.

    [System.Serializable] private sealed class DropFile
    {
        public string drop;
        public DropSource[] sources;
        public DropGarment[] garments;
    }

    [System.Serializable] private sealed class DropSource
    {
        public string fbx;
        public string actor;
    }

    [System.Serializable] private sealed class DropGarment
    {
        public string sourceKey, folder, name, simId, layer;
        // An accessory can arrive as an ASSEMBLY rather than one mesh: the
        // Jaguar Headdress exports as 29 (a rigid helmet plus 28 feathers and
        // cords). Listing them here merges them into the one skinned mesh the
        // wardrobe contract allows — see MergeParts.
        public string[] sourceKeys;
        // Material names whose geometry is NOT wanted. A kit often carries
        // parts you would rather not ship — the jaguar headdress is a helmet
        // plus a fan of feathers, and the feathers are the half that reads
        // badly in game. Applied to the built meshes by Strip Dropped
        // Materials, so it needs no re-export.
        public string[] dropMaterials;
        public string[] slots, noHide;
        public DropMaterial[] materials;
        // Colourways of this same geometry (spec §31B.4E). Harvested from the
        // product's own material presets without dressing anything twice —
        // Complete Anarchy alone carries 33 across 14 garments, and a variant
        // per DAZ preset would otherwise have meant 1 404 fittings.
        public DropVariant[] variants;
        public DropHeel heelPose;   // spec §31B.4C — heeled shoes only
    }

    [System.Serializable] private sealed class DropVariant
    {
        public string name;
        // An array, not a map: JsonUtility cannot deserialise a dictionary.
        public DropVariantTexture[] textures;
        public DropVariantColor[] colors;
    }

    [System.Serializable] private sealed class DropVariantTexture
    {
        public string source, texture;
    }

    [System.Serializable] private sealed class DropVariantColor
    {
        public string source, color;
    }

    // Straight out of the DAZ foot-pose preset that ships with a heeled shoe.
    // Absent on every other garment, and absent means "flat".
    [System.Serializable] private sealed class DropHeel
    {
        public float foot, toe, lift;
        public float[] axis;        // empty = the bone's X, which is DAZ's axis
    }

    [System.Serializable] private sealed class DropMaterial
    {
        public string source, texture, color;
        public string[] alsoSources;    // more FBX surfaces sharing this material
        public float smoothness = 0.3f, metallic;
        public bool doubleSided = true, alphaClip;
    }

    private static void EnsureLoaded()
    {
        if (_garments != null)
        {
            return;
        }

        var sources = new List<(string, ActorName, string)>(BuiltInSources);
        var garments = new List<GarmentSpec>(BuiltInGarments);

        if (Directory.Exists(DropRoot))
        {
            foreach (var file in Directory.GetFiles(DropRoot, "*.json").OrderBy(f => f))
            {
                try
                {
                    LoadDrop(file, sources, garments);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[NewWear] поставка {Path.GetFileName(file)} не прочиталась: {e.Message}");
                }
            }
        }

        _sources = sources.ToArray();
        _garments = garments.ToArray();
    }

    private static void LoadDrop(string file, List<(string, ActorName, string)> sources, List<GarmentSpec> garments)
    {
        var parsed = JsonUtility.FromJson<DropFile>(File.ReadAllText(file));
        if (parsed == null)
        {
            throw new IOException("не разобрался JSON");
        }

        var drop = !string.IsNullOrEmpty(parsed.drop)
            ? parsed.drop
            : Path.GetFileNameWithoutExtension(file);

        foreach (var s in parsed.sources ?? new DropSource[0])
        {
            if (!System.Enum.TryParse<ActorName>(s.actor, out var actor))
            {
                Debug.LogError($"[NewWear] {Path.GetFileName(file)}: неизвестная девушка '{s.actor}'");
                continue;
            }

            // A re-run of the same drop must not index the same FBX twice.
            if (!sources.Any(existing => existing.Item1 == s.fbx))
            {
                sources.Add((s.fbx, actor, drop));
            }
        }

        foreach (var g in parsed.garments ?? new DropGarment[0])
        {
            if (garments.Any(existing => existing.SimId == g.simId))
            {
                Debug.LogWarning($"[NewWear] {Path.GetFileName(file)}: {g.simId} уже описан — пропущен");
                continue;
            }

            garments.Add(new GarmentSpec
            {
                Drop = drop,
                SourceKey = g.sourceKey,
                SourceKeys = g.sourceKeys != null && g.sourceKeys.Length > 0 ? g.sourceKeys : null,
                DropMaterials = g.dropMaterials ?? new string[0],
                Folder = g.folder,
                Name = g.name,
                SimId = g.simId,
                Layer = ParseEnum(g.layer, VisualWearLayer.Wear, file),
                Slots = ParseSlots(g.slots, file),
                NoHide = ParseSlots(g.noHide, file),
                Materials = (g.materials ?? new DropMaterial[0]).Select(m => new MatSpec
                {
                    Source = m.source,
                    Aliases = m.alsoSources ?? new string[0],
                    Texture = string.IsNullOrEmpty(m.texture) ? null : m.texture,
                    Color = ParseColor(m.color),
                    Smoothness = m.smoothness,
                    Metallic = m.metallic,
                    DoubleSided = m.doubleSided,
                    AlphaClip = m.alphaClip,
                }).ToArray(),
                Variants = (g.variants ?? new DropVariant[0]).Select(v => new VariantSpec
                {
                    Name = v.name,
                    Textures = (v.textures ?? new DropVariantTexture[0])
                        .Where(t => !string.IsNullOrEmpty(t.source))
                        .GroupBy(t => t.source)
                        .ToDictionary(grp => grp.Key, grp => grp.First().texture),
                    Colors = (v.colors ?? new DropVariantColor[0])
                        .Where(c => !string.IsNullOrEmpty(c.source) && !string.IsNullOrEmpty(c.color))
                        .GroupBy(c => c.source)
                        .ToDictionary(grp => grp.Key, grp => grp.First().color),
                }).Where(v => !string.IsNullOrEmpty(v.Name)
                              && (v.Textures.Count > 0 || v.Colors.Count > 0)).ToArray(),
                Heel = ParseHeel(g.heelPose),
            });
        }
    }

    private static HeelPose ParseHeel(DropHeel heel)
    {
        if (heel == null)
        {
            return default;
        }

        return new HeelPose
        {
            footDegrees = heel.foot,
            toeDegrees = heel.toe,
            lift = heel.lift,
            axis = heel.axis != null && heel.axis.Length == 3
                ? new Vector3(heel.axis[0], heel.axis[1], heel.axis[2])
                : Vector3.zero,
        };
    }

    private static T ParseEnum<T>(string value, T fallback, string file) where T : struct
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (System.Enum.TryParse<T>(value, out var parsed))
        {
            return parsed;
        }

        Debug.LogError($"[NewWear] {Path.GetFileName(file)}: не знаю {typeof(T).Name} '{value}', беру {fallback}");
        return fallback;
    }

    private static VisualWearSlot[] ParseSlots(string[] names, string file)
    {
        if (names == null)
        {
            return new VisualWearSlot[0];
        }

        var slots = new List<VisualWearSlot>();
        foreach (var name in names)
        {
            if (System.Enum.TryParse<VisualWearSlot>(name, out var slot))
            {
                slots.Add(slot);
            }
            else
            {
                Debug.LogError($"[NewWear] {Path.GetFileName(file)}: нет такого слота '{name}'");
            }
        }

        return slots.ToArray();
    }

    // "RRGGBB" / "#RRGGBB"; empty means plain white.
    private static Color ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return Color.white;
        }

        return ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out var c)
            ? c
            : Color.white;
    }

    // One-shot auto-run: fires after every compile, and does nothing unless
    // some drop has an export sitting there with a garment still to build.
    // Scoped like everything else — a finished drop whose exports have been
    // cleaned up must not stop a new one from being noticed.
    [InitializeOnLoadMethod]
    private static void AutoRun()
    {
        EditorApplication.delayCall += () =>
        {
            if (Application.productName != "HexLive") return;
            var only = ActiveDrop();
            var pending = Garments.Where(g => (only == null || g.Drop == only) &&
                                              !File.Exists(PrefabPath(g)))
                .Select(g => g.Drop)
                .ToHashSet();
            if (pending.Count == 0) return;
            // Only worth starting if the exports those garments come from are
            // actually here.
            if (!Sources.Any(s => pending.Contains(s.drop) && File.Exists(s.path))) return;
            Run(force: false);
        };
    }

    /// <summary>
    /// Keep a finished drop's FBX exports instead of throwing them away.
    /// </summary>
    /// <remarks>
    /// Discarding is right for a drop that is DONE (see <see cref="DiscardExports"/>),
    /// and wrong while its extraction is still being fixed: the exports are the
    /// only copy of what DAZ produced, so a re-extract after a bug fix costs a
    /// full re-dress in DAZ — twenty minutes for a one-line change. Batch 1 paid
    /// that once. While a drop is under work this stays ON; it goes back OFF when
    /// the pipeline is trusted again.
    /// </remarks>
    private const string KeepExportsPref = "HexLive.Wear.KeepDropExports";

    private static bool KeepExports => EditorPrefs.GetBool(KeepExportsPref, true);

    [MenuItem("HexLive/Wear/Хранить экспорты поставки")]
    private static void ToggleKeepExports() =>
        EditorPrefs.SetBool(KeepExportsPref, !KeepExports);

    [MenuItem("HexLive/Wear/Хранить экспорты поставки", validate = true)]
    private static bool ToggleKeepExportsValidate()
    {
        Menu.SetChecked("HexLive/Wear/Хранить экспорты поставки", KeepExports);
        return true;
    }

    [MenuItem("HexLive/Wear/Extract New Wear (Temp FBX)")]
    private static void RunMenu() => Run(force: false);

    [MenuItem("HexLive/Wear/Extract New Wear (Force Re-Extract)")]
    private static void RunForceMenu() => Run(force: true);

    /// <summary>
    /// Cut the geometry of unwanted materials out of already-built meshes.
    /// </summary>
    /// <remarks>
    /// A kit does not always ship a whole you want. The jaguar headdress is a
    /// helmet plus a fan of feathers, and the feathers read badly in game — but
    /// the pieces already welded into one mesh, and the drop's FBX exports are
    /// gone (DiscardExports), so re-extracting without them is not an option.
    ///
    /// It does not need to be. The weld groups geometry into one SUBMESH per
    /// material, so a part is already separable: keep the submeshes whose
    /// material is wanted, drop the rest, and compact the vertices no surviving
    /// triangle uses. The prefab's material array is trimmed to match.
    ///
    /// Idempotent: a mesh that no longer has the dropped material is left alone.
    /// </remarks>
    [MenuItem("HexLive/Wear/Strip Dropped Materials")]
    private static void StripDroppedMaterialsMenu()
    {
        _sources = null;
        _garments = null;

        var only = ActiveDrop();
        var log = new System.Text.StringBuilder();
        foreach (var g in Garments.Where(g => only == null || g.Drop == only))
        {
            if (g.DropMaterials == null || g.DropMaterials.Length == 0)
            {
                continue;
            }

            var unwanted = new HashSet<string>(g.DropMaterials);
            // The renderer's material order IS the submesh order, so the prefab
            // is what says which submesh belongs to which material.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(g));
            var smr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (smr == null)
            {
                Debug.LogWarning($"[NewWear] {g.Name}: префаба нет, нечего чистить");
                continue;
            }

            var keep = new List<int>();
            for (var i = 0; i < smr.sharedMaterials.Length; i++)
            {
                var m = smr.sharedMaterials[i];
                var name = m != null ? m.name.Replace(" (Instance)", "") : "";
                if (!unwanted.Contains(name))
                {
                    keep.Add(i);
                }
            }

            if (keep.Count == smr.sharedMaterials.Length)
            {
                log.AppendLine($"  {g.Name}: уже почищен");
                continue;
            }

            if (keep.Count == 0)
            {
                Debug.LogError($"[NewWear] {g.Name}: убрать просят ВСЁ — отказ");
                continue;
            }

            foreach (var actor in System.Enum.GetValues(typeof(ActorName)).Cast<ActorName>())
            {
                var path = $"{ImportRoot}/{g.Folder}/Meshes/{actor}.mesh";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null)
                {
                    continue;
                }

                var before = mesh.vertexCount;
                var trimmed = KeepSubMeshes(mesh, keep);
                EditorUtility.CopySerialized(trimmed, mesh);
                Object.DestroyImmediate(trimmed);
                EditorUtility.SetDirty(mesh);
                log.AppendLine($"  {g.Name}/{actor}: {before} -> {mesh.vertexCount} вершин");
            }

            var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var live = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            live.sharedMaterials = keep.Select(i => smr.sharedMaterials[i]).ToArray();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(g));
            Object.DestroyImmediate(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        var report = "[NewWear] вырезано лишнее:\n" + log;
        Debug.Log(report);
        File.WriteAllText("Temp/newwear-strip.txt", report);
    }

    /// <summary>A copy of `mesh` carrying only the listed submeshes.</summary>
    private static Mesh KeepSubMeshes(Mesh mesh, List<int> keep)
    {
        var used = new SortedSet<int>();
        var kept = keep.Select(mesh.GetTriangles).ToList();
        foreach (var index in kept.SelectMany(t => t))
        {
            used.Add(index);
        }

        var remap = new Dictionary<int, int>(used.Count);
        foreach (var old in used)
        {
            remap[old] = remap.Count;
        }

        var src = mesh.vertices;
        var normals = mesh.normals;
        var tangents = mesh.tangents;
        var uv = mesh.uv;
        var order = used.ToArray();

        var copy = new Mesh
        {
            name = mesh.name,
            indexFormat = order.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        copy.SetVertices(order.Select(i => src[i]).ToList());
        if (normals.Length == src.Length)
        {
            copy.SetNormals(order.Select(i => normals[i]).ToList());
        }

        if (tangents.Length == src.Length)
        {
            copy.SetTangents(order.Select(i => tangents[i]).ToList());
        }

        if (uv.Length == src.Length)
        {
            copy.SetUVs(0, order.Select(i => uv[i]).ToList());
        }

        copy.bindposes = mesh.bindposes;
        SetSingleBoneSkin(copy, order.Length);
        copy.subMeshCount = kept.Count;
        for (var i = 0; i < kept.Count; i++)
        {
            copy.SetTriangles(kept[i].Select(t => remap[t]).ToArray(), i);
        }

        copy.RecalculateBounds();
        return copy;
    }

    /// <summary>
    /// Rebuild the drops' MATERIALS in place, without touching meshes.
    /// </summary>
    /// <remarks>
    /// Materials need no FBX — only the manifest and the textures on disk — and
    /// that matters because a finished drop's exports are thrown away
    /// (DiscardExports), which makes a normal re-extract impossible afterwards.
    /// A material is exactly the thing you fix later: `build` stages textures
    /// under a folder named from the DAZ mesh key, so renaming a garment during
    /// review strands them, and every material comes out with no albedo — a
    /// white garment in the world, not just on the icon.
    ///
    /// The prefabs reference these `.mat` assets by GUID, so refreshing them in
    /// place is enough; nothing has to be re-extracted.
    /// </remarks>
    [MenuItem("HexLive/Wear/Rebuild Materials From Drops")]
    private static void RebuildMaterialsMenu()
    {
        _sources = null;
        _garments = null;

        var only = ActiveDrop();
        var done = 0;
        var blank = new List<string>();
        foreach (var g in Garments.Where(g => only == null || g.Drop == only))
        {
            EnsureFolder($"{ImportRoot}/{g.Folder}/Materials");
            foreach (var spec in g.Materials)
            {
                var mat = BuildMaterial(g, spec);
                done++;
                if (spec.Texture != null && mat.GetTexture("_BaseMap") == null)
                {
                    blank.Add($"{g.Folder}/{spec.Source} ← {spec.Texture}");
                }
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        var report = $"[NewWear] материалов пересобрано: {done}" +
                     (blank.Count > 0
                         ? $"\n  БЕЗ ТЕКСТУРЫ {blank.Count} (файла нет в <вещь>/Textures):\n    " +
                           string.Join("\n    ", blank.Take(20))
                         : "");
        Debug.Log(report);
        File.WriteAllText("Temp/newwear-materials.txt", report);
    }

    /// <summary>
    /// Throw away the exports of every drop that is already built.
    /// </summary>
    /// <remarks>
    /// A one-off broom for what accumulated before a drop became the unit of
    /// work: measured, `Assets/Temp` held 38 FBX exports, and every extraction
    /// instantiated all of them — eight-plus full Genesis rigs at once, which
    /// is enough to take the editor down. Manual on purpose: deleting that many
    /// files is a decision, and anything not described by a manifest is left
    /// alone and merely listed.
    /// </remarks>
    [MenuItem("HexLive/Wear/Discard Finished Drop Exports")]
    private static void DiscardFinishedMenu()
    {
        _sources = null;
        _garments = null;

        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var drop in Sources.Select(s => s.drop).Distinct())
        {
            var theirs = Garments.Where(g => g.Drop == drop).ToArray();
            var unfinished = theirs.Count(g => !File.Exists(PrefabPath(g)));
            if (theirs.Length == 0 || unfinished > 0)
            {
                kept.Add($"{drop} (не собрано {unfinished} из {theirs.Length})");
                continue;
            }

            foreach (var (path, _, _) in Sources.Where(s => s.drop == drop))
            {
                if (File.Exists(path) && AssetDatabase.DeleteAsset(path))
                {
                    removed.Add(Path.GetFileName(path));
                }
            }
        }

        // Exports no manifest mentions — the pre-manifest era. Not ours to
        // delete, but the operator should know they are sitting there.
        var described = new HashSet<string>(Sources.Select(s => s.path.Replace('\\', '/')));
        var strays = Directory.Exists("Assets/Temp")
            ? Directory.GetFiles("Assets/Temp", "*.fbx")
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !described.Contains(p))
                .Select(Path.GetFileName)
                .ToArray()
            : new string[0];

        AssetDatabase.Refresh();
        Debug.Log($"[NewWear] убрано экспортов: {removed.Count}" +
                  (removed.Count > 0 ? "\n  " + string.Join(", ", removed) : "") +
                  (kept.Count > 0 ? "\n  оставлены незаконченные: " + string.Join("; ", kept) : "") +
                  (strays.Length > 0
                      ? $"\n  ничьих (ни в одном манифесте), решайте сами — {strays.Length}: " +
                        string.Join(", ", strays)
                      : ""));
    }

    private static string PrefabPath(GarmentSpec g) => $"{WearRoot}/{g.SimId}/{g.Name}.prefab";

    private static void Run(bool force)
    {
        // Editing a drop JSON re-imports the asset but does not reload the
        // domain, so the cached tables would be stale on a manual re-run.
        _sources = null;
        _garments = null;

        var only = ActiveDrop();
        var sources = Sources.Where(s => only == null || s.drop == only).ToArray();
        var garments = Garments.Where(g => only == null || g.Drop == only).ToArray();
        if (only != null)
        {
            if (garments.Length == 0)
            {
                Debug.LogError($"[NewWear] поставка '{only}' не найдена среди манифестов в {DropRoot}");
                return;
            }

            Debug.Log($"[NewWear] работаю только с поставкой '{only}': " +
                      $"{garments.Length} вещ(и), {sources.Length} экспорт(ов)");
        }
        else
        {
            Debug.LogWarning("[NewWear] поставка не указана — беру ВСЕ манифесты. " +
                             $"Обычно это не то, что нужно: одна новая вещь пересоберёт все " +
                             $"{Garments.Length}. Запись имени в {ActiveDropFile} ограничивает прогон.");
        }

        // A drop whose exports have been cleaned up is DONE, not broken — the
        // FBX is a temporary, and its meshes already live in the project. It is
        // only an error when that drop is the one we were asked to build.
        var missingFbx = sources.Where(s => !File.Exists(s.path)).Select(s => s.path).ToList();
        if (missingFbx.Count > 0)
        {
            if (only != null)
            {
                Debug.LogError("[NewWear] нет экспортов поставки: " + string.Join(", ", missingFbx));
                return;
            }

            Debug.Log($"[NewWear] пропускаю {missingFbx.Count} убранных экспорт(ов) — " +
                      "их вещи уже собраны");
            var gone = new HashSet<string>(sources.Where(s => !File.Exists(s.path)).Select(s => s.drop));
            sources = sources.Where(s => File.Exists(s.path)).ToArray();
            garments = garments.Where(g => !gone.Contains(g.Drop)).ToArray();
        }

        // Instantiate the FBX rigs once; index every garment renderer.
        var instances = new List<GameObject>();
        // One girl now has several rigs — one FBX per drop — so this is a list.
        // Keyed by girl alone, the second drop simply overwrote the first and a
        // welded garment went looking for its helmet in the Sweet Jane export.
        var rigs = new Dictionary<ActorName, List<GameObject>>();
        var renderers = new Dictionary<(ActorName actor, string key), SkinnedMeshRenderer>();
        try
        {
            foreach (var (path, actor, _) in sources)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    Debug.LogError($"[NewWear] can't load {path}");
                    return;
                }

                var instance = Object.Instantiate(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instances.Add(instance);
                if (!rigs.TryGetValue(actor, out var forActor))
                {
                    rigs[actor] = forActor = new List<GameObject>();
                }

                forActor.Add(instance);
                foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var key = garments.FirstOrDefault(g =>
                        r.sharedMesh != null && r.sharedMesh.name.StartsWith(g.SourceKey) ||
                        r.name.StartsWith(g.SourceKey))?.SourceKey;
                    if (key != null)
                    {
                        renderers[(actor, key)] = r;
                    }
                }
            }

            var done = 0;
            var skipped = 0;
            var failed = 0;
            var log = new System.Text.StringBuilder();
            foreach (var g in garments)
            {
                if (!force && File.Exists(PrefabPath(g)))
                {
                    skipped++;
                    continue;
                }

                // Каждая вещь сама по себе. Одна необычная — колье Amy без
                // собственной кости `hip` — валила исключением ВЕСЬ заход, и
                // семнадцать здоровых вещей не собирались из-за одной. Ошибка
                // должна стоить одну строку в отчёте, а не прогон.
                try
                {
                    if (ExtractGarment(g, renderers, rigs, log))
                    {
                        done++;
                    }
                }
                catch (System.Exception e)
                {
                    failed++;
                    log.AppendLine($"  {g.SourceKey}: СБОЙ — {e.Message}");
                    Debug.LogError($"[NewWear] {g.SourceKey}: {e}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var report = $"[NewWear] extracted {done}, skipped (already built) {skipped}"
                + (failed > 0 ? $", СБОЙ у {failed}" : string.Empty) + $"\n{log}";
            Debug.Log(report);
            // Also to a file: the MCP bridge's console read is the first thing
            // to time out on a busy editor, and this run's own verdict is
            // exactly what an agent needs when it cannot read the console.
            File.WriteAllText("Temp/newwear.txt", report);

            if (done > 0)
            {
                // Materialize the new GarmentLibrary rows as GarmentDefinition
                // assets + refresh the catalog, then re-export simdata.json
                // (spec §59.3 — headless probes refuse to run without it).
                EditorApplication.ExecuteMenuItem("HexLive/Garments/Rebuild Catalog From Defaults");
                EditorApplication.ExecuteMenuItem("HexLive/Export Sim Data (JSON)");
            }

            if (only != null && !KeepExports)
            {
                DiscardExports(only, sources, garments, instances);
            }
        }
        finally
        {
            foreach (var instance in instances)
            {
                Object.DestroyImmediate(instance);
            }
        }
    }

    /// <summary>
    /// Throw away a finished drop's FBX exports.
    /// </summary>
    /// <remarks>
    /// The FBX is an INTERMEDIATE, not an asset: what ships is the mesh cut out
    /// of it, which is now a `.mesh` of its own. Left behind, the exports are
    /// megabytes Unity re-imports on every launch, they show up in the project
    /// as a full outfit on a rig nobody wears, and — worst — the next run
    /// instantiates them all over again, so one new pair of knickers drags the
    /// entire wardrobe through the extractor.
    ///
    /// Only when the drop actually finished: every one of its garments has a
    /// prefab. A half-built drop keeps its exports so the run can be repeated.
    /// </remarks>
    private static void DiscardExports(
        string drop,
        (string path, ActorName actor, string drop)[] sources,
        GarmentSpec[] garments,
        List<GameObject> instances)
    {
        var unfinished = garments.Where(g => !File.Exists(PrefabPath(g))).Select(g => g.Name).ToArray();
        if (unfinished.Length > 0)
        {
            Debug.LogWarning($"[NewWear] экспорты поставки '{drop}' оставлены: " +
                             $"не собрано {unfinished.Length} вещ(и) — {string.Join(", ", unfinished)}");
            return;
        }

        // The rigs are still instantiated from these very assets; drop them
        // first or Unity deletes the file out from under a live object.
        foreach (var instance in instances)
        {
            Object.DestroyImmediate(instance);
        }

        instances.Clear();

        var removed = new List<string>();
        foreach (var (path, _, _) in sources)
        {
            if (AssetDatabase.DeleteAsset(path))
            {
                removed.Add(Path.GetFileName(path));
            }
            else if (File.Exists(path))
            {
                Debug.LogWarning($"[NewWear] не удалось убрать {path}");
            }
        }

        if (removed.Count > 0)
        {
            AssetDatabase.Refresh();
            Debug.Log($"[NewWear] поставка '{drop}' собрана — убрал промежуточные экспорты: " +
                      string.Join(", ", removed));
        }
    }

    private static bool ExtractGarment(
        GarmentSpec g,
        Dictionary<(ActorName, string), SkinnedMeshRenderer> renderers,
        Dictionary<ActorName, List<GameObject>> rigs,
        System.Text.StringBuilder log)
    {
        EnsureFolder($"{ImportRoot}/{g.Folder}/Meshes");
        EnsureFolder($"{ImportRoot}/{g.Folder}/Materials");
        EnsureFolder($"{WearRoot}/{g.SimId}");

        // --- materials (URP Lit, flat albedo per the art style) --------------
        // Built BEFORE the weld: MergeParts decides whether the merged mesh
        // needs tangents, and the only honest answer comes from OUR materials.
        var mats = new Dictionary<string, Material>();
        foreach (var spec in g.Materials)
        {
            var built = BuildMaterial(g, spec);
            mats[spec.Source] = built;
            foreach (var alias in spec.Aliases)
            {
                mats[alias] = built;
            }
        }

        if (g.SourceKeys != null)
        {
            foreach (var pair in rigs)
            {
                // Only one of a girl's rigs holds this kit; the rest belong to
                // other drops and are expected to come back empty.
                foreach (var rig in pair.Value)
                {
                    var welded = MergeParts(g, rig, mats);
                    if (welded != null)
                    {
                        renderers[(pair.Key, g.SourceKey)] = welded;
                        break;
                    }
                }
            }
        }

        if (!renderers.TryGetValue((ActorName.Jana, g.SourceKey), out var reference))
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: no renderer in the jana FBX — skipped");
            return false;
        }

        // --- meshes: one fitted copy per girl --------------------------------
        // The girls of THIS run, not every girl any drop ever covered: with the
        // run scoped to one drop, the others are simply not in the room.
        var meshes = new Dictionary<ActorName, Mesh>();
        foreach (var actor in rigs.Keys.OrderBy(a => (int)a))
        {
            if (!renderers.TryGetValue((actor, g.SourceKey), out var r) || r.sharedMesh == null)
            {
                // Expected when a girl was not part of this garment's drop —
                // she simply cannot wear it until someone re-exports her.
                Debug.LogWarning($"[NewWear] {g.SourceKey}: no mesh for {actor}");
                continue;
            }

            var path = $"{ImportRoot}/{g.Folder}/Meshes/{actor}.mesh";
            var copy = Object.Instantiate(r.sharedMesh);
            copy.name = actor.ToString();
            // The COPY is what ships, and Object.Instantiate does not carry a
            // lean vertex layout across — so the layout is applied here, to the
            // mesh that actually becomes the asset.
            var welded = g.SourceKeys != null;
            if (welded)
            {
                Slim(copy, WantsTangents(mats));
            }

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null && welded)
            {
                // CopySerialized writes INTO the object already in the asset
                // database, and that object keeps its own vertex layout — a
                // slim mesh copied into a fat one comes back out fat, which is
                // how three girls ended up at 4.97 MB and the fourth at 9.66
                // from the same run. Deleting through AssetDatabase (not just
                // unlinking the file — the database caches the object and hands
                // the stale one back) is what makes the layout reproducible.
                AssetDatabase.DeleteAsset(path);
                existing = null;
            }

            if (existing != null)
            {
                // Keep the GUID stable across re-runs — prefab refs survive.
                EditorUtility.CopySerialized(copy, existing);
                Object.DestroyImmediate(copy);
            }
            else
            {
                AssetDatabase.CreateAsset(copy, path);
            }

            // Re-resolved from the PATH, never kept from the variable above. A
            // freshly created asset is a live object only until the next import,
            // and this run triggers several (EnsureFolder, SaveAssets) before the
            // prefab is written — after which the old reference serializes as
            // null. Only the welded garments took the delete-and-create branch,
            // so only they came out with an empty mesh on the renderer: present
            // in the project, registered everywhere, invisible in game.
            var stored = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (stored == null)
            {
                Debug.LogError($"[NewWear] {g.SourceKey}: меш {actor} не сохранился в {path}");
                continue;
            }

            meshes[actor] = stored;
        }

        if (!meshes.ContainsKey(ActorName.Jana))
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: no Jana mesh — skipped");
            return false;
        }

        // --- prefab -----------------------------------------------------------
        var root = new GameObject(g.Name);
        try
        {
            BuildBonesAndRenderer(g, reference, meshes[ActorName.Jana], mats, root);
            FillWearComponent(g, meshes, root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(g));
            // Re-imported, because the FILE being right is not the same as the
            // editor holding it. A welded garment's mesh asset is deleted and
            // recreated on every run, and a prefab already loaded in this session
            // keeps pointing at the object that went away — it reads back with an
            // empty mesh and the garment simply does not draw, while every check
            // against the file on disk says it is fine.
            AssetDatabase.ImportAsset(PrefabPath(g), ImportAssetOptions.ForceUpdate);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        var variants = BuildVariants(g, reference, mats);

        log.AppendLine($"  {g.SourceKey} -> {PrefabPath(g)} ({meshes.Count} meshes, {mats.Count} mats" +
                       (variants > 0 ? $", расцветок {variants}" : "") + ")" +
                       (meshes.TryGetValue(ActorName.Jana, out var saved) ? $"\n      сохранённый: {Layout(saved)}" : ""));
        return true;
    }

    /// <summary>
    /// Build a material set and a catalog entry for every colourway.
    /// </summary>
    /// <remarks>
    /// Spec §31B.4E. The geometry is already built and shared: a variant only
    /// swaps textures, so it costs a folder of `.mat` files and one
    /// GarmentDefinition — never another mesh, prefab or fitting.
    ///
    /// The definition is written HERE rather than by the register stage because
    /// this is the only place that knows which materials were actually created.
    /// It carries `prototypeId`, so `GarmentVariants` sends it to the
    /// prototype's art folder, and `variantMaterials` in submesh order, which is
    /// what `Wear.ApplyVariant` paints with.
    ///
    /// Stats are COPIED from the prototype and not invented: spotted knickers
    /// are not warmer than starred ones, and forty guessed warmth values would
    /// be impossible to defend later.
    /// </remarks>
    private static int BuildVariants(
        GarmentSpec g, SkinnedMeshRenderer reference, Dictionary<string, Material> mats)
    {
        if (g.Variants == null || g.Variants.Length == 0)
        {
            return 0;
        }

        var protoPath = DefinitionPath(g.SimId);
        var prototype = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(protoPath);
        var built = 0;
        foreach (var variant in g.Variants)
        {
            var folder = $"{ImportRoot}/{g.Folder}/Materials/{Sanitize(variant.Name)}";
            EnsureFolder(folder);

            // One material per submesh, in the renderer's order — the same order
            // Wear.ApplyVariant walks.
            var painted = new List<Material>();
            foreach (var slot in reference.sharedMaterials)
            {
                var surface = slot != null ? slot.name.Replace(" (Instance)", "") : "";
                var spec = SpecFor(g, surface);
                var path = $"{folder}/{Sanitize(spec.Source)}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, path);
                }

                // Everything but the picture and its tint comes from the
                // prototype's spec: a colourway changes how the cloth LOOKS,
                // not how it behaves.
                mat.SetColor("_BaseColor", variant.Colors.TryGetValue(surface, out var tint)
                    ? ParseColor(tint)
                    : spec.Color);
                mat.SetFloat("_Smoothness", spec.Smoothness);
                mat.SetFloat("_Metallic", spec.Metallic);
                if (spec.DoubleSided)
                {
                    mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                }

                if (spec.AlphaClip)
                {
                    mat.SetFloat("_AlphaClip", 1f);
                    mat.SetFloat("_Cutoff", 0.3f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                }

                // A surface this colourway does not mention keeps the
                // prototype's texture — that is what "recolour the cloth, keep
                // the buckles" looks like in data.
                var file = variant.Textures.TryGetValue(surface, out var named)
                    ? named
                    : spec.Texture;
                var tex = file != null ? FindTexture(g, file) : null;
                mat.SetTexture("_BaseMap", tex);
                EditorUtility.SetDirty(mat);
                painted.Add(mat);
            }

            // An item id travels into simdata.json, I2 terms and save files, so
            // it may not carry whatever DAZ called the colourway. `Sanitize` only
            // strips characters a FILENAME rejects, and a space is not one of
            // them — "Bra Purple" came out as `clothing.top_anarchy_bra purple`,
            // an id that reads as two words everywhere it is ever printed.
            var id = $"{g.SimId}_{VariantSlug(variant.Name)}";
            var defPath = DefinitionPath(id, prototype != null ? protoPath : null, g.Layer.ToString());
            var def = AssetDatabase.LoadAssetAtPath<GarmentDefinition>(defPath);
            if (def == null)
            {
                def = ScriptableObject.CreateInstance<GarmentDefinition>();
                AssetDatabase.CreateAsset(def, defPath);
            }

            def.id = id;
            def.prototypeId = g.SimId;
            def.variantMaterials = painted.ToArray();
            if (prototype != null)
            {
                def.displayName = $"{prototype.displayName} ({variant.Name})";
                def.layer = prototype.layer;
                // Fully qualified: UnityEditor has a BodyPart of its own, and
                // `using UnityEditor` makes the bare name ambiguous.
                def.covers = new List<HexLive.Simulation.Content.BodyPart>(prototype.covers);
                def.warmth = prototype.warmth;
                def.armor = prototype.armor;
                def.thermalDelta = prototype.thermalDelta;
                def.dressDurationTicks = prototype.dressDurationTicks;
                def.capacity = prototype.capacity;
            }

            EditorUtility.SetDirty(def);
            // Written NOW, not at the end of the run. `EnsureFolder` on the next
            // colourway asks the AssetDatabase to import, and an import reloads
            // any dirty-but-unsaved asset from disk — so every variant but the
            // last was quietly reverted to a blank `GarmentDefinition`: no id, no
            // prototype, no materials. The assets existed, which is why it read
            // as a naming problem rather than a lost write.
            AssetDatabase.SaveAssets();
            built++;
        }

        return built;
    }

    // Catalog assets are filed by layer; a variant lands beside its prototype.
    private static string DefinitionPath(string id, string beside = null, string layer = null)
    {
        // Concat, NOT Join: `string.Join("_", chars)` puts a separator between
        // EVERY character, which is how the first variants landed on disk as
        // `c_l_o_t_h_i_n_g___b_e_l_t___a_n_a_r_c_h_y`.
        var slug = new string(id.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        foreach (var drawer in System.Enum.GetNames(typeof(VisualWearLayer)))
        {
            var path = $"Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/{drawer}/{slug}.asset";
            if (File.Exists(path))
            {
                return path;
            }
        }

        // The folders ARE the layers, so a recoloured belt filed under `Wear`
        // sits in a different drawer from the belt it recolours. Beside the
        // prototype when it already exists; otherwise by the garment's own
        // layer — on a FIRST run the prototype asset does not exist yet (the
        // catalog builder makes it afterwards), and six variants of the second
        // drop landed in `Wear` while the builder created empty twins for them
        // in `Outerwear`. Two assets, one id, and the empty one won.
        var folder = beside != null
            ? Path.GetDirectoryName(beside)?.Replace('\\', '/')
            : null;
        folder ??= layer != null
            ? $"Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/{layer}"
            : null;
        folder ??= "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets/Wear";
        EnsureFolder(folder);
        return $"{folder}/{slug}.asset";
    }

    // --- welding an assembly into one garment --------------------------------
    //
    //  Some DAZ products are not a garment but a kit. The Jaguar Headdress
    //  exports as twenty-nine renderers: a rigid helmet parented straight to
    //  the head bone, plus twenty-eight feathers and cords, each skinned to ONE
    //  private bone. The wardrobe takes exactly one SkinnedMeshRenderer and one
    //  Mesh per garment, so a kit cannot be worn at all until it is welded.
    //
    //  Those private bones are dead weight HERE: Wear.Construct binds a
    //  garment's skeleton to the body BY NAME, and no girl has a bone called
    //  "Bone". Nothing can ever drive them, so the feathers cannot sway
    //  whatever we do — they are rigid bodies hanging off the head. That makes
    //  this merge exact rather than a compromise: bake every part into the one
    //  bone they all really follow and the result matches DAZ, at one bone and
    //  one draw call per material.
    //
    //  The maths is just the skinning identity. A skinned vertex lands at
    //      world = bone.localToWorld * bindpose * v
    //  so a part's authored-space matrix is bone.localToWorld * bindpose⁻¹,
    //  read off the instance while it still stands in its bind pose. Every part
    //  is pushed through that into the rig's own space, and one bindpose
    //  (anchor.worldToLocal * rig.localToWorld) sends the whole thing back.
    /// <summary>Тот ли это узел, что назван ключом сварки.</summary>
    /// <remarks>
    /// Сравнение по началу имени нужно, потому что DAZ дописывает к узлу число
    /// вершин: ключ `TA_StockingL` должен найти `TA_StockingL_882`. Но голое
    /// «начинается с» захватывает и однофамильцев: `glove_l_zipper_slider` — это
    /// бегунок ПЕРЧАТКИ, а `glove_l_zipper_slider_dup_3` уже бегунок БОТИНКА, и
    /// перчатка утащила бы его в себя, оставив ботинок без детали.
    ///
    /// Поэтому после ключа допускается только суффикс DAZ: подчёркивание и
    /// цифры. Всё остальное — другая вещь.
    /// </remarks>
    private static bool KeyMatches(string key, string node)
    {
        // Unity дописывает мешу «.Shape», и узел приезжает как
        // `ChRO_gloveR_33641.Shape`. Без этого среза правило «после ключа
        // только цифры» отвергало ВСЕ правые половины пар, и сваренная вещь
        // выходила однобокой: один сапог, одна перчатка.
        var dot = node.LastIndexOf(".Shape", System.StringComparison.Ordinal);
        if (dot > 0)
        {
            node = node.Substring(0, dot);
        }

        if (node == key)
        {
            return true;
        }

        if (!node.StartsWith(key, System.StringComparison.Ordinal))
        {
            return false;
        }

        var tail = node.Substring(key.Length);
        return tail.Length > 1 && tail[0] == '_' && tail.Skip(1).All(char.IsDigit);
    }

    private static SkinnedMeshRenderer MergeParts(
        GarmentSpec g, GameObject rig, Dictionary<string, Material> mats)
    {
        if (g.Materials == null || g.Materials.Length == 0)
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: сборный предмет без материалов");
            return null;
        }

        // The body is simply the rig's richest skeleton — 172 bones against the
        // one bone every prop part carries.
        var body = rig.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .OrderByDescending(r => r.bones.Length)
            .FirstOrDefault();
        var bodyBones = new HashSet<Transform>(
            body != null ? body.bones.Where(b => b != null) : Enumerable.Empty<Transform>());
        var byName = new Dictionary<string, Transform>();
        foreach (var bone in bodyBones)
        {
            byName[bone.name] = bone;
        }

        if (bodyBones.Count == 0)
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: в FBX нет скелета тела");
            return null;
        }

        var parts = new List<(Renderer renderer, Mesh mesh, Matrix4x4 toRig, Transform anchor)>();
        Transform shared = null;
        foreach (var r in rig.GetComponentsInChildren<Renderer>(true))
        {
            if (!g.SourceKeys.Any(k => KeyMatches(k, r.name)))
            {
                continue;
            }

            Mesh mesh;
            Matrix4x4 authored;
            Transform from;
            if (r is SkinnedMeshRenderer skinned)
            {
                mesh = skinned.sharedMesh;
                var bind = mesh != null ? mesh.bindposes : null;
                var i = System.Array.FindIndex(skinned.bones, b => b != null);
                if (mesh == null || bind == null || i < 0 || i >= bind.Length)
                {
                    Debug.LogWarning($"[NewWear] {g.SourceKey}: {r.name} без пригодного скиннинга — пропущен");
                    continue;
                }

                // Unity's own identity, and it takes the bind pose STRAIGHT, not
                // inverted: `bindposes[i] = bones[i].worldToLocal * renderer.localToWorld`,
                // so `bones[i].localToWorld * bindposes[i]` is exactly the mesh's
                // model-to-world matrix. Inverting it applied the bone's offset a
                // second time instead of cancelling it — the stockings came out
                // as a flat slab at y = 1.70 (twice thigh height) and the cuffs
                // four metres wide at y = 4.36. Rigid props survived it only
                // because their single bind pose was near identity.
                authored = skinned.bones[i].localToWorldMatrix * bind[i];
                from = skinned.bones[i];
            }
            else
            {
                var filter = r.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                authored = r.transform.localToWorldMatrix;
                from = r.transform;
            }

            var anchor = NearestBone(from, bodyBones, byName);
            if (anchor == null)
            {
                Debug.LogWarning($"[NewWear] {g.SourceKey}: {r.name} ни к чему не привязан — пропущен");
                continue;
            }

            shared = shared == null ? anchor : CommonAncestor(shared, anchor);
            parts.Add((r, mesh, rig.transform.worldToLocalMatrix * authored, anchor));
        }

        if (parts.Count == 0 || shared == null)
        {
            // Not an error: the caller offers every rig the girl has, and only
            // one of them was exported with this kit on her.
            return null;
        }

        // --- pour every part into one buffer ---------------------------------
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var order = new List<MatSpec>();
        var triangles = new Dictionary<MatSpec, List<int>>();
        var samples = new Dictionary<MatSpec, Material>();

        // Tangents are only ever read by a normal-mapped shader, and cost 16
        // bytes a vertex — a fifth of this mesh. Ours are flat URP/Lit with an
        // albedo and nothing else, so they are dead weight; asked of the built
        // materials rather than assumed, so a normal map added later brings
        // them back on its own.
        var wantsTangents = WantsTangents(mats);

        // Collapsing everything onto ONE bone is exact for a kit whose parts
        // hang off private bones the body can never drive — the jaguar
        // headdress, where nothing could animate them whatever we did. It is
        // destructive for anything genuinely skinned to the BODY: a pair of
        // stockings spans thigh, shin and foot, and baking it to one bone makes
        // a rigid tube that lands wherever that bone's bind pose points.
        // Measured — the stockings and the cuffs were the only two garments of
        // the drop to go through the weld, and both flew off into the sky.
        //
        // So the question is whether the parts are skinned to the body at all.
        //
        // Asked BY NAME, and it has to be: DAZ gives every fitted figure its own
        // copy of the skeleton, so one export of this drop carries 19 transforms
        // called `pelvis` and 12 called `lThighBend`. The stockings reference
        // their own copy, never the body's, and a reference-identity test is
        // therefore false for EVERY garment ever exported — which is exactly how
        // the pair got baked onto one bone and flew off. `Wear.Construct` stitches
        // garment bones to the body by name for the same reason.
        //
        // More than one shared bone, because a rigid prop pinned to a single bone
        // (the jaguar headdress on `head`) is still right to collapse: it is the
        // SPAN across bones that a single-bone bake destroys.
        var skinnedToBody = parts.Any(p => p.renderer is SkinnedMeshRenderer s &&
                                           s.bones.Where(b => b != null)
                                               .Select(b => b.name)
                                               .Where(byName.ContainsKey)
                                               .Distinct()
                                               .Skip(1)
                                               .Any());
        var boneList = new List<Transform>();
        var boneIndex = new Dictionary<Transform, int>();
        var weights = new List<BoneWeight>();

        foreach (var (renderer, mesh, toRig, _) in parts)
        {
            var offset = vertices.Count;
            var src = mesh.vertices;
            var srcNormals = mesh.normals;
            var srcTangents = wantsTangents ? mesh.tangents : System.Array.Empty<Vector4>();
            var srcUv = mesh.uv;
            for (var i = 0; i < src.Length; i++)
            {
                vertices.Add(toRig.MultiplyPoint3x4(src[i]));
                normals.Add(i < srcNormals.Length
                    ? toRig.MultiplyVector(srcNormals[i]).normalized
                    : Vector3.up);
                if (wantsTangents)
                {
                    if (i < srcTangents.Length)
                    {
                        var t = srcTangents[i];
                        var dir = toRig.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                        tangents.Add(new Vector4(dir.x, dir.y, dir.z, t.w));
                    }
                    else
                    {
                        tangents.Add(new Vector4(1f, 0f, 0f, -1f));
                    }
                }

                uvs.Add(i < srcUv.Length ? srcUv[i] : Vector2.zero);
            }

            // Carry the real skinning across for a garment the body drives:
            // the same bones, re-indexed into the merged mesh's own bone list.
            if (skinnedToBody)
            {
                var skinned = renderer as SkinnedMeshRenderer;
                var partBones = skinned != null ? skinned.bones : System.Array.Empty<Transform>();
                var map = new int[partBones.Length];
                for (var b = 0; b < partBones.Length; b++)
                {
                    var bone = partBones[b];
                    if (bone == null)
                    {
                        map[b] = 0;
                        continue;
                    }

                    // Onto the BODY's skeleton, by name. The halves of a pair are
                    // two fitted figures, so each carries its own copy of the
                    // skeleton — and DAZ exports only the bones that copy actually
                    // weights. Keeping both copies gives a bone list no single
                    // subtree contains: the left stocking's `hip` has no `rShin`,
                    // and cloning it later fails on exactly those two bones. The
                    // body's skeleton is the one that has all of them, its bind
                    // pose is the pose the garment was fitted in, and binding by
                    // name is what `Wear.Construct` does at runtime anyway.
                    if (byName.TryGetValue(bone.name, out var canonical))
                    {
                        bone = canonical;
                    }

                    if (!boneIndex.TryGetValue(bone, out var at))
                    {
                        at = boneList.Count;
                        boneIndex[bone] = at;
                        boneList.Add(bone);
                    }

                    map[b] = at;
                }

                var srcWeights = mesh.boneWeights;
                for (var i = 0; i < src.Length; i++)
                {
                    var w = i < srcWeights.Length ? srcWeights[i] : default;
                    weights.Add(new BoneWeight
                    {
                        boneIndex0 = map.Length > w.boneIndex0 ? map[w.boneIndex0] : 0,
                        boneIndex1 = map.Length > w.boneIndex1 ? map[w.boneIndex1] : 0,
                        boneIndex2 = map.Length > w.boneIndex2 ? map[w.boneIndex2] : 0,
                        boneIndex3 = map.Length > w.boneIndex3 ? map[w.boneIndex3] : 0,
                        weight0 = w.weight0, weight1 = w.weight1,
                        weight2 = w.weight2, weight3 = w.weight3,
                    });
                }
            }

            var slots = renderer.sharedMaterials;
            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var slot = sub < slots.Length ? slots[sub] : null;
                var spec = SpecFor(g, slot != null ? slot.name.Replace(" (Instance)", "") : "");
                if (!triangles.TryGetValue(spec, out var bucket))
                {
                    bucket = new List<int>();
                    triangles[spec] = bucket;
                    samples[spec] = slot;
                    order.Add(spec);
                }

                foreach (var index in mesh.GetTriangles(sub))
                {
                    bucket.Add(index + offset);
                }
            }
        }

        var merged = new Mesh
        {
            name = g.SourceKey,
            // 32-bit indices double the index buffer and buy nothing under
            // 65 536 vertices. The headdress sits at 55 844.
            indexFormat = vertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        merged.SetVertices(vertices);
        merged.SetNormals(normals);
        if (wantsTangents)
        {
            merged.SetTangents(tangents);
        }

        merged.SetUVs(0, uvs);
        // The bind pose is the same identity either way — bone.worldToLocal
        // times the rig's localToWorld — just one bone or many.
        if (skinnedToBody && boneList.Count > 0)
        {
            merged.bindposes = boneList
                .Select(b => b.worldToLocalMatrix * rig.transform.localToWorldMatrix)
                .ToArray();
            merged.boneWeights = weights.ToArray();
        }
        else
        {
            merged.bindposes = new[] { shared.worldToLocalMatrix * rig.transform.localToWorldMatrix };
            SetSingleBoneSkin(merged, vertices.Count);
        }

        merged.subMeshCount = order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            merged.SetTriangles(triangles[order[i]], i);
        }

        merged.RecalculateBounds();

        var go = new GameObject(g.SourceKey + " (merged)");
        go.transform.SetParent(rig.transform, false);
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = merged;
        smr.bones = skinnedToBody && boneList.Count > 0 ? boneList.ToArray() : new[] { shared };
        smr.rootBone = shared;
        smr.localBounds = merged.bounds;
        // Real FBX materials, not placeholders: their names go through the same
        // alias table BuildMaterial was keyed by, so the mapping stays honest.
        smr.sharedMaterials = order.Select(spec => samples[spec]).ToArray();

        Debug.Log($"[NewWear] {g.SourceKey}: собрано {parts.Count} част(и) → " +
                  $"{vertices.Count} вершин, {order.Count} материал(а), кость '{shared.name}'; " +
                  Layout(merged));
        return smr;
    }

    // Tangents are only ever read by a normal-mapped shader and cost 16 bytes a
    // vertex — a fifth of a welded mesh. Asked of the built materials rather
    // than assumed, so a normal map added later brings them back on its own.
    private static bool WantsTangents(Dictionary<string, Material> mats)
    {
        return mats.Values.Any(
            m => m != null && m.HasProperty(BumpMap) && m.GetTexture(BumpMap) != null);
    }

    // What a mesh actually costs per vertex. Worth logging rather than
    // assuming: a mesh authored lean can be widened again by a later copy.
    private static string Layout(Mesh mesh)
    {
        var bones = mesh.GetBonesPerVertex();
        var influences = bones.Length > 0 ? bones[0] : (byte)0;
        var text = $"тангенсы={mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent)}, " +
                   $"влияний/вершину={influences}, индексы={mesh.indexFormat}";
        bones.Dispose();
        return text;
    }

    /// <summary>
    /// Strip a welded mesh down to what it actually needs.
    /// </summary>
    /// <remarks>
    /// Only for welded kits (spec §31B.4D), where every vertex genuinely rides
    /// one bone. An ordinary garment's weights are real and must not be touched.
    ///
    /// Measured on the headdress, per vertex: position 12 + normal 12 + tangent
    /// 16 + uv 8 + four blend weights and four indices 32 = 80 bytes, down to
    /// 36. With the 16-bit index buffer that is 10.79 MB a mesh to 4.97 —
    /// −54%, and nothing about the garment changes.
    ///
    /// ⚠️ Two things have to be true for any of it to reach the file, and both
    /// cost hours to find: it must run on the COPY that becomes the asset
    /// (`Object.Instantiate` does not carry a lean layout across), and the old
    /// asset must be DELETED rather than copied into — see the caller. Judge
    /// the result by the file's content, never by a file merely being there: a
    /// `.mesh` left from an earlier run reads exactly like a failure.
    /// </remarks>
    private static void Slim(Mesh mesh, bool keepTangents)
    {
        if (!keepTangents)
        {
            mesh.SetTangents(new List<Vector4>());
        }

        // ONLY when the mesh really is one bone. This was written for a rigid
        // kit and then inherited by the skinning-preserving weld, where it undid
        // the whole point: the merged stockings arrived with 8 correct bind poses
        // and every vertex pinned to bone 0, so the pair hung off one thigh —
        // the bind poses made it look right in every check that counted them.
        // A garment the body drives keeps its weights; it is 32 bytes a vertex,
        // and it is the difference between cloth and a plank.
        if (mesh.bindposes == null || mesh.bindposes.Length <= 1)
        {
            SetSingleBoneSkin(mesh, mesh.vertexCount);
        }
    }

    /// <summary>
    /// Bind every vertex to bone 0 with weight 1, storing ONE influence.
    /// </summary>
    /// <remarks>
    /// `mesh.boneWeights = …` always writes the four-influence layout: four
    /// indices and four weights, 32 bytes a vertex, of which this mesh uses
    /// eight and pads the rest with zeroes. That was the single biggest block
    /// in the file — bigger than positions, normals and UVs together. The
    /// modern API stores exactly what is there.
    ///
    /// The catch is that the LEGACY `mesh.boneWeights` GETTER then comes back
    /// empty, which is the trap `HealthDollStage` already documents (it painted
    /// the whole doll one colour). Anything reading weights off a garment has
    /// to use GetAllBoneWeights — see the matching fix in SeveredLimbFactory.
    /// </remarks>
    private static void SetSingleBoneSkin(Mesh mesh, int vertexCount)
    {
        var bonesPerVertex = new Unity.Collections.NativeArray<byte>(
            vertexCount, Unity.Collections.Allocator.Temp);
        var influences = new Unity.Collections.NativeArray<BoneWeight1>(
            vertexCount, Unity.Collections.Allocator.Temp);
        try
        {
            for (var i = 0; i < vertexCount; i++)
            {
                bonesPerVertex[i] = 1;
                influences[i] = new BoneWeight1 { boneIndex = 0, weight = 1f };
            }

            mesh.SetBoneWeights(bonesPerVertex, influences);
        }
        finally
        {
            bonesPerVertex.Dispose();
            influences.Dispose();
        }
    }

    private static MatSpec SpecFor(GarmentSpec g, string materialName)
    {
        foreach (var spec in g.Materials)
        {
            if (spec.Source == materialName ||
                System.Array.IndexOf(spec.Aliases, materialName) >= 0)
            {
                return spec;
            }
        }

        return g.Materials[0];
    }

    /// <summary>
    /// The body bone a part really hangs from.
    /// </summary>
    /// <remarks>
    /// Walking up for a bone of the BODY is not enough. A prop can be pinned to
    /// a garment instead — the Nerd Crush bow tie hangs off the blouse's own
    /// rig, not off the girl — and then the walk reaches the top having found
    /// nothing, and the piece is dropped. A conforming garment mirrors the
    /// body's bone names, though, and `Wear.Construct` stitches by name anyway,
    /// so a bone called `chestUpper` on the blouse means the body's `chestUpper`.
    /// </remarks>
    private static Transform NearestBone(
        Transform from, HashSet<Transform> bones, Dictionary<string, Transform> byName)
    {
        for (var t = from; t != null; t = t.parent)
        {
            if (bones.Contains(t))
            {
                return t;
            }
        }

        for (var t = from; t != null; t = t.parent)
        {
            if (byName.TryGetValue(t.name, out var mirrored))
            {
                return mirrored;
            }
        }

        return null;
    }

    private static Transform CommonAncestor(Transform a, Transform b)
    {
        for (var x = a; x != null; x = x.parent)
        {
            for (var y = b; y != null; y = y.parent)
            {
                if (x == y)
                {
                    return x;
                }
            }
        }

        return a;
    }

    /// <summary>
    /// A garment's texture, wherever the staging step happened to leave it.
    /// </summary>
    /// <remarks>
    /// `build` stages textures under a folder derived from the DAZ MESH KEY,
    /// because at that point nobody has named the garment yet. Renaming it in
    /// the manifest during review — which is the whole point of the review —
    /// then strands them, and every material comes out with no albedo: a white
    /// garment in the world, not just on the icon.
    ///
    /// That has now happened on two separate drops, so the fix belongs here
    /// rather than in a checklist: look under the reviewed name first, and fall
    /// back to the draft one the key would have produced.
    /// </remarks>
    private static Texture2D FindTexture(GarmentSpec g, string file)
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(
            $"{ImportRoot}/{g.Folder}/Textures/{file}");
        if (tex != null)
        {
            return tex;
        }

        foreach (var key in g.SourceKeys ?? new[] { g.SourceKey })
        {
            var draft = string.Concat(key.Where(char.IsLetterOrDigit));
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                $"{ImportRoot}/{draft}/Textures/{file}");
            if (tex != null)
            {
                return tex;
            }
        }

        return null;
    }

    private static Material BuildMaterial(GarmentSpec g, MatSpec spec)
    {
        var path = $"{ImportRoot}/{g.Folder}/Materials/{Sanitize(spec.Source)}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
        }

        mat.SetColor("_BaseColor", spec.Color);
        mat.SetFloat("_Smoothness", spec.Smoothness);
        mat.SetFloat("_Metallic", spec.Metallic);
        if (spec.DoubleSided)
        {
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        }

        // Карта ставится ВСЕГДА, в том числе в null. Раньше её только
        // назначали: если у поверхности текстуру убрали из манифеста, материал
        // молча оставался со старой. Бойцовский топ так и остался в чужом
        // атласе кэйкоги после того, как атлас был снят, — и выглядело это как
        // «артефакт текстуры», а не как невыполненная правка.
        var tex = spec.Texture != null ? FindTexture(g, spec.Texture) : null;
        if (spec.Texture != null && tex == null)
        {
            Debug.LogWarning($"[NewWear] {g.Folder}: texture {spec.Texture} not imported yet");
        }

        mat.SetTexture("_BaseMap", tex);

        if (spec.AlphaClip)
        {
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.3f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Clone the reference skeleton subtree under "hip" (pruned to the bones
    // this garment skins to + their ancestors) and add the renderer child.
    private static void BuildBonesAndRenderer(
        GarmentSpec g, SkinnedMeshRenderer reference, Mesh defaultMesh,
        Dictionary<string, Material> mats, GameObject root)
    {
        var srcHip = FindGarmentHip(reference);
        if (srcHip == null)
        {
            throw new IOException($"{g.SourceKey}: no 'hip' bone in the source FBX");
        }

        // Wanted bones are held BY OBJECT, not by name. DAZ parents each fitted
        // figure under the body's skeleton and gives it a full copy of the bone
        // names, so one export has 19 transforms called `hip`. Selecting by name
        // let a neighbouring garment's node in — the cap's skeleton rode into the
        // stockings prefab — and, worse, `clones` is keyed by name, so the cap's
        // `hip` clone overwrote the body's and the mesh would have bound to it.
        var needed = new HashSet<Transform>();
        foreach (var bone in reference.bones)
        {
            if (bone == null)
            {
                continue;
            }

            // The bone itself plus every ancestor up to (and including) hip.
            for (var t = bone; t != null; t = t.parent)
            {
                needed.Add(t);
                if (t == srcHip)
                {
                    break;
                }
            }
        }

        if (reference.rootBone != null)
        {
            needed.Add(reference.rootBone);
        }

        var clones = new Dictionary<string, Transform>();
        var hipClone = CloneBoneSubtree(srcHip, root.transform, needed, clones);

        // Кости, которых под тазом нет вовсе. Их приносит сварка: у юбки свои
        // `Skirt Left/Right/Back`, у байкерской куртки — кости бегунков молний,
        // и висят они в экспорте не под скелетом тела, а сами по себе. Раньше
        // такая вещь просто падала с «5 bone(s) missing», и вместе с ней — весь
        // заход.
        //
        // Подцепляем их к клону ближайшего предка, который уже склонирован;
        // если такого нет — к тазу. Локальные преобразования сохраняются, так
        // что деталь остаётся там, где её нарисовал автор.
        foreach (var bone in reference.bones)
        {
            if (bone == null || clones.ContainsKey(bone.name))
            {
                continue;
            }

            var chain = new List<Transform>();
            var walk = bone;
            while (walk != null && !clones.ContainsKey(walk.name))
            {
                chain.Add(walk);
                walk = walk.parent;
            }

            var anchor = walk != null ? clones[walk.name] : hipClone;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                anchor = CloneBone(chain[i], anchor, clones);
            }
        }

        var meshGo = new GameObject(g.Name + " Mesh");
        meshGo.transform.SetParent(root.transform, false);
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = defaultMesh;
        // Quietly collapsing an unresolved bone onto the root is how the Sweet
        // Jane skirt shipped welded to the hip: its two thigh bones were absent
        // from the cloned skeleton, so the hem never followed the legs and it
        // read as a fitting problem rather than a broken bind.
        var missing = reference.bones
            .Where(b => b == null || !clones.ContainsKey(b.name))
            .Select(b => b != null ? b.name : "<null>")
            .Distinct()
            .ToArray();
        if (missing.Length > 0)
        {
            throw new IOException(
                $"{g.SourceKey}: {missing.Length} bone(s) missing from the cloned " +
                $"skeleton under '{srcHip.name}': {string.Join(", ", missing)}");
        }

        smr.bones = reference.bones.Select(b => clones[b.name]).ToArray();
        smr.rootBone = reference.rootBone != null &&
            clones.TryGetValue(reference.rootBone.name, out var rb)
            ? rb
            : hipClone;
        smr.localBounds = reference.localBounds;
        smr.sharedMaterials = reference.sharedMaterials
            .Select(m =>
            {
                var name = m != null ? m.name.Replace(" (Instance)", "") : "";
                return mats.TryGetValue(name, out var mapped) ? mapped : mats.Values.First();
            })
            .ToArray();
    }

    /// <summary>Одна кость, без её детей.</summary>
    private static Transform CloneBone(Transform src, Transform parent,
                                       Dictionary<string, Transform> clones)
    {
        var clone = new GameObject(src.name).transform;
        clone.SetParent(parent, false);
        clone.localPosition = src.localPosition;
        clone.localRotation = src.localRotation;
        clone.localScale = src.localScale;
        clones[src.name] = clone;
        return clone;
    }

    private static Transform CloneBoneSubtree(
        Transform src, Transform parent, HashSet<Transform> needed,
        Dictionary<string, Transform> clones)
    {
        var clone = CloneBone(src, parent, clones);
        foreach (Transform child in src)
        {
            if (SubtreeNeeded(child, needed))
            {
                CloneBoneSubtree(child, clone, needed, clones);
            }
        }

        return clone;
    }

    private static bool SubtreeNeeded(Transform t, HashSet<Transform> needed)
    {
        if (needed.Contains(t))
        {
            return true;
        }

        foreach (Transform child in t)
        {
            if (SubtreeNeeded(child, needed))
            {
                return true;
            }
        }

        return false;
    }

    private static void FillWearComponent(
        GarmentSpec g, Dictionary<ActorName, Mesh> meshes, GameObject root)
    {
        // Fit scales are tuned by hand in WardrobeTest and are NOT authored here
        // (every garment lands at 1.0). Carry the tuned values over, or a force
        // re-extract quietly throws away the fitting pass on every older piece.
        var tuned = ReadTunedFit(g);

        var wear = root.AddComponent<Wear>();
        var so = new SerializedObject(wear);

        var configs = so.FindProperty("configs");
        configs.arraySize = meshes.Count;
        var i = 0;
        foreach (var pair in meshes.OrderBy(p => (int)p.Key))
        {
            var e = configs.GetArrayElementAtIndex(i++);
            var fit = tuned.TryGetValue(pair.Key, out var f) ? f : (scale: 1f, heightOffset: 0f);
            e.FindPropertyRelative("actorName").enumValueIndex = (int)pair.Key;
            e.FindPropertyRelative("scale").floatValue = fit.scale;
            e.FindPropertyRelative("heightOffset").floatValue = fit.heightOffset;
            e.FindPropertyRelative("mesh").objectReferenceValue = pair.Value;
        }

        var heel = so.FindProperty("heel");
        heel.FindPropertyRelative("footDegrees").floatValue = g.Heel.footDegrees;
        heel.FindPropertyRelative("toeDegrees").floatValue = g.Heel.toeDegrees;
        heel.FindPropertyRelative("lift").floatValue = g.Heel.lift;
        heel.FindPropertyRelative("axis").vector3Value = g.Heel.axis;

        WriteSlotList(so.FindProperty("slots"), g.Slots);

        // Исключения «бельё видно насквозь» и флаг волос ставятся ГЛАЗАМИ, в
        // тестовой сцене, и манифест о них ничего не знает. Пересбор обязан их
        // сохранить — ровно как сохраняет подгонку размера выше: иначе одна
        // команда «пересобрать» молча стирает вечер работы, и понять это можно
        // только заметив, что бельё снова спряталось.
        var previous = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(g));
        var tunedWear = previous != null ? previous.GetComponent<Wear>() : null;
        WriteSlotList(so.FindProperty("noHideUnderwearSlots"),
            tunedWear != null ? tunedWear.NoHideUnderwearSlots.ToArray() : g.NoHide);
        so.FindProperty("layer").enumValueIndex = (int)g.Layer;
        so.FindProperty("gender").enumValueIndex = (int)VisualGender.Female;
        // Всё, что садится на голову, по умолчанию прячет причёску: шапка — это
        // оболочка вокруг черепа, а причёска отдельный меш поверх него, и без
        // этого волосы прорастают сквозь тулью. Правится галочкой в тестовой
        // сцене — есть шляпы, из-под которых волосы должны торчать.
        so.FindProperty("hidesHair").boolValue = tunedWear != null
            ? tunedWear.HidesHair
            : g.Slots != null && g.Slots.Contains(VisualWearSlot.Head);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Read the fit tuned into the prefab we are about to overwrite. Empty on a
    // first extract, and for any girl who was not part of the earlier drop.
    private static Dictionary<ActorName, (float scale, float heightOffset)> ReadTunedFit(
        GarmentSpec g)
    {
        var tuned = new Dictionary<ActorName, (float scale, float heightOffset)>();
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(g));
        if (existing == null || !existing.TryGetComponent<Wear>(out var wear))
        {
            return tuned;
        }

        var configs = new SerializedObject(wear).FindProperty("configs");
        for (var i = 0; i < configs.arraySize; i++)
        {
            var e = configs.GetArrayElementAtIndex(i);
            tuned[(ActorName)e.FindPropertyRelative("actorName").enumValueIndex] = (
                e.FindPropertyRelative("scale").floatValue,
                e.FindPropertyRelative("heightOffset").floatValue);
        }

        return tuned;
    }

    private static void WriteSlotList(SerializedProperty list, VisualWearSlot[] slots)
    {
        list.arraySize = slots.Length;
        for (var i = 0; i < slots.Length; i++)
        {
            list.GetArrayElementAtIndex(i).enumValueIndex = (int)slots[i];
        }
    }

    // A DAZ FBX of a dressed figure carries ONE skeleton copy PER FITTED GARMENT
    // on top of the figure's own, each rooted at its own node called "hip" and
    // pruned by DAZ to just the bones that garment is weighted to. Searching the
    // whole file for "hip" lands on whichever copy comes first — a foreign one,
    // whose pruning has nothing to do with this garment: the Sweet Jane FBX has
    // six of them, and the tank's copy (torso only) has no thigh bones at all.
    // Walk up from this garment's own bones instead; copies are siblings, never
    // nested, so the nearest "hip" ancestor is always the right one.
    private static Transform FindGarmentHip(SkinnedMeshRenderer reference)
    {
        foreach (var bone in reference.bones)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                if (t.name == "hip")
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static string Sanitize(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }

    /// <summary>A colourway's name as the tail of an item id.</summary>
    private static string VariantSlug(string name)
    {
        var slug = new string(name
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray());
        // Runs of punctuation collapse, so "Red / Purple" is `red_purple` rather
        // than `red___purple`.
        while (slug.Contains("__"))
        {
            slug = slug.Replace("__", "_");
        }

        return slug.Trim('_');
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        var leaf = Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif
