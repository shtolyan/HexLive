using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// Spec 40.19: dropped clothing lies on the ground as the REAL garment mesh
// instead of a coloured primitive. The wear prefab's skinned mesh renders in
// bind pose through a plain MeshFilter (a T-pose shirt flattened looks like
// clothes laid out on the ground), rotated onto its back and squashed to
// cloth thickness purely via transforms — no vertex baking, so meshes
// without Read/Write import work too. The source meshes live in the
// character's bind space with the origin at the FEET; recentring the mesh
// against its bounds is what puts the pivot at the garment's own centre, so
// a spawned drop sits on its anchor instead of floating a torso-height away.
// Footwear keeps its 3D shape and simply stands on the ground.
public static class GarmentDropFactory
{
    // Thickness squash for the lying garment. Not zero: coplanar front/back
    // faces would z-fight.
    private const float FlattenFactor = 0.12f;

    // Gap between side-by-side pieces, as a fraction of the widest piece.
    private const float PieceGapFactor = 0.2f;

    // Builds a ground-drop visual for a sim item id, or null when the id has
    // no wear prefabs (caller falls back to its primitive). Root pivot = the
    // centre of the laid-out garment (group centre for multi-piece items).
    public static GameObject Build(string definitionId)
    {
        var visuals = ActorWardrobe.GetVisuals(definitionId);
        if (visuals.Count == 0)
        {
            return null;
        }

        var flatten = !IsFootwear(definitionId);
        var lieFlat = flatten ? Quaternion.Euler(-90f, 0f, 0f) : Quaternion.identity;
        var root = new GameObject($"GarmentDrop {definitionId}");
        var pieces = new List<Transform>();
        var widths = new List<float>();
        foreach (var wear in visuals)
        {
            var source = wear.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (source == null || source.sharedMesh == null)
            {
                continue;
            }

            var mesh = source.sharedMesh;
            var piece = new GameObject(mesh.name);
            piece.transform.SetParent(root.transform, false);
            if (flatten)
            {
                piece.transform.localScale = new Vector3(1f, FlattenFactor, 1f);
            }

            var view = new GameObject("Mesh");
            view.transform.SetParent(piece.transform, false);
            view.transform.localRotation = lieFlat;
            // Recentre: the mesh's bounds centre lands on the piece pivot.
            view.transform.localPosition = -(lieFlat * mesh.bounds.center);
            view.AddComponent<MeshFilter>().sharedMesh = mesh;
            view.AddComponent<MeshRenderer>().sharedMaterials = source.sharedMaterials;

            pieces.Add(piece.transform);
            widths.Add(mesh.bounds.size.x);
        }

        if (pieces.Count == 0)
        {
            Object.Destroy(root);
            return null;
        }

        // A sim item can be several visual pieces (underwear = bra +
        // panties): lay them side by side, group centred on the root pivot.
        if (pieces.Count > 1)
        {
            var gap = 0f;
            var total = 0f;
            foreach (var w in widths)
            {
                gap = Mathf.Max(gap, w);
                total += w;
            }

            gap *= PieceGapFactor;
            total += gap * (pieces.Count - 1);
            var x = -total * 0.5f;
            for (var i = 0; i < pieces.Count; i++)
            {
                pieces[i].localPosition = new Vector3(x + widths[i] * 0.5f, 0f, 0f);
                x += widths[i] + gap;
            }
        }

        return root;
    }

    private static bool IsFootwear(string definitionId)
    {
        var id = definitionId.ToLowerInvariant();
        return id.Contains("boot") || id.Contains("shoe");
    }
}

}
