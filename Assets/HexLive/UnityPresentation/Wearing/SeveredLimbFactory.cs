using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Spec §50: a severed limb is carved from its owner's one canonical FBX body.
// Fresh drops use a lightweight pose clone: copy only the already-evaluated
// transform hierarchy, restore the distal scale on that invisible clone, bake
// the skinned mesh, slice the matching bone-weight region, then destroy the
// clone before the next render. The live actor is never restored, re-dressed or
// otherwise mutated. Restored/late drops use the same owner's readable FBX in a
// reference pose at their persisted junction.
public static class SeveredLimbFactory
{
    // Which bone sub-tree each severed zone carved off. Matches NpcActorView's
    // collapsed chain so the dropped mesh is exactly what vanished on the body.
    private static readonly Dictionary<string, string> ZoneDistalBone = new()
    {
        ["ArmL"] = "lForearmBend",
        ["ArmR"] = "rForearmBend",
        ["LegL"] = "lShin",
        ["LegR"] = "rShin"
    };

    // Used for a restored object or when a current pose cannot safely be
    // captured. The caller anchors and grounds this reference-pose visual.
    public static GameObject BuildReference(NpcActorView owner, string variant)
    {
        if (!TryResolveSource(owner, variant, out var skin, out var distalBoneName))
        {
            return null;
        }

        var source = skin.sharedMesh;
        if (!TryBuildLimbMask(source, skin.bones, distalBoneName, out var vertexInLimb))
        {
            return null;
        }

        var limbMesh = SliceMesh(source, source, vertexInLimb);
        return limbMesh != null
            ? BuildVisual(limbMesh, variant, owner.SkinTint, recenter: true)
            : null;
    }

    // Captures the owner's final animated/procedural pose. The returned object
    // is already in world space and overlays the limb's former location; its
    // caller reparents it with worldPositionStays=true so it remains there.
    public static GameObject BuildFromCurrentPose(NpcActorView owner, string variant)
    {
        if (!TryResolveSource(owner, variant, out var skin, out var distalBoneName) ||
            !owner.TryGetSeveredLimbOriginalScale(variant, out var originalScale))
        {
            return null;
        }

        var source = skin.sharedMesh;
        if (!TryBuildLimbMask(source, skin.bones, distalBoneName, out var vertexInLimb))
        {
            return null;
        }

        GameObject poseClone = null;
        Mesh posedMesh = null;
        try
        {
            if (!TryClonePoseHierarchy(owner.SeveredLimbPoseRoot, skin,
                    distalBoneName, originalScale, out poseClone, out var clonedSkin))
            {
                return null;
            }

            posedMesh = new Mesh { name = $"{source.name}_{variant}_posed" };
            clonedSkin.BakeMesh(posedMesh, false);
            if (posedMesh.vertexCount != source.vertexCount)
            {
                return null;
            }

            var limbMesh = SliceMesh(source, posedMesh, vertexInLimb);
            if (limbMesh == null)
            {
                return null;
            }

            var visual = BuildVisual(limbMesh, variant, owner.SkinTint, recenter: false);
            // BakeMesh(false) returns renderer-local geometry. Reusing the
            // source renderer's world TRS puts every sliced vertex precisely
            // where that skin vertex was in the evaluated body pose.
            visual.transform.SetPositionAndRotation(
                skin.transform.position, skin.transform.rotation);
            visual.transform.localScale = skin.transform.lossyScale;
            return visual;
        }
        finally
        {
            // The pose clone never survives into another rendered frame. In
            // play mode Destroy is processed before the next render; edit-mode
            // callers (gates/tools) need immediate cleanup.
            DestroyTransient(posedMesh);
            DestroyTransient(poseClone);
        }
    }

    private static bool TryResolveSource(
        NpcActorView owner,
        string variant,
        out SkinnedMeshRenderer skin,
        out string distalBoneName)
    {
        skin = null;
        distalBoneName = string.Empty;
        if (owner == null || string.IsNullOrEmpty(variant) ||
            !ZoneDistalBone.TryGetValue(variant, out distalBoneName))
        {
            return false;
        }

        skin = owner.PrimaryBodySkin;
        return skin != null && skin.sharedMesh != null && skin.sharedMesh.isReadable;
    }

    private static bool TryBuildLimbMask(
        Mesh mesh,
        Transform[] bones,
        string distalBoneName,
        out bool[] vertexInLimb)
    {
        vertexInLimb = null;
        var distalRoot = FindBone(bones, distalBoneName);
        if (distalRoot == null)
        {
            return false;
        }

        var inLimb = new bool[bones.Length];
        var any = false;
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            if (bone != null && (bone == distalRoot || bone.IsChildOf(distalRoot)))
            {
                inLimb[i] = true;
                any = true;
            }
        }

