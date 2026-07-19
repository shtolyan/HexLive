#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  New-wear extractor (spec §31B.4, "new wear" drop of 2026-07).
//
//  Assets/Temp holds three DAZ FBX exports — jana / marta / molly, each
//  wearing the SAME nine garments fitted to her body. This tool pulls the
//  garment meshes out of every FBX and assembles the standard wear assets:
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

    // FBX file -> which girl the garments are fitted to.
    private static readonly (string path, ActorName actor)[] Sources =
    {
        ("Assets/Temp/molly new.fbx", ActorName.Molly),
        ("Assets/Temp/marta new wear.fbx", ActorName.Marta),
        ("Assets/Temp/jana new wear.fbx", ActorName.Jana),
    };

    private sealed class MatSpec
    {
        public string Source;       // material name inside the FBX
        public string Texture;      // file under <Folder>/Textures (null = plain color)
        public Color Color = Color.white;
        public float Smoothness = 0.3f;
        public float Metallic;
        public bool DoubleSided = true;
        public bool AlphaClip;
    }

    private sealed class GarmentSpec
    {
        public string SourceKey;    // mesh name inside the FBX
        public string Folder;       // ImportedActors/Wear/<Folder>
        public string Name;         // prefab + root GameObject name
        public string SimId;        // Resources/HexLive/Wear/<SimId>/
        public VisualWearLayer Layer;
        public VisualWearSlot[] Slots;
        public VisualWearSlot[] NoHide = { };
        public MatSpec[] Materials;
    }

    // Slot / layer choices mirror the closest shipped garment (skirt =
    // Skirt G3F, sweater = Jacket_7653, dresses = CityDress_1).
    private static readonly GarmentSpec[] Garments =
    {
        new()
        {
            SourceKey = "fl-panty_1661", Folder = "PantyFlair", Name = "PantyFlair",
            SimId = "underwear.panty_flair", Layer = VisualWearLayer.Underwear,
            Slots = new[] { VisualWearSlot.Pelvis },
            Materials = new[]
            {
                new MatSpec { Source = "panty", Texture = "flair-panty-01.jpg" },
            },
        },
        new()
        {
            SourceKey = "Panties G3F_2867", Folder = "PantyBasic", Name = "PantyBasic",
            SimId = "underwear.panty_basic", Layer = VisualWearLayer.Underwear,
            Slots = new[] { VisualWearSlot.Pelvis },
            Materials = new[]
            {
                new MatSpec { Source = "Mat1", Smoothness = 0.5f },
                new MatSpec { Source = "Mat2", Smoothness = 0.5f },
            },
        },
        new()
        {
            SourceKey = "Bra G3F_3690", Folder = "BraBasic", Name = "BraBasic",
            SimId = "underwear.bra_basic", Layer = VisualWearLayer.Underwear,
            Slots = new[] { VisualWearSlot.Chest },
            Materials = new[]
            {
                new MatSpec { Source = "Mat1", Smoothness = 0.5f },
                new MatSpec { Source = "Mat2", Smoothness = 0.5f },
            },
        },
        new()
        {
            SourceKey = "Swimsuit Top G3F_3209", Folder = "SwimTop", Name = "SwimTop",
            SimId = "underwear.swim_top", Layer = VisualWearLayer.Underwear,
            Slots = new[] { VisualWearSlot.Chest },
            Materials = new[]
            {
                new MatSpec { Source = "Mat1", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe1", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe2", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe3", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe4", Smoothness = 0.5f },
            },
        },
        new()
        {
            SourceKey = "Swimsuit Bottom G3F_4769", Folder = "SwimBottom", Name = "SwimBottom",
            SimId = "underwear.swim_bottom", Layer = VisualWearLayer.Underwear,
            Slots = new[] { VisualWearSlot.Pelvis },
            Materials = new[]
            {
                new MatSpec { Source = "Mat1", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe1", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe2", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe3", Smoothness = 0.5f },
                new MatSpec { Source = "Stripe4", Smoothness = 0.5f },
            },
        },
        new()
        {
            SourceKey = "fl-skirt_3552", Folder = "SkirtFlair", Name = "SkirtFlair",
            SimId = "clothing.skirt_flair", Layer = VisualWearLayer.Wear,
            Slots = new[] { VisualWearSlot.Pelvis },
            NoHide = new[] { VisualWearSlot.Pelvis },
            Materials = new[]
            {
                new MatSpec { Source = "skirt", Texture = "flair-skirt-01.jpg" },
                new MatSpec { Source = "waistband", Texture = "flair-skirt-01.jpg" },
            },
        },
        new()
        {
            SourceKey = "fl-sweater_8621", Folder = "SweaterFlair", Name = "SweaterFlair",
            SimId = "clothing.sweater_flair", Layer = VisualWearLayer.Wear,
            Slots = new[]
            {
                VisualWearSlot.Chest, VisualWearSlot.ShoulderR, VisualWearSlot.ShoulderL,
                VisualWearSlot.ForearmR, VisualWearSlot.ForearmL,
            },
            Materials = new[]
            {
                new MatSpec { Source = "sweater", Texture = "flair-sweat-01.jpg" },
                new MatSpec { Source = "Sleeves", Texture = "flair-sweat-01.jpg" },
            },
        },
        new()
        {
            SourceKey = "ndrss_dress_27797", Folder = "NightDress", Name = "NightDress",
            SimId = "clothing.dress_night", Layer = VisualWearLayer.Wear,
            Slots = new[] { VisualWearSlot.Chest, VisualWearSlot.Belly, VisualWearSlot.Pelvis },
            NoHide = new[] { VisualWearSlot.Chest },
            Materials = new[]
            {
                new MatSpec { Source = "Base", Texture = "mytilus_ndrss_dressW_tex.jpg", Smoothness = 0.35f },
                new MatSpec
                {
                    Source = "Clasp", Color = new Color(1f, 0.8118f, 0.5137f),
                    Metallic = 0.8f, Smoothness = 0.65f, DoubleSided = false,
                },
                new MatSpec { Source = "SkirtLace", Smoothness = 0.3f },
            },
        },
        new()
        {
            SourceKey = "PriDre_plain_fur_dress_116570", Folder = "FurDress", Name = "FurDress",
            SimId = "clothing.dress_fur", Layer = VisualWearLayer.Wear,
            Slots = new[] { VisualWearSlot.Chest, VisualWearSlot.Belly, VisualWearSlot.Pelvis },
            NoHide = new[] { VisualWearSlot.Chest },
            Materials = new[]
            {
                new MatSpec { Source = "fur_plain", Texture = "PrDr_fur5_alpha.png", Smoothness = 0.2f, AlphaClip = true },
                new MatSpec { Source = "dress", Texture = "PrDr_fur5.jpg", Smoothness = 0.25f },
            },
        },
    };

    // One-shot auto-run: fires after every compile, does nothing once all nine
    // prefabs exist (or when the Temp FBX drop is gone from this machine).
    [InitializeOnLoadMethod]
    private static void AutoRun()
    {
        EditorApplication.delayCall += () =>
        {
            if (Application.productName != "HexLive") return;
            if (Sources.Any(s => !File.Exists(s.path))) return;
            if (Garments.All(g => File.Exists(PrefabPath(g)))) return;
            Run(force: false);
        };
    }

    [MenuItem("HexLive/Wear/Extract New Wear (Temp FBX)")]
    private static void RunMenu() => Run(force: false);

    [MenuItem("HexLive/Wear/Extract New Wear (Force Re-Extract)")]
    private static void RunForceMenu() => Run(force: true);

    private static string PrefabPath(GarmentSpec g) => $"{WearRoot}/{g.SimId}/{g.Name}.prefab";

    private static void Run(bool force)
    {
        var missingFbx = Sources.Where(s => !File.Exists(s.path)).Select(s => s.path).ToList();
        if (missingFbx.Count > 0)
        {
            Debug.LogError("[NewWear] FBX not found: " + string.Join(", ", missingFbx));
            return;
        }

        // Instantiate the three FBX rigs once; index every garment renderer.
        var instances = new List<GameObject>();
        var renderers = new Dictionary<(ActorName actor, string key), SkinnedMeshRenderer>();
        try
        {
            foreach (var (path, actor) in Sources)
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
                foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var key = Garments.FirstOrDefault(g =>
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
            var log = new System.Text.StringBuilder();
            foreach (var g in Garments)
            {
                if (!force && File.Exists(PrefabPath(g)))
                {
                    skipped++;
                    continue;
                }

                if (ExtractGarment(g, renderers, log))
                {
                    done++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[NewWear] extracted {done}, skipped (already built) {skipped}\n{log}");

            if (done > 0)
            {
                // Materialize the new GarmentLibrary rows as GarmentDefinition
                // assets + refresh the catalog, then re-export simdata.json
                // (spec §59.3 — headless probes refuse to run without it).
                EditorApplication.ExecuteMenuItem("HexLive/Garments/Rebuild Catalog From Defaults");
                EditorApplication.ExecuteMenuItem("HexLive/Export Sim Data (JSON)");
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

    private static bool ExtractGarment(
        GarmentSpec g,
        Dictionary<(ActorName, string), SkinnedMeshRenderer> renderers,
        System.Text.StringBuilder log)
    {
        if (!renderers.TryGetValue((ActorName.Jana, g.SourceKey), out var reference))
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: no renderer in the jana FBX — skipped");
            return false;
        }

        EnsureFolder($"{ImportRoot}/{g.Folder}/Meshes");
        EnsureFolder($"{ImportRoot}/{g.Folder}/Materials");
        EnsureFolder($"{WearRoot}/{g.SimId}");

        // --- meshes: one fitted copy per girl --------------------------------
        var meshes = new Dictionary<ActorName, Mesh>();
        foreach (var (_, actor) in Sources)
        {
            if (!renderers.TryGetValue((actor, g.SourceKey), out var r) || r.sharedMesh == null)
            {
                Debug.LogWarning($"[NewWear] {g.SourceKey}: no mesh for {actor}");
                continue;
            }

            var path = $"{ImportRoot}/{g.Folder}/Meshes/{actor}.mesh";
            var copy = Object.Instantiate(r.sharedMesh);
            copy.name = actor.ToString();
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                // Keep the GUID stable across re-runs — prefab refs survive.
                EditorUtility.CopySerialized(copy, existing);
                Object.DestroyImmediate(copy);
                meshes[actor] = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(copy, path);
                meshes[actor] = copy;
            }
        }

        if (!meshes.ContainsKey(ActorName.Jana))
        {
            Debug.LogError($"[NewWear] {g.SourceKey}: no Jana mesh — skipped");
            return false;
        }

        // --- materials (URP Lit, flat albedo per the art style) --------------
        var mats = new Dictionary<string, Material>();
        foreach (var spec in g.Materials)
        {
            mats[spec.Source] = BuildMaterial(g, spec);
        }

        // --- prefab -----------------------------------------------------------
        var root = new GameObject(g.Name);
        try
        {
            BuildBonesAndRenderer(g, reference, meshes[ActorName.Jana], mats, root);
            FillWearComponent(g, meshes, root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(g));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        log.AppendLine($"  {g.SourceKey} -> {PrefabPath(g)} ({meshes.Count} meshes, {mats.Count} mats)");
        return true;
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

        if (spec.Texture != null)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                $"{ImportRoot}/{g.Folder}/Textures/{spec.Texture}");
            if (tex == null)
            {
                Debug.LogWarning($"[NewWear] {g.Folder}: texture {spec.Texture} not imported yet");
            }

            mat.SetTexture("_BaseMap", tex);
        }

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
        var srcHip = FindByName(reference.rootBone != null
            ? reference.rootBone.root
            : reference.transform.root, "hip");
        if (srcHip == null)
        {
            throw new IOException($"{g.SourceKey}: no 'hip' bone in the source FBX");
        }

        var needed = new HashSet<string>();
        foreach (var bone in reference.bones)
        {
            if (bone == null)
            {
                continue;
            }

            // The bone itself plus every ancestor up to (and including) hip.
            for (var t = bone; t != null; t = t.parent)
            {
                needed.Add(t.name);
                if (t == srcHip)
                {
                    break;
                }
            }
        }

        if (reference.rootBone != null)
        {
            needed.Add(reference.rootBone.name);
        }

        var clones = new Dictionary<string, Transform>();
        var hipClone = CloneBoneSubtree(srcHip, root.transform, needed, clones);

        var meshGo = new GameObject(g.Name + " Mesh");
        meshGo.transform.SetParent(root.transform, false);
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = defaultMesh;
        smr.bones = reference.bones
            .Select(b => b != null && clones.TryGetValue(b.name, out var c) ? c : hipClone)
            .ToArray();
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

    private static Transform CloneBoneSubtree(
        Transform src, Transform parent, HashSet<string> needed,
        Dictionary<string, Transform> clones)
    {
        var clone = new GameObject(src.name).transform;
        clone.SetParent(parent, false);
        clone.localPosition = src.localPosition;
        clone.localRotation = src.localRotation;
        clone.localScale = src.localScale;
        clones[src.name] = clone;
        foreach (Transform child in src)
        {
            if (SubtreeNeeded(child, needed))
            {
                CloneBoneSubtree(child, clone, needed, clones);
            }
        }

        return clone;
    }

    private static bool SubtreeNeeded(Transform t, HashSet<string> needed)
    {
        if (needed.Contains(t.name))
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
        var wear = root.AddComponent<Wear>();
        var so = new SerializedObject(wear);

        var configs = so.FindProperty("configs");
        configs.arraySize = meshes.Count;
        var i = 0;
        foreach (var pair in meshes.OrderBy(p => (int)p.Key))
        {
            var e = configs.GetArrayElementAtIndex(i++);
            e.FindPropertyRelative("actorName").enumValueIndex = (int)pair.Key;
            e.FindPropertyRelative("scale").floatValue = 1f;
            e.FindPropertyRelative("mesh").objectReferenceValue = pair.Value;
        }

        WriteSlotList(so.FindProperty("slots"), g.Slots);
        WriteSlotList(so.FindProperty("noHideUnderwearSlots"), g.NoHide);
        so.FindProperty("layer").enumValueIndex = (int)g.Layer;
        so.FindProperty("gender").enumValueIndex = (int)VisualGender.Female;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void WriteSlotList(SerializedProperty list, VisualWearSlot[] slots)
    {
        list.arraySize = slots.Length;
        for (var i = 0; i < slots.Length; i++)
        {
            list.GetArrayElementAtIndex(i).enumValueIndex = (int)slots[i];
        }
    }

    private static Transform FindByName(Transform root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name == name);
    }

    private static string Sanitize(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
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
