using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Spec §50: a severed limb lies in the world as the REAL limb geometry, carved
// out of its former owner's body mesh. We slice the owner's shared (bind-pose)
// mesh — NOT a runtime bake — so the collapse-to-nothing we do on the living
// body (which hides the limb there) never touches the dropped copy. Triangles
// whose vertices are skinned to the distal bone chain (forearm+hand / shin+
// foot) become a standalone MeshFilter, recentred on its own bounds so the
// drop sits on its anchor. Needs a Read/Write-enabled mesh; when that's
// unavailable (or the owner is gone) the caller falls back to a primitive.
public static class SeveredLimbFactory
{
    private const string MartaActorResource = "HexLive/Actors/Marta";

    // Which bone sub-tree each severed zone carved off. Matches NpcActorView's
    // SeveredDistalBone so the dropped mesh is exactly what vanished on the body.
    private static readonly Dictionary<string, string> ZoneDistalBone = new()
    {
        ["ArmL"] = "lForearmBend",
        ["ArmR"] = "rForearmBend",
        ["LegL"] = "lShin",
        ["LegR"] = "rShin"
    };

    // Build the limb mesh object for `variant` (a BodyPart name) from `owner`.
    // Returns null when the owner/mesh can't provide it — caller uses a prop.
    public static GameObject Build(NpcActorView owner, string variant)
    {
        if (string.IsNullOrEmpty(variant) ||
            !ZoneDistalBone.TryGetValue(variant, out var distalBoneName))
        {
            return null;
        }

        // Spec §50 + 40.7: the drop keeps its owner's skin tone at sever time
        // (tan/sunburn/grime baked into the flesh material below).
        var skinTint = owner != null ? owner.SkinTint : Color.white;

        // Molly's limbs always carve from Marta's readable Genesis mesh (her
        // own re-saved mesh sliced wrong). Marta is ALSO the donor when the
        // owner view is gone (e.g. the drop outlives/precedes the actor view)
        // or its skin can't provide geometry — a real limb must never degrade
        // to the capsule fallback just because the owner wasn't found.
        if (owner == null || owner.ActorMesh == ActorName.Molly)
        {
            var martaLimb = BuildFromMartaLimb(variant, distalBoneName, skinTint);
            if (martaLimb != null)
            {
                return martaLimb;
            }
        }

        var ownLimb = owner != null
            ? BuildFromSkin(owner.PrimaryBodySkin, variant, distalBoneName, skinTint)
            : null;
        return ownLimb != null ? ownLimb : BuildFromMartaLimb(variant, distalBoneName, skinTint);
    }

    private static GameObject BuildFromMartaLimb(string variant, string distalBoneName, Color skinTint)
    {
        var marta = Resources.Load<GameObject>(MartaActorResource);
        if (marta == null)
        {
            return null;
        }

        return BuildFromSkin(FindPrimaryBodySkin(marta), variant, distalBoneName, skinTint);
    }

