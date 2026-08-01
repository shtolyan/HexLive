#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;
using UnityMeshSimplifier;

// ---------------------------------------------------------------------------
//  Hairstyle extractor ("hair" drop of 2026-08).
//
//  Assets/Temp/hair.fbx is one DAZ export of Genesis3Female wearing thirteen
//  hairstyles at once, TWELVE of which we ship (see the Mai Hair note in the
//  table below). This tool pulls each hair mesh out and assembles the
//  standard hair assets, which are ordinary Wear prefabs (spec §31B.2) — the
//  same contract the shipped OnyxHair / ShilohHair / JelikaHair follow:
//
//    Assets/ImportedActors/Wear/<Name>/Meshes/<Name>.mesh      (decimated)
//    Assets/ImportedActors/Wear/<Name>/Materials/<mat>.mat     (URP Lit)
//    Assets/ImportedActors/Wear/<Name>/Textures/*.png          (prepared offline)
//    Assets/ImportedActors/Wear/<Name>/<Name>.prefab           (Wear prefab)
//
//  To put one on a girl: drop the prefab into the `hair` field of her
//  BodyBones (Assets/Resources/HexLive/Actors/<Girl>.prefab). BodyBones
//  spawns it in Construct and Wear.Construct stitches every hair bone onto
//  the matching body bone by NAME, so any hair fits any actor — all thirteen
//  skin to the shared Genesis3 head/neck/face bones.
//
//  TEXTURES are NOT built here. DAZ ships colour and opacity as two separate
//  images and DAZ FBX does not embed either, so they are composited offline
//  (diffuse RGB + transparency map -> alpha) straight from the DAZ library
//  into each hair's Textures/ folder, capped at 1024. That is why every
//  MatSpec below names a ready .png instead of a source jpg pair.
//
//  DECIMATION: DAZ hair arrives subdivided — 42 K to 918 K verts. Shipped hair
//  spans 33 K (JelikaHair) to 270 K (OnyxHair, on Jolly right now), so only the
//  heaviest of the drop are actually out of line. Anything above TargetVerts is
//  simplified with border/UV-seam preservation ON: that strips the subdivision
//  but keeps the hair-card outlines and UV islands crisp, which is what the
//  alpha cut-out reads. Bone weights and bindposes survive the simplifier.
//
//  Runs automatically once after compile (only while some target prefab is
//  missing and the Temp FBX exists). Re-runs are idempotent; use the Force
//  menu item to stamp everything again after changing the specs below.
//
//  Menu: HexLive/Hair/Extract Hair (Temp FBX)
//        HexLive/Hair/Extract Hair (FORCE Re-Extract)
// ---------------------------------------------------------------------------
public static class HairExtractor
{
    private const string Fbx = "Assets/Temp/hair.fbx";
    private const string ImportRoot = "Assets/ImportedActors/Wear";

    // Shipped hair for scale: JelikaHair 33 K, ShilohHair 79 K, LowPonytail
    // 129 K, OnyxHair 270 K. 150 K keeps a new hair at or under the
    // LowPonytail/Shiloh band without dropping below what is already in game;
    // it leaves 8 of the 13 untouched and mainly exists to cut Mai Hair (918 K)
    // down to size. Hair cards are mostly border edges, so the simplifier
    // FLOORS well above a naive ratio — the log prints what each mesh landed on.
    private const int TargetVerts = 150000;

    private sealed class MatSpec
    {
        public string Source;       // material name inside the FBX
        public string Texture;      // file under <Name>/Textures (null = plain colour)
        public Color Color = Color.white;
        public bool AlphaClip;      // has an opacity map baked into alpha
    }

    private sealed class HairSpec
    {
        public string SourceKey;    // mesh/node name inside the FBX
        public string Name;         // folder + prefab + root GameObject name
        public MatSpec[] Materials;
    }

