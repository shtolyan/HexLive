#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MagicaCloth2;

// ---------------------------------------------------------------------------
//  Magica Cloth 2 physics setup for HexLive girls.
//
//  Breast jiggle : BoneSpring on lPectoral / rPectoral of the body prefabs.
//  Hair (LowPony): BoneSpring on Tail / lBangs / rBangs of the hair prefab.
//
//  BoneSpring is Magica's mode for a small number of bones (breasts, short
//  strands). It's the "nicer Dynamic Bone": movement inertia (jiggle on walk),
//  angle-limited so it stays subtle, spring return, plus distance culling so
//  far characters stop simulating (cheap for crowds / mobile).
//
//  IMPORTANT: Jana/Marta are prefab VARIANTS, so we add the component with
//  PrefabUtility.ApplyAddedComponent (SaveAsPrefabAsset silently drops added
//  components on variants). Molly is a regular prefab; same code path works.
//
//  Values are a tuned STARTING POINT — tweak live in the MagicaCloth inspector.
//  Re-running is idempotent (skips a prefab that already has a MagicaCloth).
//
//  Menu:  HexLive/Physics/*
// ---------------------------------------------------------------------------
public static class BodyHairPhysicsSetup
{
    static readonly string[] BodyPrefabs =
    {
        "Assets/Resources/HexLive/Actors/Jana.prefab",
        "Assets/Resources/HexLive/Actors/Molly.prefab",
        "Assets/Resources/HexLive/Actors/Marta.prefab",
        "Assets/Resources/HexLive/Actors/Jolly.prefab",
    };

    [MenuItem("HexLive/Physics/Setup Breast Jiggle (all girls)")]
    static void SetupBreasts()
    {
        int ok = 0;
        foreach (var p in BodyPrefabs)
            if (Setup(p, new[] { "lPectoral", "rPectoral" }, breast: true)) ok++;
        AssetDatabase.SaveAssets();
        Debug.Log($"[Physics] Breast jiggle: {ok} prefab(s) updated.");
    }

    // --- Hair -------------------------------------------------------------
    //
    // One row per STRAND-BONE GROUP a hairstyle owns; roots only, BoneSpring
    // walks each chain down from there. Two deflection budgets, both already
    // approved on the two hairstyles that shipped with springs:
    //
    //  * CHAIN (OnyxHair Tail1->Tail2->Tail3, ~0.26 m a joint): the budget is
    //    split across the joints instead of spent on one, so 14 deg x 3 lands
    //    on Marta's ~40 deg of total sway ("немножечко"), not triple it. A long
    //    chain also needs a firmer pull back to the animated pose and less
    //    world inertia, or a walk cycle whips the tail around.
    //  * STUB (LowPonytail's three childless 0.16 m bangs): one joint, so it
    //    gets the whole ~40 deg itself — the defaults in Configure.
    //
    // Eight hairstyles are deliberately ABSENT: AdellHair, Bob3Hair, Hair07,
    // JelikaHair_32434, JenniferHair, LoonaHair, Neu09Hair, TootsieRollHair are
    // skinned to head/neck/chest/shoulder bones alone and own no strand bone at
    // all, so BoneSpring has nothing to move. They sway through MeshCloth
    // instead — the GarmentCloth component, the same path skirts use.
    struct HairSpring
    {
        public string Hair;      // folder + prefab name under HairRoot
        public string[] Roots;   // strand-bone roots for this group
        public bool Chain;       // true = multi-joint chain, false = single stub
    }

    const string HairRoot = "Assets/ImportedActors/Hair";

    static readonly HairSpring[] HairSprings =
    {
        // Front tips only — the rest of the cap rides the skull.
        new HairSpring { Hair = "AsukaHair", Chain = false,
                         Roots = new[] { "Front Tips LEFT", "Front Tips RIGHT" } },

        // 18 childless strands: six front locks a side plus three back ones.
        new HairSpring { Hair = "BendineHair", Chain = false, Roots = new[]
                       { "lFront1", "lFront2", "lFront3", "lFront4", "lFront5", "lFront6",
                         "rFront1", "rFront2", "rFront3", "rFront4", "rFront5", "rFront6",
                         "lBack1", "lBack2", "lBack3", "rBack1", "rBack2", "rBack3" } },

        // Two pigtails, three joints each.
        new HairSpring { Hair = "ChunkyHair", Chain = true, Roots = new[] { "lTail", "rTail" } },

        // 11 childless locks hanging off the head.
        new HairSpring { Hair = "EilisHair", Chain = false, Roots = new[]
                       { "Right1", "Right2", "Right3", "Right4", "Left1", "Left2",
                         "BackRight1", "BackRight2", "BackRight3", "BackLeft1", "BackLeft2" } },

        // Ponytail chain (4 joints) and the bangs/side stubs are separate
        // groups: one component cannot hold two deflection budgets.
        new HairSpring { Hair = "LeonyPonytail", Chain = true, Roots = new[] { "PonytailBase" } },
        new HairSpring { Hair = "LeonyPonytail", Chain = false,
                         Roots = new[] { "lBangs", "rBangs", "lSide", "rSide" } },

        // The two that shipped with springs already — listed so the pass is
        // complete and self-documenting; Setup skips a prefab that has one.
        new HairSpring { Hair = "LowPonytail", Chain = false,
                         Roots = new[] { "Tail", "lBangs", "rBangs" } },
        new HairSpring { Hair = "OnyxHair", Chain = true, Roots = new[] { "Tail1" } },
    };

