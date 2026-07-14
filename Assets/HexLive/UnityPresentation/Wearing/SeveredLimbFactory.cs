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
        if (owner == null || string.IsNullOrEmpty(variant) ||
            !ZoneDistalBone.TryGetValue(variant, out var distalBoneName))
        {
            return null;
        }

        var skin = owner.PrimaryBodySkin;
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
        var weights = mesh.boneWeights;
        var vertexInLimb = new bool[weights.Length];
        for (var v = 0; v < weights.Length; v++)
        {
            vertexInLimb[v] = DominantBoneInLimb(weights[v], inLimb);
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
        // a limb reliably; the stump end is an open ring (the raw cut).
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.80f, 0.60f, 0.52f));
        mat.SetFloat("_Smoothness", 0.2f);
        view.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return root;
    }

    private static bool DominantBoneInLimb(BoneWeight w, bool[] inLimb)
    {
        // Pick the highest-weight bone of the four and test membership.
        var bi = w.boneIndex0;
        var bw = w.weight0;
        if (w.weight1 > bw) { bw = w.weight1; bi = w.boneIndex1; }
        if (w.weight2 > bw) { bw = w.weight2; bi = w.boneIndex2; }
        if (w.weight3 > bw) { bi = w.boneIndex3; }
        return bi >= 0 && bi < inLimb.Length && inLimb[bi];
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
