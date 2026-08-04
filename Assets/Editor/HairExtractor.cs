#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

// ---------------------------------------------------------------------------
//  Hairstyle extractor ("hair" drop of 2026-08).
//
//  Assets/Temp/hair.fbx is one DAZ export of Genesis3Female wearing thirteen
//  hairstyles at once, TWELVE of which we ship (see the Mai Hair note in the
//  table below). This tool pulls each hair mesh out and assembles the
//  standard hair assets, which are ordinary Wear prefabs (spec Â§31B.2) â€” the
//  same contract the shipped OnyxHair / ShilohHair / JelikaHair follow:
//
//    Assets/ImportedActors/Wear/<Name>/Meshes/<Name>.mesh      (verbatim copy)
//    Assets/ImportedActors/Wear/<Name>/Materials/<mat>.mat     (created ONCE)
//    Assets/ImportedActors/Wear/<Name>/Textures/*.png          (prepared offline)
//    Assets/ImportedActors/Wear/<Name>/<Name>.prefab           (Wear prefab)
//
//  MESHES AND THE PREFAB ARE REGENERATED; MATERIALS AND TEXTURES ARE NOT.
//  An existing .mat is reused untouched and texture import settings are left
//  alone, because the hair look is tuned by hand in the editor. A Force
//  Re-Extract is therefore safe to run at any time. Delete a .mat to have it
//  re-authored from scratch.
//
//  To put one on a girl: drop the prefab into the `hair` field of her
//  BodyBones (Assets/Resources/HexLive/Actors/<Girl>.prefab). BodyBones
//  spawns it in Construct and Wear.Construct stitches every hair bone onto
//  the matching body bone by NAME, so any hair fits any actor â€” all thirteen
//  skin to the shared Genesis3 head/neck/face bones.
//
//  TEXTURES are NOT built here. DAZ ships colour and opacity as two separate
//  images and DAZ FBX does not embed either, so they are composited offline
//  (diffuse RGB + transparency map -> alpha) straight from the DAZ library
//  into each hair's Textures/ folder, capped at 1024. That is why every
//  MatSpec below names a ready .png instead of a source jpg pair.
//
//  NO DECIMATION. Meshes are copied verbatim — see BuildMesh for why the
//  simplifier had to go. Weight is fine as-is: the heaviest is BendineHair at
//  328 K vs the shipped OnyxHair's 270 K.
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
    // ⚠️ Причёски живут в СВОЕЙ папке, не в Wear. Когда-то экстрактор писал их
    // рядом с одеждой, и в проекте завелись две копии каждой причёски: в Hair
    // — настроенные руками (пороги прозрачности, шапочки в прозрачном режиме),
    // а в Wear — пустые, без текстур. Девушки ссылались на первые, тестовая
    // сцена показывала вторые, и причёски в ней выглядели белыми.
    private const string ImportRoot = "Assets/ImportedActors/Hair";
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
                // The FBX left TransparentColor unconnected on these four, so
                // they came out fully opaque — solid card rectangles through
                // the hair. Same product, same UV space as the strands below,
                // so they take the strand cut-out too.
                new MatSpec { Source = "Side2", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front2", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "Back", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
                new MatSpec { Source = "Bangs2", Texture = "08OOTBendineHair__OOTUtilityBendineHairT.png", AlphaClip = true },
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
                // Bangs2/Back/Front2/Side2 had no TransparentColor in the FBX —
                // they take the strand cut-out (see BendineHair). Hairbands is
                // genuinely opaque cloth and stays that way.
                new MatSpec { Source = "Bangs2", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Hairbands", Texture = "OOTCloth_08B.png" },
                new MatSpec { Source = "Bangs1", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Cap", Texture = "08OOTChunkyCap__OOTUtilityChunkyCapT.png", AlphaClip = true },
                new MatSpec { Source = "Back", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "BackUnder", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front1", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Side1", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Front2", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
                new MatSpec { Source = "Side2", Texture = "08OOTChunkyHair__OOTUtilityChunkyHairT.png", AlphaClip = true },
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
        // â€” 3.8x the shipped OnyxHair. Dropped 2026-08-01 rather than shipped
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

    // Deliberately NOT under HexLive/Wear/ â€” sitting next to NewWearExtractor's
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
        // silently gets NOTHING back â€” every decimated hair would come out
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
            Debug.LogError($"[Hair] {h.SourceKey}: no renderer in {Fbx} â€” skipped");
            return false;
        }

        EnsureFolder($"{ImportRoot}/{h.Name}/Meshes");
        EnsureFolder($"{ImportRoot}/{h.Name}/Materials");

        var mesh = BuildMesh(h, reference.sharedMesh, out _, out var after);
        var boneCount = 0;
        var mats = new Dictionary<string, Material>();
        foreach (var spec in h.Materials)
        {
            mats[spec.Source] = BuildMaterial(h, spec);
        }

        var root = new GameObject(h.Name);
        try
        {
            BuildBonesAndRenderer(h, reference, mesh, mats, root, out boneCount);
            FillWearComponent(root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(h));
        }
        finally
        {
            Object.DestroyImmediate(root);
        }

        log.AppendLine($"  {h.Name,-16} {after,7} verts, {boneCount} bones, {mats.Count} mats");
        return true;
    }

    // Copy the imported mesh VERBATIM. Decimation was tried and REMOVED
    // (2026-08-01): UnityMeshSimplifier shreds hair. A card is a flat strip
    // whose silhouette is the alpha cut-out, so collapsing along it tears
    // holes and detaches strands â€” visibly "ÐºÐ»Ð¾Ñ‡ÑŒÑ" in the wardrobe test even
    // at 97% retention (LeonyPonytail). The undecimated ones look right, and
    // the weight is fine anyway: the heaviest here is BendineHair at 328 K vs
    // the shipped OnyxHair's 270 K, and the one true outlier (Mai Hair) is
    // dropped rather than simplified. Do not re-add a simplifier pass.
    // NOTE: vertexCount is HIGHER than the FBX's unique-position count â€” the
    // importer splits verts at UV/normal seams.
    private static Mesh BuildMesh(HairSpec h, Mesh source, out int before, out int after)
    {
        before = source.vertexCount;
        var path = $"{ImportRoot}/{h.Name}/Meshes/{h.Name}.mesh";

        var built = Object.Instantiate(source);
        built.name = h.Name;
        after = built.vertexCount;

        // Replace the asset outright rather than EditorUtility.CopySerialized
        // into it. CopySerialized looked attractive (stable GUID across
        // re-runs) but it leaves a Mesh internally inconsistent when the old
        // asset held a DIFFERENT mesh: the vertex-channel layout is not
        // rebuilt, and at runtime Unity refuses the renderer with "does not
        // match the expected mesh data size and vertex stride". It bit exactly
        // the five hairstyles whose asset had previously held a DECIMATED
        // mesh, while the ones that were always a straight copy rendered fine
        // — which is what made it look like a leftover decimation bug. GUID
        // churn is harmless here: the only thing referencing this mesh is the
        // prefab written a few lines later in the same run.
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        AssetDatabase.CreateAsset(built, path);

        // Settle the asset BEFORE anything references it, then hand back the
        // instance the AssetDatabase actually holds — not the in-memory object
        // we just created. Skipping this wrote a perfectly valid .mesh (right
        // GUID, right fileID 4300000) that Unity had never imported, so the
        // prefab's reference resolved to nothing and the renderer showed
        // "Missing (Mesh)" with no error anywhere. Delete-then-create needs the
        // synchronous import; CreateAsset alone does not queue one in time.
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(
            path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        var settled = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (settled == null)
        {
            throw new IOException($"{h.Name}: mesh asset did not import at {path}");
        }

        return settled;
    }


    // MATERIALS ARE HAND-TUNED AND OFF LIMITS ON RE-EXTRACT.
    //
    // An existing .mat is returned untouched — no shader reset, no property
    // writes, no texture re-assignment — and the texture importers are not
    // touched at all. The look of hair (opaque, matte, no cut-out) is an art
    // decision made in the editor, not something this tool should keep
    // restamping: a Force Re-Extract must be safe to run without wiping that
    // work. Only a MISSING material is authored here, and only enough to be a
    // sane starting point before it is tuned by hand.
    private static Material BuildMaterial(HairSpec h, MatSpec spec)
    {
        var path = $"{ImportRoot}/{h.Name}/Materials/{Sanitize(spec.Source)}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null)
        {
            return existing;
        }

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", spec.Texture != null ? Color.white : spec.Color);
        mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        mat.SetFloat("_Smoothness", 0f);
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Surface", 0f);
        mat.SetFloat("_AlphaClip", 0f);
        mat.SetOverrideTag("RenderType", "Opaque");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;

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

        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // Rebuild the hair's own bone subtree and add the renderer child.
    // Wear.Construct finds "hip" by name and stitches the subtree onto the body.
    //
    // Cloning is driven by the mesh's OWN bone list and keyed by Transform
    // IDENTITY, walking each bone up to hip. The first version walked the
    // hierarchy down from hip and matched by NAME with a silent
    // `?: hipClone` fallback — and that fallback fired constantly: a hair
    // skinned to the Genesis face rig (lowerJaw, brows, cheeks) came out with
    // only 8 of its 20 bones, the other 13 all collapsed onto the hip clone.
    // Every vertex weighted to a missing bone then got dragged to the pelvis,
    // which in game reads as the hair melting down over the face. Nothing
    // logged. Never resolve skeleton bones by name here, and never silently
    // substitute one: an unresolved bone is a hard error.
    private static void BuildBonesAndRenderer(
        HairSpec h, SkinnedMeshRenderer reference, Mesh mesh,
        Dictionary<string, Material> mats, GameObject root, out int boneCount)
    {
        var srcHip = FindOwnHip(reference);
        if (srcHip == null)
        {
            throw new IOException($"{h.SourceKey}: none of this mesh's bones sit under a 'hip'");
        }

        var clones = new Dictionary<Transform, Transform>();
        foreach (var bone in reference.bones)
        {
            if (bone != null)
            {
                CloneChain(bone, srcHip, root.transform, clones, h);
            }
        }

        if (reference.rootBone != null)
        {
            CloneChain(reference.rootBone, srcHip, root.transform, clones, h);
        }

        var meshGo = new GameObject(h.Name + " Mesh");
        meshGo.transform.SetParent(root.transform, false);
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = reference.bones.Select(b => clones[b]).ToArray();
        smr.rootBone = reference.rootBone != null ? clones[reference.rootBone] : clones[srcHip];
        smr.localBounds = reference.localBounds;
        smr.sharedMaterials = ResolveMaterials(h, reference, mats);
        boneCount = clones.Count;
    }

    // THIS mesh's hip — found by walking UP from its own bones, never by
    // searching the instance by name. hair.fbx is one export of a figure
    // wearing thirteen conformed hairstyles, and every one of them carries a
    // full skeleton copy: the file holds FOURTEEN nodes called "hip". A
    // by-name search returns the body's, whose subtree a hairstyle's bones do
    // not live in — which is precisely how the old mapping ended up matching
    // only the bones whose names the two skeletons share (head, neck*, chest*)
    // and dumping the rest on a fallback.
    private static Transform FindOwnHip(SkinnedMeshRenderer renderer)
    {
        foreach (var probe in new[] { renderer.rootBone }.Concat(renderer.bones))
        {
            for (var t = probe; t != null; t = t.parent)
            {
                if (t.name == "hip")
                {
                    return t;
                }
            }
        }

        return null;
    }

    // Clone `bone` and every ancestor up to (and including) `srcHip`, memoised
    // by source Transform so a shared ancestor is only cloned once. Returns the
    // clone, parented under its own parent's clone (hip goes under the root).
    private static Transform CloneChain(
        Transform bone, Transform srcHip, Transform root,
        Dictionary<Transform, Transform> clones, HairSpec h)
    {
        if (clones.TryGetValue(bone, out var existing))
        {
            return existing;
        }

        Transform parentClone;
        if (bone == srcHip)
        {
            parentClone = root;
        }
        else if (bone.parent == null)
        {
            // Walked past hip without meeting it — the bone hangs off a
            // different skeleton, which would silently mis-skin the hair.
            throw new IOException(
                $"{h.SourceKey}: bone '{bone.name}' is not under the 'hip' this mesh binds to");
        }
        else
        {
            parentClone = CloneChain(bone.parent, srcHip, root, clones, h);
        }

        var clone = new GameObject(bone.name).transform;
        clone.SetParent(parentClone, false);
        clone.localPosition = bone.localPosition;
        clone.localRotation = bone.localRotation;
        clone.localScale = bone.localScale;
        clones[bone] = clone;
        return clone;
    }

    // Match the FBX submesh materials to our specs BY NAME. hair.fbx holds 115
    // materials and names repeat across hairstyles ("Scalp", "Cap", "Bangs1"),
    // so Unity may hand back a trailing-index variant â€” strip it and retry,
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
                Debug.LogWarning($"[Hair] {h.Name}: material '{name}' not in spec â€” fell back to submesh order ({h.Materials[i].Source})");
                result[i] = mapped;
                continue;
            }

            Debug.LogWarning($"[Hair] {h.Name}: material '{name}' unresolved â€” using first spec material");
            result[i] = mats.Values.First();
        }

        return result;
    }

    // Hair is spawned straight from BodyBones.Construct, never through
    // Equip â€” it owns no slot and no layer, and per-actor fit scales are
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