    private static GameObject BuildFromSkin(
        SkinnedMeshRenderer skin, string variant, string distalBoneName, Color skinTint)
    {
        var mesh = skin != null ? skin.sharedMesh : null;
        if (mesh == null || !mesh.isReadable)
        {
            return null; // no Read/Write access — caller falls back
        }

        var bones = skin.bones;
        var distalRoot = FindBone(bones, distalBoneName);
        if (distalRoot == null)
        {
            return null;
        }

        // Bone indices in the distal sub-tree (the cut bone and its children).
        var inLimb = new bool[bones.Length];
        var any = false;
        for (var i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            if (b != null && (b == distalRoot || b.IsChildOf(distalRoot)))
            {
                inLimb[i] = true;
                any = true;
            }
        }

        if (!any)
        {
            return null;
        }

        // A vertex belongs to the limb if its dominant bone weight is a limb
        // bone. Keep triangles all of whose vertices are limb vertices.
        //
        // Modern skin-weight API, for the reason HealthDollStage already
        // documents: the legacy mesh.boneWeights getter comes back EMPTY unless
        // the mesh carries exactly four influences per vertex. Welded garments
        // (spec §31B.4D) store ONE, so the old call sliced nothing off them and
        // the cloth on a severed arm silently stayed whole. GetAllBoneWeights
        // sorts influences most-significant first, so the dominant bone is the
        // first entry of each run.
        var bonesPerVertex = mesh.GetBonesPerVertex();
        var allWeights = mesh.GetAllBoneWeights();
        if (bonesPerVertex.Length != mesh.vertexCount)
        {
            return null;   // unskinned mesh — nothing to slice by
        }

        var vertexInLimb = new bool[mesh.vertexCount];
        var cursor = 0;
        for (var v = 0; v < vertexInLimb.Length; v++)
        {
            int count = bonesPerVertex[v];
            if (count > 0)
            {
                var bone = allWeights[cursor].boneIndex;
                vertexInLimb[v] = bone >= 0 && bone < inLimb.Length && inLimb[bone];
            }

            cursor += count;
        }

        var limbMesh = SliceMesh(mesh, vertexInLimb);
        if (limbMesh == null)
        {
            return null;
        }

        // Render the sliced mesh in bind pose through a plain MeshFilter,
        // recentred on its own bounds (the source origin sits at the feet).
        var root = new GameObject($"SeveredLimb {variant}");
        var view = new GameObject("Mesh");
        view.transform.SetParent(root.transform, false);
        view.transform.localPosition = -limbMesh.bounds.center;
        view.AddComponent<MeshFilter>().sharedMesh = limbMesh;
        // A solid OPAQUE skin-tone material — NOT the actor's submesh-0 material,
        // which on a Genesis figure can be a transparent lashes/eyes material
        // (that rendered the limb near-invisible). A plain flesh colour reads as
        // a limb reliably; the stump end is an open ring (the raw cut). The
        // owner's skin tint multiplies it — the same math the body shader does
        // (texture × _BaseColor) — so a tanned body drops a tanned limb.
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.80f, 0.60f, 0.52f) * skinTint);
        mat.SetFloat("_Smoothness", 0.2f);
        view.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return root;
    }

    private static SkinnedMeshRenderer FindPrimaryBodySkin(GameObject actorRoot)
    {
        var skins = actorRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var skin in skins)
        {
            if (skin != null && skin.sharedMesh != null && skin.bones != null &&
                skin.name.IndexOf("Genesis", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return skin;
            }
        }

        SkinnedMeshRenderer best = null;
        var bestVerts = -1;
        foreach (var skin in skins)
        {
            if (skin == null || skin.sharedMesh == null || skin.bones == null ||
                !skin.sharedMesh.isReadable)
            {
                continue;
            }

            var n = skin.name.ToLowerInvariant();
            if (n.Contains("hair") || n.Contains("eyelash") || n.Contains("brow") || n.Contains("eye"))
            {
                continue;
            }

            if (skin.sharedMesh.vertexCount > bestVerts)
            {
                bestVerts = skin.sharedMesh.vertexCount;
                best = skin;
            }
        }

        return best;
    }

    // Build a new mesh from the triangles fully inside the limb-vertex set,
    // compacting to only the referenced vertices.
    private static Mesh SliceMesh(Mesh source, bool[] vertexInLimb)
    {
        var srcVerts = source.vertices;
        var srcNormals = source.normals;
        var srcUv = source.uv;
        var remap = new int[srcVerts.Length];
        for (var i = 0; i < remap.Length; i++)
        {
            remap[i] = -1;
        }

        var newVerts = new List<Vector3>();
        var newNormals = new List<Vector3>();
        var newUv = new List<Vector2>();
        var newTris = new List<int>();
        var hasNormals = srcNormals != null && srcNormals.Length == srcVerts.Length;
        var hasUv = srcUv != null && srcUv.Length == srcVerts.Length;

        for (var sm = 0; sm < source.subMeshCount; sm++)
        {
            var tris = source.GetTriangles(sm);
            for (var t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (!vertexInLimb[a] || !vertexInLimb[b] || !vertexInLimb[c])
                {
                    continue;
                }

                newTris.Add(Emit(a, remap, newVerts, newNormals, newUv, srcVerts, srcNormals, srcUv, hasNormals, hasUv));
                newTris.Add(Emit(b, remap, newVerts, newNormals, newUv, srcVerts, srcNormals, srcUv, hasNormals, hasUv));
                newTris.Add(Emit(c, remap, newVerts, newNormals, newUv, srcVerts, srcNormals, srcUv, hasNormals, hasUv));
            }
        }

        if (newTris.Count == 0)
        {
            return null;
        }

        var mesh = new Mesh { name = $"{source.name}_limb" };
        mesh.SetVertices(newVerts);
        if (hasNormals)
        {
            mesh.SetNormals(newNormals);
        }

        if (hasUv)
        {
            mesh.SetUVs(0, newUv);
        }

        mesh.SetTriangles(newTris, 0);
        if (!hasNormals)
        {
            mesh.RecalculateNormals();
        }

        mesh.RecalculateBounds();
        return mesh;
    }

    private static int Emit(int src, int[] remap, List<Vector3> verts, List<Vector3> normals,
        List<Vector2> uv, Vector3[] srcVerts, Vector3[] srcNormals, Vector2[] srcUv,
        bool hasNormals, bool hasUv)
    {
        if (remap[src] >= 0)
        {
            return remap[src];
        }

        var idx = verts.Count;
        remap[src] = idx;
        verts.Add(srcVerts[src]);
        if (hasNormals)
        {
            normals.Add(srcNormals[src]);
        }

        if (hasUv)
        {
            uv.Add(srcUv[src]);
        }

        return idx;
    }

    private static Transform FindBone(Transform[] bones, string name)
    {
        foreach (var b in bones)
        {
            if (b != null && b.name == name)
            {
                return b;
            }
        }

        return null;
    }
}

}
