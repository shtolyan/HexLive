using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Photographs wear prefabs into inventory icons (spec §31B.4A step 4).
///
/// Until now that step was the one hand-made link in the drop pipeline — the
/// spec itself says "there is no automated photographer tool" — so every new
/// garment either got a hand-shot PNG or silently fell back to an emoji in the
/// inventory UI. It is the same shot every time, so it may as well be a menu.
///
/// Transparency is taken the compositing way: render the garment twice, once on
/// black and once on white, and read the alpha out of the difference. Unity's
/// preview render target has no usable alpha of its own, and this also gets the
/// anti-aliased and alpha-clipped edges (feathers, lace) right instead of
/// leaving them fringed.
/// </summary>
internal static class WearIconShooter
{
    private const string WearRoot = "Assets/Resources/HexLive/Wear";
    private const string IconRoot = "Assets/Resources/HexLive/UI/Items";
    private const int Size = 512;

    // Mostly front, turned a little and seen slightly from above. The angle is
    // there to give a garment some depth, not to show its side: a wide flat
    // piece (a feather fan) goes edge-on at a true three-quarter and stops
    // reading as itself.
    private static readonly Vector3 View = new Vector3(-0.28f, 0.12f, -1f);

    [MenuItem("HexLive/Wear/Shoot Missing Item Icons")]
    private static void ShootMissing()
    {
        Shoot(false);
    }

    [MenuItem("HexLive/Wear/Shoot Item Icons (Re-shoot All)")]
    private static void ShootAll()
    {
        Shoot(true);
    }

    private static void Shoot(bool force)
    {
        Directory.CreateDirectory(IconRoot);
        var log = new System.Text.StringBuilder();
        var shot = new List<string>();

        // The prototype's geometry, kept even when its own icon is already on
        // disk: a variant (§31B.4E) borrows it and would otherwise be skipped
        // for the wrong reason.
        var art = new Dictionary<string, Mesh>();
        var protoMaterials = new Dictionary<string, Material[]>();

        foreach (var folder in AssetDatabase.GetSubFolders(WearRoot).OrderBy(f => f))
        {
            var id = Path.GetFileName(folder);

            var prefab = AssetDatabase.FindAssets("t:GameObject", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .FirstOrDefault(p => p != null && p.GetComponentInChildren<SkinnedMeshRenderer>(true) != null);
            if (prefab == null)
            {
                log.AppendLine($"  {id}: нет префаба со скиннед-мешем");
                continue;
            }

            var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr.sharedMesh == null)
            {
                log.AppendLine($"  {id}: у рендерера нет меша");
                continue;
            }

            art[id] = smr.sharedMesh;
            protoMaterials[id] = smr.sharedMaterials;

            var target = $"{IconRoot}/{id}.png";
            if (!force && File.Exists(target))
            {
                continue;
            }

            if (!Write(target, smr.sharedMesh, smr.sharedMaterials, shot, log, id))
            {
                continue;
            }
        }

        // A variant is one geometry with other materials, so it is one item in
        // the inventory and needs its own picture — the loader takes the id
        // verbatim and has no fallback to the prototype's PNG.
        foreach (var def in AssetDatabase.FindAssets("t:GarmentDefinition")
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .Select(AssetDatabase.LoadAssetAtPath<GarmentDefinition>)
                     .Where(d => d != null && !string.IsNullOrEmpty(d.id))
                     .Where(d => d.variantMaterials != null && d.variantMaterials.Length > 0)
                     .OrderBy(d => d.id))
        {
            var target = $"{IconRoot}/{def.id}.png";
            if (!force && File.Exists(target))
            {
                continue;
            }

            if (!art.TryGetValue(def.ArtId, out var mesh) ||
                !protoMaterials.ContainsKey(def.ArtId))
            {
                log.AppendLine($"  {def.id}: нет арта прототипа {def.ArtId}");
                continue;
            }

            if (mesh.subMeshCount > def.variantMaterials.Length)
            {
                log.AppendLine($"  {def.id}: материалов {def.variantMaterials.Length}, " +
                               $"подмешей {mesh.subMeshCount} — часть меша не будет снята");
            }

            // Laid OVER the prototype's materials, exactly as Wear.ApplyVariant
            // does at runtime — a colourway is allowed to repaint only part of a
            // garment. The welded boots are seven submeshes and their colourways
            // name six, so passing the variant array alone left the stocking top
            // with no material: the icon would disagree with the thing worn.
            var painted = (Material[])protoMaterials[def.ArtId].Clone();
            for (var i = 0; i < painted.Length && i < def.variantMaterials.Length; i++)
            {
                if (def.variantMaterials[i] != null)
                {
                    painted[i] = def.variantMaterials[i];
                }
            }

            Write(target, mesh, painted, shot, log, def.id);
        }

        AssetDatabase.Refresh();
        foreach (var path in shot)
        {
            ApplySpriteImport(path);
        }

        Debug.Log($"[WearIcon] снято {shot.Count}\n{log}");
    }

    private static bool Write(string target, Mesh mesh, Material[] materials,
                              List<string> shot, System.Text.StringBuilder log, string id)
    {
        var png = Capture(mesh, materials);
        if (png == null)
        {
            log.AppendLine($"  {id}: снимок не получился");
            return false;
        }

        File.WriteAllBytes(target, png);
        shot.Add(target);
        log.AppendLine($"  {id} -> {target}");
        return true;
    }

