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

    [MenuItem("HexLive/Physics/Setup Hair Spring (LowPonytail)")]
    static void SetupHair()
    {
        Setup("Assets/ImportedActors/Wear/LowPonytail/LowPonytail.prefab",
              new[] { "Tail", "lBangs", "rBangs" }, breast: false);
        AssetDatabase.SaveAssets();
    }

    // Jolly's OnyxHair is one ponytail CHAIN (Tail1 -> Tail2 -> Tail3, ~0.26 m
    // per joint = 0.79 m of hair), where LowPonytail is three childless 0.16 m
    // stubs hanging off the head. Same recipe, but the deflection budget is
    // split across the joints instead of spent on one: 14 deg x 3 joints lands
    // on Marta's ~40 deg of total sway ("немножечко"), not triple it. The
    // longer chain also needs a firmer pull back to the animated pose and less
    // world inertia, or a walk cycle whips the tail around.
    [MenuItem("HexLive/Physics/Setup Hair Spring (OnyxHair)")]
    static void SetupOnyxHair()
    {
        const string path = "Assets/ImportedActors/Wear/OnyxHair/OnyxHair.prefab";
        Setup(path, new[] { "Tail1" }, breast: false,
              limitAngle: 14f, restoreStiffness: 0.30f, worldInertia: 0.80f);
        ConfigureOnyxHairRenderer(path);
        AssetDatabase.SaveAssets();
    }

    static bool Setup(string path, string[] boneNames, bool breast,
                      float? limitAngle = null, float? restoreStiffness = null,
                      float? worldInertia = null)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (asset == null) { Debug.LogError($"[Physics] not found: {path}"); return false; }
        if (asset.GetComponent<MagicaCloth>() != null)
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

    static void ConfigureOnyxHairRenderer(string path)
    {
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;
                smr.localBounds = new Bounds(new Vector3(0f, 0.3f, -0.18f), new Vector3(0.56f, 1.44f, 0.96f));

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