        if (!any)
        {
            return false;
        }

        // A vertex belongs to the limb if its dominant influence belongs to
        // the distal sub-tree. GetAllBoneWeights also handles one-influence
        // welded FBX meshes; legacy mesh.boneWeights silently returned empty.
        var bonesPerVertex = mesh.GetBonesPerVertex();
        var allWeights = mesh.GetAllBoneWeights();
        try
        {
            if (bonesPerVertex.Length != mesh.vertexCount)
            {
                return false;
            }

            vertexInLimb = new bool[mesh.vertexCount];
            var cursor = 0;
            for (var vertex = 0; vertex < vertexInLimb.Length; vertex++)
            {
                var count = (int)bonesPerVertex[vertex];
                if (cursor + count > allWeights.Length)
                {
                    vertexInLimb = null;
                    return false;
                }

                if (count > 0)
                {
                    var bone = allWeights[cursor].boneIndex;
                    vertexInLimb[vertex] = bone >= 0 && bone < inLimb.Length && inLimb[bone];
                }

                cursor += count;
            }

            return true;
        }
        finally
        {
            if (bonesPerVertex.IsCreated)
            {
                bonesPerVertex.Dispose();
            }

            if (allWeights.IsCreated)
            {
                allWeights.Dispose();
            }
        }
    }

    // Copies transforms only. Instantiating owner.gameObject would awaken a
    // second NpcActorView, speech stage, wardrobe loaders and VFX; this clone
    // has no gameplay/presentation behaviours and exists solely for BakeMesh.
    private static bool TryClonePoseHierarchy(
        Transform sourceRoot,
        SkinnedMeshRenderer sourceSkin,
        string distalBoneName,
        Vector3 originalScale,
        out GameObject poseClone,
        out SkinnedMeshRenderer clonedSkin)
    {
        poseClone = null;
        clonedSkin = null;
        if (sourceRoot == null || sourceSkin == null)
        {
            return false;
        }

        var map = new Dictionary<Transform, Transform>();
        poseClone = new GameObject("SeveredLimb PoseClone")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        var cloneRoot = poseClone.transform;
        cloneRoot.SetPositionAndRotation(sourceRoot.position, sourceRoot.rotation);
        cloneRoot.localScale = sourceRoot.lossyScale;
        map[sourceRoot] = cloneRoot;
        CloneChildren(sourceRoot, cloneRoot, map);

        if (!map.TryGetValue(sourceSkin.transform, out var clonedSkinTransform))
        {
            return false;
        }

        var sourceBones = sourceSkin.bones;
        var clonedBones = new Transform[sourceBones.Length];
        for (var i = 0; i < sourceBones.Length; i++)
        {
            if (sourceBones[i] == null || !map.TryGetValue(sourceBones[i], out clonedBones[i]))
            {
                return false;
            }
        }

        var sourceDistal = FindBone(sourceBones, distalBoneName);
        if (sourceDistal == null || !map.TryGetValue(sourceDistal, out var clonedDistal))
        {
            return false;
        }

        clonedDistal.localScale = originalScale;
        clonedSkin = clonedSkinTransform.gameObject.AddComponent<SkinnedMeshRenderer>();
        clonedSkin.sharedMesh = sourceSkin.sharedMesh;
        clonedSkin.bones = clonedBones;
        clonedSkin.rootBone = sourceSkin.rootBone != null &&
                              map.TryGetValue(sourceSkin.rootBone, out var clonedRootBone)
            ? clonedRootBone
            : null;
        clonedSkin.localBounds = sourceSkin.localBounds;
        clonedSkin.quality = sourceSkin.quality;
        clonedSkin.updateWhenOffscreen = true;

        for (var shape = 0; shape < sourceSkin.sharedMesh.blendShapeCount; shape++)
        {
            clonedSkin.SetBlendShapeWeight(shape, sourceSkin.GetBlendShapeWeight(shape));
        }

        // It is a calculation object, never a second visible character. Keep
        // the transform hierarchy active so BakeMesh observes it on every
        // Unity version; the only renderer is disabled and the whole clone is
        // destroyed before another camera render.
        clonedSkin.enabled = false;
        return true;
    }

    private static void CloneChildren(
        Transform source,
        Transform clone,
        Dictionary<Transform, Transform> map)
    {
        for (var i = 0; i < source.childCount; i++)
        {
            var sourceChild = source.GetChild(i);
            var cloneChildObject = new GameObject(sourceChild.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var cloneChild = cloneChildObject.transform;
            cloneChild.SetParent(clone, false);
            cloneChild.localPosition = sourceChild.localPosition;
            cloneChild.localRotation = sourceChild.localRotation;
            cloneChild.localScale = sourceChild.localScale;
            map[sourceChild] = cloneChild;
            CloneChildren(sourceChild, cloneChild, map);
        }
    }

    private static GameObject BuildVisual(
        Mesh limbMesh,
        string variant,
        Color skinTint,
        bool recenter)
    {
        var root = new GameObject($"SeveredLimb {variant}");
        var view = new GameObject("Mesh");
        view.transform.SetParent(root.transform, false);
        view.transform.localPosition = recenter ? -limbMesh.bounds.center : Vector3.zero;
        view.AddComponent<MeshFilter>().sharedMesh = limbMesh;

        // Use a solid opaque skin-tone material. Actor submesh 0 is not a safe
        // skin contract (on Genesis it can be lashes/eyes and near-transparent).
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetColor("_BaseColor", new Color(0.80f, 0.60f, 0.52f) * skinTint);
        material.SetFloat("_Smoothness", 0.2f);
        view.AddComponent<MeshRenderer>().sharedMaterial = material;
        return root;
    }

    // Topology/weights come from the readable FBX. Geometry can be either that
    // reference mesh or a BakeMesh result with the same vertex ordering.
    private static Mesh SliceMesh(Mesh topology, Mesh geometry, bool[] vertexInLimb)
    {
        var sourceVertices = geometry.vertices;
        var sourceNormals = geometry.normals;
        var sourceUv = topology.uv;
        if (sourceVertices.Length != topology.vertexCount)
        {
            return null;
        }

        var remap = new int[sourceVertices.Length];
        for (var i = 0; i < remap.Length; i++)
        {
            remap[i] = -1;
        }

        var newVertices = new List<Vector3>();
        var newNormals = new List<Vector3>();
        var newUv = new List<Vector2>();
        var newTriangles = new List<int>();
        var hasNormals = sourceNormals != null && sourceNormals.Length == sourceVertices.Length;
        var hasUv = sourceUv != null && sourceUv.Length == sourceVertices.Length;

        for (var subMesh = 0; subMesh < topology.subMeshCount; subMesh++)
        {
            var triangles = topology.GetTriangles(subMesh);
            for (var triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                var a = triangles[triangle];
                var b = triangles[triangle + 1];
                var c = triangles[triangle + 2];
                if (!vertexInLimb[a] || !vertexInLimb[b] || !vertexInLimb[c])
                {
                    continue;
                }

                newTriangles.Add(Emit(a, remap, newVertices, newNormals, newUv,
                    sourceVertices, sourceNormals, sourceUv, hasNormals, hasUv));
                newTriangles.Add(Emit(b, remap, newVertices, newNormals, newUv,
                    sourceVertices, sourceNormals, sourceUv, hasNormals, hasUv));
                newTriangles.Add(Emit(c, remap, newVertices, newNormals, newUv,
                    sourceVertices, sourceNormals, sourceUv, hasNormals, hasUv));
            }
        }

        if (newTriangles.Count == 0)
        {
            return null;
        }

        var mesh = new Mesh
        {
            name = $"{topology.name}_limb",
            indexFormat = topology.indexFormat
        };
        mesh.SetVertices(newVertices);
        if (hasNormals)
        {
            mesh.SetNormals(newNormals);
        }

        if (hasUv)
        {
            mesh.SetUVs(0, newUv);
        }

        mesh.SetTriangles(newTriangles, 0);
        if (!hasNormals)
        {
            mesh.RecalculateNormals();
        }

        mesh.RecalculateBounds();
        return mesh;
    }

    private static int Emit(
        int source,
        int[] remap,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv,
        Vector3[] sourceVertices,
        Vector3[] sourceNormals,
        Vector2[] sourceUv,
        bool hasNormals,
        bool hasUv)
    {
        if (remap[source] >= 0)
        {
            return remap[source];
        }

        var index = vertices.Count;
        remap[source] = index;
        vertices.Add(sourceVertices[source]);
        if (hasNormals)
        {
            normals.Add(sourceNormals[source]);
        }

        if (hasUv)
        {
            uv.Add(sourceUv[source]);
        }

        return index;
    }

    private static Transform FindBone(Transform[] bones, string name)
    {
        foreach (var bone in bones)
        {
            if (bone != null && bone.name == name)
            {
                return bone;
            }
        }

        return null;
    }

    private static void DestroyTransient(Object value)
    {
        if (value == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Object.Destroy(value);
        }
        else
        {
            Object.DestroyImmediate(value);
        }
    }
}

}