    private static byte[] Capture(Mesh mesh, Material[] materials)
    {
        var onBlack = Render(mesh, materials, Color.black);
        var onWhite = Render(mesh, materials, Color.white);
        if (onBlack == null || onWhite == null)
        {
            return null;
        }

        try
        {
            var dark = onBlack.GetPixels();
            var light = onWhite.GetPixels();
            for (var i = 0; i < dark.Length; i++)
            {
                // Where the garment is opaque both renders agree; where it is
                // absent they differ by exactly the backdrop.
                var alpha = 1f - ((light[i].r - dark[i].r) +
                                  (light[i].g - dark[i].g) +
                                  (light[i].b - dark[i].b)) / 3f;
                alpha = Mathf.Clamp01(alpha);
                dark[i] = alpha < 0.004f
                    ? Color.clear
                    : new Color(dark[i].r / alpha, dark[i].g / alpha, dark[i].b / alpha, alpha);
            }

            var composed = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            composed.SetPixels(dark);
            composed.Apply();
            var png = composed.EncodeToPNG();
            Object.DestroyImmediate(composed);
            return png;
        }
        finally
        {
            Object.DestroyImmediate(onBlack);
            Object.DestroyImmediate(onWhite);
        }
    }

    // PreviewRenderUtility rather than a scene camera: it is the one path that
    // renders correctly in edit mode under URP, and it touches no open scene.
    private static Texture2D Render(Mesh mesh, Material[] materials, Color backdrop)
    {
        var preview = new PreviewRenderUtility();
        try
        {
            var bounds = OneOfAPair(mesh);
            var radius = Mathf.Max(bounds.extents.magnitude, 0.001f);
            var camera = preview.camera;
            camera.fieldOfView = 30f;
            var distance = FitDistance(bounds, camera.fieldOfView);
            camera.transform.position = bounds.center - View.normalized * distance;
            camera.transform.LookAt(bounds.center);
            camera.nearClipPlane = radius * 0.05f;
            camera.farClipPlane = distance * 4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = backdrop;

            preview.lights[0].intensity = 1.15f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35f, 140f, 0f);
            preview.lights[1].intensity = 0.55f;
            preview.lights[1].transform.rotation = Quaternion.Euler(-15f, -60f, 0f);

            preview.BeginStaticPreview(new Rect(0, 0, Size, Size));
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                var material = i < materials.Length ? materials[i] : null;
                if (material != null)
                {
                    preview.DrawMesh(mesh, Matrix4x4.identity, material, i);
                }
            }

            camera.Render();
            return preview.EndStaticPreview();
        }
        finally
        {
            preview.Cleanup();
        }
    }

    /// <summary>
    /// Closest the camera can stand and still hold the whole garment.
    /// </summary>
    /// <remarks>
    /// Fitting the bounding SPHERE is the easy version and it wastes the frame:
    /// a headdress is four times wider than it is deep, so the sphere is mostly
    /// empty air and the item shrinks to a third of the icon. This projects the
    /// eight box corners into the camera's own basis instead and takes the
    /// tightest distance that still keeps every one of them inside the frustum.
    /// </remarks>
    /// <summary>
    /// Frame ONE half of a pair, not the span between them.
    /// </summary>
    /// <remarks>
    /// A pair is one item and therefore one mesh, and in the bind pose the arms
    /// are spread: the gloves measure 1.45 m across and 5 cm tall. Fitting that
    /// box into a square icon draws a hairline — six of the drop's icons came
    /// out effectively blank, which is exactly what the content check caught and
    /// a "the file exists" check would not have.
    ///
    /// Only when the mesh is much wider than it is tall or deep, and only along
    /// X: nothing else in the wardrobe has that shape, and a hat framed on half
    /// of itself would be worse than one framed whole.
    /// </remarks>
    private static Bounds OneOfAPair(Mesh mesh)
    {
        var bounds = mesh.bounds;
        var size = bounds.size;
        if (size.x <= 3f * Mathf.Max(size.y, size.z))
        {
            return bounds;
        }

        // The REAL box of the right-hand half, walked over the vertices rather
        // than assumed to be half the width: a pair is two lumps with air
        // between them, so half the bounding box is mostly that air and the
        // glove ends up small and off-centre inside its own icon.
        var found = false;
        var half = new Bounds();
        foreach (var v in mesh.vertices)
        {
            if (v.x < bounds.center.x)
            {
                continue;
            }

            if (!found)
            {
                half = new Bounds(v, Vector3.zero);
                found = true;
            }
            else
            {
                half.Encapsulate(v);
            }
        }

        return found ? half : bounds;
    }

    private static float FitDistance(Bounds bounds, float fieldOfView)
    {
        var direction = View.normalized;
        var basis = Quaternion.Inverse(Quaternion.LookRotation(direction));
        var tangent = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
        var distance = 0f;

        for (var corner = 0; corner < 8; corner++)
        {
            var offset = new Vector3(
                (corner & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                (corner & 2) == 0 ? -bounds.extents.y : bounds.extents.y,
                (corner & 4) == 0 ? -bounds.extents.z : bounds.extents.z);
            var local = basis * offset;
            // The frame is square, so one tangent bounds both axes.
            distance = Mathf.Max(distance,
                Mathf.Max(Mathf.Abs(local.x), Mathf.Abs(local.y)) / tangent - local.z);
        }

        return distance * 1.06f;   // a hair of margin so nothing touches the edge
    }

    private static void ApplySpriteImport(string path)
    {
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
        {
            return;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();
    }
}
