using System.Collections.Generic;
using HexLive.Simulation.Content;
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

    // §35.5B: does this sim item render as a real garment (and can therefore
    // hang on the drying rack)?
    public static bool IsGarment(string definitionId) =>
        ActorWardrobe.GetVisuals(definitionId).Count > 0;

    // Pull this item's wear prefabs into memory behind the loading curtain. The
    // lookup is cached, so the first time the item is actually DROPPED the view
    // is built from RAM instead of paying a blocking Resources read inside the
    // tick — see the prewarm block in SimulationRunnerBehaviour. A non-garment
    // id is a cheap no-op that also caches the "nothing here" answer.
    public static void Prewarm(string definitionId) => ActorWardrobe.PrewarmAsync(definitionId);

    // Builds a ground-drop visual for a sim item id, or null when the id has
    // no wear prefabs (caller falls back to its primitive). Root pivot = the
    // centre of the laid-out garment (group centre for multi-piece items).
    public static GameObject Build(string definitionId) => Build(definitionId, hanging: false);

    // §35.5B: a rack-hung visual — the same bind-pose garment kept UPRIGHT
    // (squashed to cloth thickness front-to-back instead of lying flat), so it
    // reads as clothes draped over the rail. Footwear stays 3D as on the ground.
    public static GameObject BuildHanging(string definitionId) => Build(definitionId, hanging: true);

    private static GameObject Build(string definitionId, bool hanging)
    {
        var visuals = ActorWardrobe.GetVisuals(definitionId);
        if (visuals.Count == 0)
        {
            return null;
        }

        var flatten = !IsFootwear(definitionId);
        var lieFlat = flatten && !hanging ? Quaternion.Euler(-90f, 0f, 0f) : Quaternion.identity;
        var root = new GameObject($"GarmentDrop {definitionId}");
        var pieces = new List<Transform>();
        var widths = new List<float>();
        var hangingGloves = hanging && IsPairedHandwear(definitionId);
        foreach (var wear in visuals)
        {
            var source = wear.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (source == null || source.sharedMesh == null)
            {
                continue;
            }

            var mesh = source.sharedMesh;
            // Paired handwear arrives as a single skinned mesh containing the
            // left and right arm pieces at their character-space positions. On
            // a rail that
            // leaves the pair metres apart with fingers pointing away from one
            // another. Split the disconnected left/right halves only for the
            // hanging presentation, then hang the cuffs together and turn both
            // finger tips down. The sim still owns one garment item.
            // Always bake the rail presentation. Some imported glove variants
            // expose a readable bind mesh while others do not; mixing those
            // two paths was why the same gloves could look compact at spawn
            // but revert to the character-wide T-pose after a reload.
            // Baking supplies one readable, renderer-local mesh contract for
            // every variant without changing the source asset.
            var bakedGloveMesh = hangingGloves;
            if (hangingGloves)
            {
                mesh = new Mesh { name = $"{mesh.name} hanging copy" };
                source.BakeMesh(mesh);
            }
            if (hangingGloves && TryCreateHangingGlovePair(
                    root.transform, mesh, source, definitionId, pieces, widths))
            {
                if (bakedGloveMesh) Object.Destroy(mesh);
                continue;
            }

            var piece = new GameObject(mesh.name);
            piece.transform.SetParent(root.transform, false);
            if (flatten)
            {
                // Lying: squash height. Hanging (§35.5B): stay upright, squash
                // front-to-back to cloth thickness instead.
                piece.transform.localScale = hanging
                    ? new Vector3(hangingGloves ? 0.28f : 1f, 1f, FlattenFactor)
                    : new Vector3(1f, FlattenFactor, 1f);
            }

            var view = new GameObject("Mesh");
            view.transform.SetParent(piece.transform, false);
            view.transform.localRotation = lieFlat;
            // Recentre: the mesh's bounds centre lands on the piece pivot.
            view.transform.localPosition = -(lieFlat * mesh.bounds.center);
            view.AddComponent<MeshFilter>().sharedMesh = mesh;
            // §31B.4E: a ground/rack item keeps its ITEM id even though it
            // loads the prototype's art. Use that id here too: otherwise a
            // variant (for example a coloured T-shirt) renders with the
            // prototype materials only after it is taken off or washes ashore.
            view.AddComponent<MeshRenderer>().sharedMaterials = MaterialsForDrop(source, definitionId);
            if (bakedGloveMesh) TrackGeneratedMesh(root, mesh);

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
        if (pieces.Count > 1 && !hangingGloves)
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

    /// <summary>
    /// Uses the shared wardrobe slot catalogue rather than item-name guesses:
    /// sandals, heels and sneakers belong on the same shelf as boots.
    /// </summary>
    public static bool IsFootwear(string definitionId)
        => GarmentStorageCategories.IsFootwear(definitionId);

    private static bool IsPairedHandwear(string definitionId) =>
        GarmentStorageCategories.IsPairedHandwear(definitionId);

    private static bool TryCreateHangingGlovePair(
        Transform parent, Mesh source, SkinnedMeshRenderer renderer, string definitionId,
        List<Transform> pieces, List<float> widths)
    {
        if (!TrySplitHandMeshes(source, out var left, out var right)) return false;

        // In the character bind pose the hands point along opposite X axes.
        // Counter-rotate each half about Z so both fingertips point down (−Y),
        // then keep them side by side on one compact hanger. Their local X
        // separation is the same axis used by the approved editor preview;
        // it gives the pair a 0.30-wu overall width instead of the 1.60-wu
        // character bind-pose spread.
        AddHangingGlovePiece(parent, left, renderer, definitionId,
            Quaternion.Euler(0f, 0f, 90f), -0.09f, pieces, widths);
        AddHangingGlovePiece(parent, right, renderer, definitionId,
            Quaternion.Euler(0f, 0f, -90f), 0.09f, pieces, widths);
        TrackGeneratedMesh(parent.gameObject, left);
        TrackGeneratedMesh(parent.gameObject, right);
        return true;
    }

    private static void AddHangingGlovePiece(
        Transform parent, Mesh mesh, SkinnedMeshRenderer source, string definitionId,
        Quaternion rotation, float sideOffset, List<Transform> pieces, List<float> widths)
    {
        var piece = new GameObject(mesh.name);
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = new Vector3(sideOffset, 0f, 0f);

        var view = new GameObject("Mesh");
        view.transform.SetParent(piece.transform, false);
        view.transform.localRotation = rotation;
        view.transform.localPosition = -(rotation * mesh.bounds.center);
        view.AddComponent<MeshFilter>().sharedMesh = mesh;
        view.AddComponent<MeshRenderer>().sharedMaterials = MaterialsForDrop(source, definitionId);

        pieces.Add(piece.transform);
        widths.Add(mesh.bounds.size.x);
    }

    // Keeps the original submesh/material layout while splitting triangles at
    // the character-space centreline. A glove pair is two disconnected islands;
    // if imported art is unreadable or crosses the centreline, fall back to its
    // existing generic garment presentation rather than producing broken mesh.
    private static bool TrySplitHandMeshes(Mesh source, out Mesh left, out Mesh right)
    {
        left = null;
        right = null;
        try
        {
            var vertices = source.vertices;
            if (vertices.Length == 0) return false;
            var splitX = source.bounds.center.x;
            var leftTriangles = new List<int>[source.subMeshCount];
            var rightTriangles = new List<int>[source.subMeshCount];
            for (var sub = 0; sub < source.subMeshCount; sub++)
            {
                leftTriangles[sub] = new List<int>();
                rightTriangles[sub] = new List<int>();
                var triangles = source.GetTriangles(sub);
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var centre = (vertices[triangles[i]] + vertices[triangles[i + 1]] +
                                  vertices[triangles[i + 2]]) / 3f;
                    var bucket = centre.x < splitX ? leftTriangles[sub] : rightTriangles[sub];
                    bucket.Add(triangles[i]);
                    bucket.Add(triangles[i + 1]);
                    bucket.Add(triangles[i + 2]);
                }
            }

            left = BuildMeshHalf(source, leftTriangles, "left glove");
            right = BuildMeshHalf(source, rightTriangles, "right glove");
            return left != null && right != null;
        }
        catch (UnityException)
        {
            return false;
        }
    }

    private static Mesh BuildMeshHalf(Mesh source, List<int>[] trianglesBySubmesh, string name)
    {
        var map = new Dictionary<int, int>();
        var vertices = source.vertices;
        var normals = source.normals;
        var uvs = source.uv;
        var remappedVertices = new List<Vector3>();
        var remappedNormals = new List<Vector3>();
        var remappedUvs = new List<Vector2>();
        var remapped = new List<int>[trianglesBySubmesh.Length];
        for (var sub = 0; sub < trianglesBySubmesh.Length; sub++)
        {
            remapped[sub] = new List<int>();
            foreach (var oldIndex in trianglesBySubmesh[sub])
            {
                if (!map.TryGetValue(oldIndex, out var newIndex))
                {
                    newIndex = remappedVertices.Count;
                    map.Add(oldIndex, newIndex);
                    remappedVertices.Add(vertices[oldIndex]);
                    if (normals.Length == vertices.Length) remappedNormals.Add(normals[oldIndex]);
                    if (uvs.Length == vertices.Length) remappedUvs.Add(uvs[oldIndex]);
                }
                remapped[sub].Add(newIndex);
            }
        }
        if (remappedVertices.Count == 0) return null;

        var result = new Mesh { name = name, subMeshCount = source.subMeshCount };
        result.SetVertices(remappedVertices);
        if (remappedNormals.Count == remappedVertices.Count) result.SetNormals(remappedNormals);
        else result.RecalculateNormals();
        if (remappedUvs.Count == remappedVertices.Count) result.SetUVs(0, remappedUvs);
        for (var sub = 0; sub < remapped.Length; sub++) result.SetTriangles(remapped[sub], sub);
        result.RecalculateBounds();
        return result;
    }

    private static void TrackGeneratedMesh(GameObject root, Mesh mesh)
    {
        var owner = root.GetComponent<GeneratedMeshOwner>();
        if (owner == null) owner = root.AddComponent<GeneratedMeshOwner>();
        owner.Track(mesh);
    }

    private sealed class GeneratedMeshOwner : MonoBehaviour
    {
        private readonly List<Mesh> _meshes = new();

        public void Track(Mesh mesh) => _meshes.Add(mesh);

        private void OnDestroy()
        {
            foreach (var mesh in _meshes)
                if (mesh != null) Object.Destroy(mesh);
            _meshes.Clear();
        }
    }

    // Same partial-slot replacement contract as Wear.ApplyVariant: a variant
    // may supply just its cloth material and intentionally retain prototype
    // buttons, trims, or hardware in the remaining submeshes.
    private static Material[] MaterialsForDrop(SkinnedMeshRenderer source, string definitionId)
    {
        var sourceMaterials = source.sharedMaterials;
        var variantMaterials = Garments.GarmentVariants.MaterialsOf(definitionId);
        if (variantMaterials == null || variantMaterials.Length == 0)
        {
            return sourceMaterials;
        }

        var result = (Material[])sourceMaterials.Clone();
        for (var i = 0; i < result.Length && i < variantMaterials.Length; i++)
        {
            if (variantMaterials[i] != null)
            {
                result[i] = variantMaterials[i];
            }
        }

        return result;
    }
}

}