    [MenuItem("HexLive/Physics/Setup Hair Spring (all hairstyles)")]
    static void SetupHairSprings()
    {
        int done = 0, skipped = 0;
        foreach (var hair in HairSprings.Select(h => h.Hair).Distinct())
        {
            var path = $"{HairRoot}/{hair}/{hair}.prefab";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) { Debug.LogError($"[Physics] not found: {path}"); continue; }

            var groups = HairSprings.Where(h => h.Hair == hair).ToList();
            int already = asset.GetComponentsInChildren<MagicaCloth>(true).Length;
            if (already > 0)
            {
                Debug.Log($"[Physics] {hair}: {already} MagicaCloth already there " +
                          $"({groups.Count} group(s) expected) — skipped, delete them to redo.");
                skipped++;
                continue;
            }

            bool any = false;
            foreach (var g in groups)
            {
                any |= Setup(path, g.Roots, breast: false,
                             limitAngle:       g.Chain ? 14f  : (float?)null,
                             restoreStiffness: g.Chain ? 0.30f : (float?)null,
                             worldInertia:     g.Chain ? 0.80f : (float?)null,
                             allowSecond: true);
            }

            // Simulated bones leave the skinned silhouette, so the renderer's
            // own bounds no longer describe it and the hair pops out of view
            // at the edge of the screen — the trap OnyxHair hit first.
            if (any) { ConfigureHairRenderer(path); done++; }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[Physics] Hair springs: {done} hairstyle(s) set up, {skipped} already had one.");
    }

    // internal: HairSwaySetup reuses this once it has given a boneless
    // hairstyle the chain it was missing.
    internal static bool Setup(string path, string[] boneNames, bool breast,
                      float? limitAngle = null, float? restoreStiffness = null,
                      float? worldInertia = null, bool allowSecond = false)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null) { Debug.LogError($"[Physics] not found: {path}"); return false; }
        if (allowSecond == false && asset.GetComponent<MagicaCloth>() != null)
        {
            Debug.Log($"[Physics] {System.IO.Path.GetFileName(path)}: already has MagicaCloth — skipped (delete it to redo).");
            return false;
        }

        // Instantiate, add + configure on the instance, then APPLY the added
        // component back to the asset (correct for prefab variants).
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
        try
        {
            var bones = boneNames.Select(n => FindDeep(inst.transform, n))
                                 .Where(t => t != null).ToList();
            if (bones.Count == 0) { Debug.LogError($"[Physics] {path}: none of [{string.Join(",", boneNames)}] found"); return false; }

            var mc = inst.AddComponent<MagicaCloth>();
            Configure(mc.SerializeData, bones, breast,
                      limitAngle, restoreStiffness, worldInertia);

            PrefabUtility.ApplyAddedComponent(mc, path, InteractionMode.AutomatedAction);
            Debug.Log($"[Physics] {System.IO.Path.GetFileName(path)} -> BoneSpring on: {string.Join(", ", bones.Select(b => b.name))}");
            return true;
        }
        finally { Object.DestroyImmediate(inst); }
    }

    static void Configure(ClothSerializeData sd, List<Transform> bones, bool breast,
                          float? limitAngle = null, float? restoreStiffness = null,
                          float? worldInertia = null)
    {
        sd.clothType      = ClothProcess.ClothType.BoneSpring;
        sd.rootBones      = bones;
        sd.connectionMode = RenderSetupData.BoneConnectionMode.Line;

        sd.gravity = breast ? 1.0f : 3.0f;
        sd.damping = new CurveSerializeData(breast ? 0.25f : 0.10f);
        sd.radius  = new CurveSerializeData(breast ? 0.06f : 0.02f);

        sd.angleRestorationConstraint.useAngleRestoration = true;
        sd.angleRestorationConstraint.stiffness =
            new CurveSerializeData(restoreStiffness ?? (breast ? 0.60f : 0.20f));
        sd.angleLimitConstraint.useAngleLimit = true;
        sd.angleLimitConstraint.limitAngle =
            new CurveSerializeData(limitAngle ?? (breast ? 5f : 40f));
        sd.springConstraint.useSpring   = true;
        sd.springConstraint.springPower = breast ? 0.15f : 0.20f;

        sd.inertiaConstraint.worldInertia = worldInertia ?? (breast ? 0.5f : 1.0f);
        sd.cullingSettings.cameraCullingMode     = CullingSettings.CameraCullingMode.AnimatorLinkage;
        sd.cullingSettings.distanceCullingLength = new CheckSliderSerializeData(true, 25f);
    }

    // updateWhenOffscreen makes Unity recompute the bounds from the simulated
    // mesh every frame, so no hand-authored localBounds is needed (OnyxHair
    // carries one from before this was table-driven; harmless, and skipped).
    static void ConfigureHairRenderer(string path)
    {
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;

                var serializedRenderer = new SerializedObject(smr);
                var smallMeshCulling = serializedRenderer.FindProperty("m_SmallMeshCulling");
                if (smallMeshCulling != null)
                {
                    smallMeshCulling.boolValue = false;
                    serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform c in root)
        {
            var r = FindDeep(c, name);
            if (r) return r;
        }
        return null;
    }
}
#endif