    // Generated from hair.fbx (geometry -> material -> texture connections),
    // so the material names match the FBX exactly. Plain colours are the
    // material's DiffuseColor for the few untextured bits (hair bands, beads).
    private static readonly HairSpec[] Hairs =
    {
        new()
        {
            SourceKey = "SW_AdellHRG3_59959", Name = "AdellHair",
            Materials = new[]
            {
                new MatSpec { Source = "Hair", Texture = "SW_Iray05_Col03__SW_AdellHR_TR01.png", AlphaClip = true },
                new MatSpec { Source = "Base", Texture = "SW_Iray05_Col03_D__SW_Iray_BaseTR_D.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "SWAM_AsukaHr_99140", Name = "AsukaHair",
            Materials = new[]
            {
                new MatSpec { Source = "Scalp", Texture = "SWAM_Asuka_Scalp_C11__SWAM_Asuka_ScalpTR.png", AlphaClip = true },
                new MatSpec { Source = "A_Strands", Texture = "SWAM_Asuka_C11__SWAM_Asuka_A.png", AlphaClip = true },
                new MatSpec { Source = "A_Strands_A", Texture = "SWAM_Asuka_C11__SWAM_Asuka_A.png", AlphaClip = true },
                new MatSpec { Source = "A_Strands_B", Texture = "SWAM_Asuka_C11__SWAM_Asuka_A.png", AlphaClip = true },
                new MatSpec { Source = "A_Strands_C", Texture = "SWAM_Asuka_C11__SWAM_Asuka_A.png", AlphaClip = true },
                new MatSpec { Source = "B_Base", Texture = "SWAM_Asuka_C11__SWAM_Asuka_B.png", AlphaClip = true },
                new MatSpec { Source = "B_Base_Fine", Texture = "SWAM_Asuka_C11__SWAM_Asuka_B.png", AlphaClip = true },
                new MatSpec { Source = "B_Base_Str", Texture = "SWAM_Asuka_C11__SWAM_Asuka_B.png", AlphaClip = true },
                new MatSpec { Source = "C_Bow_Curl", Texture = "SWAM_Asuka_C11__SWAM_Asuka_C.png", AlphaClip = true },
                new MatSpec { Source = "B_Base_Neck", Texture = "SWAM_Asuka_C11__SWAM_Asuka_B.png", AlphaClip = true },
                new MatSpec { Source = "C_Bow_Fine", Texture = "SWAM_Asuka_C11__SWAM_Asuka_C.png", AlphaClip = true },
                new MatSpec { Source = "C_Bow", Texture = "SWAM_Asuka_C11__SWAM_Asuka_C.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "OOTHair2008_327457", Name = "BendineHair",
            Materials = new[]
            {
                new MatSpec { Source = "Side2", Texture = "08OOTBendineHair.png" },
                new MatSpec { Source = "Front2", Texture = "08OOTBendineHair.png" },
                new MatSpec { Source = "Back", Texture = "08OOTBendineHair.png" },
                new MatSpec { Source = "Bangs2", Texture = "08OOTBendineHair.png" },
                new MatSpec { Source = "Bangs1", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front1", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "BackUnder", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "Side1", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "Cap", Texture = "08OOTBendineCap__OOTUtilityBendineCapT.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "Bob3_215632", Name = "Bob3Hair",
            Materials = new[]
            {
                new MatSpec { Source = "HairFiner", Texture = "01_BobHair__T_BobFiner.png", AlphaClip = true },
                new MatSpec { Source = "HairMiddle", Texture = "01_BobHair__T_BobMiddle.png", AlphaClip = true },
                new MatSpec { Source = "HairLow", Texture = "01_BobHair__T_BobLow.png", AlphaClip = true },
                new MatSpec { Source = "Scalp", Texture = "01_BobScalp__T_BobScalp.png", AlphaClip = true },
                new MatSpec { Source = "HairFine", Texture = "01_BobHair__T_BobFine.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "OOT1921Hair_205149", Name = "ChunkyHair",
            Materials = new[]
            {
                new MatSpec { Source = "Bangs2", Texture = "08OOTChunkyHair.png" },
                new MatSpec { Source = "Hairbands", Texture = "OOTCloth_08B.png" },
                new MatSpec { Source = "Bangs1", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Cap", Texture = "08OOTChunkyCap__OOTUtilityChunkyCapT.png", AlphaClip = true },
                new MatSpec { Source = "Back", Texture = "08OOTChunkyHair.png" },
                new MatSpec { Source = "BackUnder", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front1", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Side1", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front2", Texture = "08OOTChunkyHair.png" },
                new MatSpec { Source = "Side2", Texture = "08OOTChunkyHair.png" },
            },
        },
        new()
        {
            SourceKey = "aprilyshEilisHair_141518", Name = "EilisHair",
            Materials = new[]
            {
                new MatSpec { Source = "scalp", Texture = "apreils_txblonde__apreils_trans.png", AlphaClip = true },
                new MatSpec { Source = "hair1", Texture = "apreils_txblonde__apreils_trans.png", AlphaClip = true },
                new MatSpec { Source = "hair2", Texture = "apreils_txblonde__apreils_trans.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "Hair07_120761", Name = "Hair07",
            Materials = new[]
            {
                new MatSpec { Source = "Scalp", Texture = "SWAM18_HR17_O10__SWAM18_HR17_Trans_Base.png", AlphaClip = true },
                new MatSpec { Source = "B_FrontLonger", Texture = "SWAM18_HR17_O10__SWAM18_HR17_TransB.png", AlphaClip = true },
                new MatSpec { Source = "B_FrontFine", Texture = "SWAM18_HR17_O10__SWAM18_HR17_TransB.png", AlphaClip = true },
                new MatSpec { Source = "A_Hair", Texture = "SWAM18_HR17_O10__SWAM18_HR17_TransA.png", AlphaClip = true },
                new MatSpec { Source = "B_FrontShort", Texture = "SWAM18_HR17_O10__SWAM18_HR17_TransB.png", AlphaClip = true },
                new MatSpec { Source = "A_Back", Texture = "SWAM18_HR17_O10__SWAM18_HR17_TransA.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "Jennifer Hair02_113187", Name = "JenniferHair",
            Materials = new[]
            {
                new MatSpec { Source = "HairLow", Texture = "Hair_Color_A02__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "Braid01", Texture = "Hair_Color_A01__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "Braid02", Texture = "Hair_Color_A02__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "HairFine", Texture = "Hair_Color_A01__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "HairFront", Texture = "Hair_Color_A01__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "Scalp", Texture = "Hair_Basc_A__T_Hair_Basc.png", AlphaClip = true },
                new MatSpec { Source = "temples", Texture = "Hair_Color_A01__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "BraidBack02", Texture = "Hair_Color_A01__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "BraidBack01", Texture = "Hair_Color_A01__T2_hair.png", AlphaClip = true },
                new MatSpec { Source = "Rubber_band", Color = new Color(0.9137f, 0.8471f, 0.7020f) },
            },
        },
        new()
        {
            SourceKey = "LeonyPonytail_257085", Name = "LeonyPonytail",
            Materials = new[]
            {
                new MatSpec { Source = "Cap", Texture = "01OOTLeonyCap__OOTUtilityLeonyCapT.png", AlphaClip = true },
                new MatSpec { Source = "Bands", Color = new Color(0.1020f, 0.1020f, 0.1020f) },
                new MatSpec { Source = "Side2", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Side1", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Bangs2", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Bangs1", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "BackUnder", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Back", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front2", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front1", Texture = "01OOTLeonyHair__OOTUtilityLeonyHairT.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "SWAM_Loona_42643", Name = "LoonaHair",
            Materials = new[]
            {
                new MatSpec { Source = "Str_upper", Texture = "SWAM20_HR_LoonaCol_01__SWAM20_HR_Loona_TR.png", AlphaClip = true },
                new MatSpec { Source = "Fine", Texture = "SWAM20_HR_LoonaCol_01__SWAM20_HR_Loona_TR.png", AlphaClip = true },
                new MatSpec { Source = "Str_lower", Texture = "SWAM20_HR_LoonaCol_01__SWAM20_HR_Loona_TR.png", AlphaClip = true },
                new MatSpec { Source = "Hair_lower", Texture = "SWAM20_HR_LoonaCol_01__SWAM20_HR_Loona_TR.png", AlphaClip = true },
                new MatSpec { Source = "Hair_upper", Texture = "SWAM20_HR_LoonaCol_01__SWAM20_HR_Loona_TR.png", AlphaClip = true },
                new MatSpec { Source = "FlyAways", Texture = "SWAM20_HR_LoonaCol_01__SWAM20_HR_Loona_TR.png", AlphaClip = true },
                new MatSpec { Source = "Scalp", Texture = "SWAM20_HR_LoonaCol_01B__SWAM20_HR_Loona__TRBa.png", AlphaClip = true },
            },
        },
        // "Mai Hair Base_917973" is DELIBERATELY not extracted. It is the one
        // hairstyle in the drop the simplifier cannot bring into range: 1.36 M
        // verts imported, and even the harshest pass floored at 1.03 M / 180 MB
        // — 3.8x the shipped OnyxHair. Dropped 2026-08-01 rather than shipped
        // as a permanent outlier. Re-add this entry if it is ever decimated
        // properly out-of-engine (Blender), and note its textures would need
        // re-compositing too.
        new()
        {
            SourceKey = "Neu 09_122546", Name = "Neu09Hair",
            Materials = new[]
            {
                new MatSpec { Source = "A_hair", Texture = "SW_M_C06__SWAM_HR17_06_A.png", AlphaClip = true },
                new MatSpec { Source = "B_side", Texture = "SW_M_C06__SWAM_HR17_06_B.png", AlphaClip = true },
                new MatSpec { Source = "Rubber", Color = new Color(0.9176f, 0.5294f, 0.0000f) },
                new MatSpec { Source = "Cherry02", Color = new Color(0.9176f, 0.5294f, 0.0000f) },
                new MatSpec { Source = "Cherry01", Color = new Color(0.9176f, 0.5294f, 0.0000f) },
                new MatSpec { Source = "Scalp", Texture = "SW_M_C06__SWAM_HR17_06.png", AlphaClip = true },
                new MatSpec { Source = "A_neck", Texture = "SW_M_C06__SWAM_HR17_06_A.png", AlphaClip = true },
                new MatSpec { Source = "B_sideburn", Texture = "SW_M_C06__SWAM_HR17_06_B.png", AlphaClip = true },
                new MatSpec { Source = "B_strands", Texture = "SW_M_C06__SWAM_HR17_06_B.png", AlphaClip = true },
            },
        },
        new()
        {
            SourceKey = "TootsieRoll_141752", Name = "TootsieRollHair",
            Materials = new[]
            {
                new MatSpec { Source = "Bangs", Texture = "01_TootsieCurl__T_TootsieCurl.png", AlphaClip = true },
                new MatSpec { Source = "OuterCurl", Texture = "01_TootsieRolls__T_TootsieRolls.png", AlphaClip = true },
                new MatSpec { Source = "LongCurl", Texture = "01_TootsieCurl__T_TootsieCurl.png", AlphaClip = true },
                new MatSpec { Source = "Hair", Texture = "01_TootsieHair__T_TootsieHair.png", AlphaClip = true },
                new MatSpec { Source = "FineHair", Texture = "01_TootsieCurl__T_TootsieCurl.png", AlphaClip = true },
                new MatSpec { Source = "Scalp", Texture = "01_TootsieScalp__T_TootsieScalp.png", AlphaClip = true },
                new MatSpec { Source = "UpperOuterFine", Texture = "01_TootsieRolls__T_TootsieRolls.png", AlphaClip = true },
                new MatSpec { Source = "MidCurl", Texture = "01_TootsieRolls__T_TootsieRolls.png", AlphaClip = true },
                new MatSpec { Source = "InnerCurl", Texture = "01_TootsieRolls__T_TootsieRolls.png", AlphaClip = true },
                new MatSpec { Source = "OuterCLower", Texture = "01_TootsieRolls__T_TootsieRolls.png", AlphaClip = true },
                new MatSpec { Source = "LowerOuterFine", Texture = "01_TootsieRolls__T_TootsieRolls.png", AlphaClip = true },
            },
        },
    };

    // One-shot auto-run: fires after every compile, does nothing once all
    // thirteen prefabs exist (or when the Temp FBX drop is gone).
    [InitializeOnLoadMethod]
    private static void AutoRun()
    {
        EditorApplication.delayCall += () =>
        {
            if (Application.productName != "HexLive") return;
            if (!File.Exists(Fbx)) return;
            if (Hairs.All(h => File.Exists(PrefabPath(h)))) return;
            Run(force: false);
        };
    }

    // Deliberately NOT under HexLive/Wear/ — sitting next to NewWearExtractor's
    // near-identically named "Extract New Wear (Force Re-Extract)" got the
    // wrong one clicked, which silently re-stamped all 8 garments instead.
    [MenuItem("HexLive/Hair/Extract Hair (Temp FBX)")]
    private static void RunMenu() => Run(force: false);

    [MenuItem("HexLive/Hair/Extract Hair (FORCE Re-Extract)")]
    private static void RunForceMenu() => Run(force: true);

    private static string PrefabPath(HairSpec h) => $"{ImportRoot}/{h.Name}/{h.Name}.prefab";

    private static void Run(bool force)
    {
        if (!File.Exists(Fbx))
        {
            Debug.LogError($"[Hair] FBX not found: {Fbx}");
            return;
        }

        // hair.fbx imports with Read/Write OFF. Object.Instantiate would still
        // clone such a mesh, but MeshSimplifier reads mesh.vertices directly and
        // silently gets NOTHING back — every decimated hair would come out
        // empty. Flip it on first (one slow reimport of a 306 MB FBX, and the
        // drop is gitignored so the .meta churn costs nothing).
        if (AssetImporter.GetAtPath(Fbx) is ModelImporter model && !model.isReadable)
        {
            model.isReadable = true;
            model.SaveAndReimport();
            Debug.Log("[Hair] enabled Read/Write on hair.fbx (needed by the simplifier)");
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        if (prefab == null)
        {
            Debug.LogError($"[Hair] can't load {Fbx}");
            return;
        }

        // Instantiate the rig once and index every hair renderer by spec key.
        var instance = Object.Instantiate(prefab);
        instance.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var renderers = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var r in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var key = Hairs.FirstOrDefault(h =>
                    (r.sharedMesh != null && r.sharedMesh.name.StartsWith(h.SourceKey)) ||
                    r.name.StartsWith(h.SourceKey))?.SourceKey;
                if (key != null)
                {
                    renderers[key] = r;
                }
            }

            var done = 0;
            var skipped = 0;
            var log = new System.Text.StringBuilder();
            foreach (var h in Hairs)
            {
                if (!force && File.Exists(PrefabPath(h)))
                {
                    skipped++;
                    continue;
                }

                if (ExtractHair(h, renderers, log))
                {
                    done++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Hair] extracted {done}, skipped (already built) {skipped}\n{log}");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static bool ExtractHair(
        HairSpec h,
        Dictionary<string, SkinnedMeshRenderer> renderers,
        System.Text.StringBuilder log)
    {
        if (!renderers.TryGetValue(h.SourceKey, out var reference) || reference.sharedMesh == null)
        {
            Debug.LogError($"[Hair] {h.SourceKey}: no renderer in {Fbx} — skipped");
            return false;
        }

        EnsureFolder($"{ImportRoot}/{h.Name}/Meshes");
        EnsureFolder($"{ImportRoot}/{h.Name}/Materials");

        var mesh = BuildMesh(h, reference.sharedMesh, out var before, out var after, out var note);
        var mats = new Dictionary<string, Material>();
        foreach (var spec in h.Materials)
        {
            ConfigureTexture(h, spec);
            mats[spec.Source] = BuildMaterial(h, spec);
        }

        var root = new GameObject(h.Name);
        try
        {
            BuildBonesAndRenderer(h, reference, mesh, mats, root);
            FillWearComponent(root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(h));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        // A simplifier that quietly achieves nothing looks identical to a
        // successful run in the log — so say the percentage out loud, and
        // warn when a mesh is still over budget.
        log.AppendLine($"  {h.Name,-16} {before,7} -> {after,7} verts ({100f * after / before,3:0}%), {mats.Count} mats — {note}");
        if (after > TargetVerts * 1.1f)
        {
            Debug.LogWarning($"[Hair] {h.Name}: {after} verts, still over the {TargetVerts} target ({note}).");
        }

        return true;
    }

    // Bring the mesh to ~TargetVerts (see Simplify for how, and why it has to
    // escalate). Anything already under budget is copied verbatim — most of the
    // drop lands here. Bone weights + bindposes ride along either way.
    // NOTE: `before` is Unity's vertexCount, which is HIGHER than the FBX's
    // unique-position count (the importer splits verts at UV/normal seams —
    // Mai Hair is 918 K in the file and 1.13 M once imported).
    private static Mesh BuildMesh(HairSpec h, Mesh source, out int before, out int after, out string note)
    {
        before = source.vertexCount;
        var path = $"{ImportRoot}/{h.Name}/Meshes/{h.Name}.mesh";

        Mesh built;
        if (before <= TargetVerts)
        {
            built = Object.Instantiate(source);
            note = "already under target";
        }
        else
        {
            built = Simplify(source, before, out note);
        }

        built.name = h.Name;
        after = built.vertexCount;

        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            // Keep the GUID stable across re-runs — prefab refs survive.
            EditorUtility.CopySerialized(built, existing);
            Object.DestroyImmediate(built);
            return existing;
        }

        AssetDatabase.CreateAsset(built, path);
        return built;
    }

    // Preservation passes, gentlest first. This ESCALATES instead of trusting
    // one setting, because the gentle pass is a no-op on this geometry: a DAZ
    // hair card is a flat subdivided strip, so nearly every edge is a border
    // edge and every card is its own UV island — with borders AND UV seams
    // pinned, almost no vertex is collapsible and the simplifier quietly
    // returns the mesh at ~100% (measured: Bendine 99%, Chunky 97%, Mai 100%).
    // Freeing the border lets it collapse ALONG the strip, which is exactly
    // the subdivision we want gone; freeing UV seams is the last resort and
    // can smear a card's UVs, so it is only used if the mesh is still over.
    private static readonly (bool Border, bool UvSeam, string Label)[] Passes =
    {
        (true,  true,  "borders+UV seams pinned"),
        (false, true,  "borders free"),
        (false, false, "borders+UV seams free"),
    };

    private static Mesh Simplify(Mesh source, int before, out string note)
    {
        Mesh best = null;
        var bestCount = int.MaxValue;
        var bestLabel = "";

        foreach (var pass in Passes)
        {
            var options = SimplificationOptions.Default;
            options.PreserveBorderEdges = pass.Border;
            options.PreserveUVSeamEdges = pass.UvSeam;
            options.PreserveUVFoldoverEdges = true;

            var simplifier = new MeshSimplifier { SimplificationOptions = options };
            simplifier.Initialize(source);   // captures bone weights + bindposes
            simplifier.SimplifyMesh(Mathf.Clamp01((float)TargetVerts / before));
            var candidate = simplifier.ToMesh();   // and restores them

            if (candidate.vertexCount < bestCount)
            {
                if (best != null)
                {
                    Object.DestroyImmediate(best);
                }

                best = candidate;
                bestCount = candidate.vertexCount;
                bestLabel = pass.Label;
            }
            else
            {
                Object.DestroyImmediate(candidate);
            }

            // Close enough — don't pay for a harsher pass we don't need.
            if (bestCount <= TargetVerts * 1.1f)
            {
                break;
            }
        }

        note = bestCount > TargetVerts * 1.1f
            ? $"FLOORED at {bestLabel} — could not reach target"
            : bestLabel;
        return best;
    }

    // Alpha-tested hair is unforgiving about import settings. Without
    // alphaIsTransparency the composited RGB bleeds black into the fully
    // transparent gaps and every strand gets a dark fringe; without
    // mipMapsPreserveCoverage the alpha averages below the cutoff in the lower
    // mips and the hair thins out — then vanishes — as the camera pulls back.
    // The coverage reference must be the SAME 0.42 the material clips at.
    private static void ConfigureTexture(HairSpec h, MatSpec spec)
    {
        if (spec.Texture == null)
        {
            return;
        }

        var path = $"{ImportRoot}/{h.Name}/Textures/{spec.Texture}";
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
        {
            return;
        }

        var changed = false;
        if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; changed = true; }
        if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
        if (spec.AlphaClip)
        {
            if (!importer.mipMapsPreserveCoverage) { importer.mipMapsPreserveCoverage = true; changed = true; }
            if (!Mathf.Approximately(importer.alphaTestReferenceValue, 0.42f))
            {
                importer.alphaTestReferenceValue = 0.42f;
                changed = true;
            }
        }

        if (changed)
        {
            importer.SaveAndReimport();
        }
    }

    // URP Lit with the shipped-hair recipe (see OnyxHair/HairBase.mat):
    // double-sided, zero smoothness, alpha-clipped off the composited alpha,
    // parked in the AlphaTest queue.
    private static Material BuildMaterial(HairSpec h, MatSpec spec)
    {
        var path = $"{ImportRoot}/{h.Name}/Materials/{Sanitize(spec.Source)}.mat";
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

        mat.SetColor("_BaseColor", spec.Texture != null ? Color.white : spec.Color);
        mat.SetFloat("_Smoothness", 0f);
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        if (spec.Texture != null)
        {
            var texPath = $"{ImportRoot}/{h.Name}/Textures/{spec.Texture}";
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                Debug.LogWarning($"[Hair] {h.Name}: texture not imported yet — {texPath}");
            }

            mat.SetTexture("_BaseMap", tex);
            mat.SetTexture("_MainTex", tex);
        }

        if (spec.AlphaClip)
        {
            mat.SetFloat("_Surface", 0f);
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.42f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }
        else
        {
            mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "Opaque");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }

        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Clone the reference skeleton subtree under "hip" (pruned to the bones
    // this hair skins to + their ancestors) and add the renderer child.
    // Wear.Construct finds "hip" by name and stitches the subtree onto the body.
    private static void BuildBonesAndRenderer(
        HairSpec h, SkinnedMeshRenderer reference, Mesh mesh,
        Dictionary<string, Material> mats, GameObject root)
    {
        var srcHip = FindByName(reference.rootBone != null
            ? reference.rootBone.root
            : reference.transform.root, "hip");
        if (srcHip == null)
        {
            throw new IOException($"{h.SourceKey}: no 'hip' bone in the source FBX");
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

        var meshGo = new GameObject(h.Name + " Mesh");
        meshGo.transform.SetParent(root.transform, false);
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = reference.bones
            .Select(b => b != null && clones.TryGetValue(b.name, out var c) ? c : hipClone)
            .ToArray();
        smr.rootBone = reference.rootBone != null &&
            clones.TryGetValue(reference.rootBone.name, out var rb)
            ? rb
            : hipClone;
        smr.localBounds = reference.localBounds;
        smr.sharedMaterials = ResolveMaterials(h, reference, mats);
    }

    // Match the FBX submesh materials to our specs BY NAME. hair.fbx holds 115
    // materials and names repeat across hairstyles ("Scalp", "Cap", "Bangs1"),
    // so Unity may hand back a trailing-index variant — strip it and retry,
    // then fall back to submesh order rather than dropping the slot.
    private static Material[] ResolveMaterials(
        HairSpec h, SkinnedMeshRenderer reference, Dictionary<string, Material> mats)
    {
        var source = reference.sharedMaterials;
        var result = new Material[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var name = source[i] != null ? source[i].name.Replace(" (Instance)", "") : "";
            if (mats.TryGetValue(name, out var mapped))
            {
                result[i] = mapped;
                continue;
            }

            var trimmed = name.TrimEnd("0123456789 ".ToCharArray());
            if (mats.TryGetValue(trimmed, out mapped))
            {
                result[i] = mapped;
                continue;
            }

            if (i < h.Materials.Length && mats.TryGetValue(h.Materials[i].Source, out mapped))
            {
                Debug.LogWarning($"[Hair] {h.Name}: material '{name}' not in spec — fell back to submesh order ({h.Materials[i].Source})");
                result[i] = mapped;
                continue;
            }

            Debug.LogWarning($"[Hair] {h.Name}: material '{name}' unresolved — using first spec material");
            result[i] = mats.Values.First();
        }

        return result;
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

    // Hair is spawned straight from BodyBones.Construct, never through
    // Equip — it owns no slot and no layer, and per-actor fit scales are
    // added later by the WardrobeTest scene if a girl needs one.
    private static void FillWearComponent(GameObject root)
    {
        var wear = root.AddComponent<Wear>();
        var so = new SerializedObject(wear);
        so.FindProperty("configs").arraySize = 0;
        so.FindProperty("slots").arraySize = 0;
        so.FindProperty("noHideUnderwearSlots").arraySize = 0;
        so.FindProperty("layer").enumValueIndex = (int)VisualWearLayer.Wear;
        so.FindProperty("gender").enumValueIndex = (int)VisualGender.Female;
        so.ApplyModifiedPropertiesWithoutUndo();
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
