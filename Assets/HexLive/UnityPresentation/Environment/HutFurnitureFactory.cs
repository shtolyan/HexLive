#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{

/// <summary>
/// Compact furniture authored for the 1.5-wu hut interior. These are deliberate
/// assemblies in the hut's board/stick/leaf palette, not scaled outdoor props.
/// </summary>
public static class HutFurnitureFactory
{
    // The simulation anchor is the nearest safe interior junction at local
    // X=±0.6495. Move only the furniture/pose outward: 0.6495 + 0.246 =
    // 0.8955, and the measured 0.766-wu cot leaves a 0.0205-wu seam to the
    // vertical wall at apothem 1.299. Interaction still starts from the safe
    // junction; furniture itself may occupy the architecture-edge space.
    public const float BedWallSnugOffset = 0.246f;
    // Match the player-approved BedSleepPoseTest contract exactly: the bed root
    // sits on the walkable plane. Do not reinterpret FBX bounds as a second
    // placement offset; doing so dropped the production beds toward terrain.
    public const float BedRootLift = HutAssembly.FloorSurfaceLift;
    private const string HutPrefabPath = "HexLive/Objects/building.hut_1hex";
    private static readonly Dictionary<string, Material> Materials = new();

    /// <summary>
    /// The ordinary production bed.basic assembly, compacted as integrated
    /// one-hex furniture. Meshes, materials, construction pieces and sleep
    /// marker still come from the same BedAssembly used by world furniture.
    /// </summary>
    public static GameObject? BuildBed()
    {
        var bed = BedAssembly.BuildFinished(ContentIds.BedBasic);
        if (bed == null) return null;
        bed.name = "Integrated bed.basic (native)";
        return bed;
    }

    /// <summary>
    /// Low 0.72-wu stone-lined hearth with a compact roasting bar. Simulation
    /// warmth/fuel remains the normal campfire contract; only its art is indoor.
    /// </summary>
    public static GameObject BuildHearth()
    {
        var root = new GameObject("Integrated hut hearth");
        var furniture = new GameObject("Hearth furniture").transform;
        furniture.SetParent(root.transform, false);
        furniture.localPosition = Vector3.zero;
        var stone = Material("HearthStone", new Color(0.34f, 0.31f, 0.27f));
        var stoneLight = Material("HearthStoneLight", new Color(0.47f, 0.43f, 0.36f));
        var ember = Material("HearthEmber", new Color(0.36f, 0.075f, 0.025f), emission: true);
        var bark = Material("Bark", new Color(0.30f, 0.17f, 0.075f));
        var heartwood = Material("Heartwood", new Color(0.54f, 0.285f, 0.105f));
        var rope = Material("Rope", new Color(0.62f, 0.46f, 0.24f));

        AddCylinder(furniture, "hearth_raised_base", new Vector3(0f, 0.035f, 0f),
            Quaternion.identity, 0.35f, 0.07f, stone);
        AddCylinder(furniture, "hearth_ember_bowl", new Vector3(0f, 0.078f, 0f),
            Quaternion.identity, 0.205f, 0.035f, ember);

        for (var i = 0; i < 10; i++)
        {
            var angle = i * Mathf.PI * 2f / 10f;
            AddSphere(furniture, $"hearth_stone_{i:00}",
                new Vector3(Mathf.Cos(angle) * 0.275f, 0.105f,
                    Mathf.Sin(angle) * 0.275f),
                new Vector3(0.105f, 0.070f + (i % 2) * 0.008f, 0.09f),
                Quaternion.Euler(0f, i * 37f, 0f), i % 3 == 0 ? stoneLight : stone);
        }

        AddBeam(furniture, "hearth_log_0", new Vector3(-0.18f, 0.15f, -0.13f),
            new Vector3(0.18f, 0.15f, 0.13f), 0.035f, heartwood);
        AddBeam(furniture, "hearth_log_1", new Vector3(-0.18f, 0.16f, 0.13f),
            new Vector3(0.18f, 0.16f, -0.13f), 0.035f, bark);

        // A small permanent spit makes this read as built household furniture.
        AddBeam(furniture, "stick_spit_post_l", new Vector3(-0.32f, 0.10f, 0f),
            new Vector3(-0.32f, 0.42f, 0f), 0.022f, bark);
        AddBeam(furniture, "stick_spit_post_r", new Vector3(0.32f, 0.10f, 0f),
            new Vector3(0.32f, 0.42f, 0f), 0.022f, bark);
        AddBeam(furniture, "stick_bar", new Vector3(-0.35f, 0.40f, 0f),
            new Vector3(0.35f, 0.40f, 0f), 0.020f, heartwood);
        AddCylinder(furniture, "rope_spit_l", new Vector3(-0.32f, 0.39f, 0f),
            Quaternion.identity, 0.035f, 0.035f, rope);
        AddCylinder(furniture, "rope_spit_r", new Vector3(0.32f, 0.39f, 0f),
            Quaternion.identity, 0.035f, 0.035f, rope);

        var firePoint = new GameObject("fire_point");
        firePoint.transform.SetParent(root.transform, false);
        firePoint.transform.localPosition = Vector3.zero;
        return root;
    }

    private static void AddCube(Transform parent, string name, Vector3 position,
        Vector3 size, Quaternion rotation, Material material)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
        FinishPiece(piece, parent, name, position, rotation, size, material);
    }

    private static void AddSphere(Transform parent, string name, Vector3 position,
        Vector3 radius, Quaternion rotation, Material material)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        FinishPiece(piece, parent, name, position, rotation, radius * 2f, material);
    }

    private static void AddCylinder(Transform parent, string name, Vector3 position,
        Quaternion rotation, float radius, float height, Material material)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        FinishPiece(piece, parent, name, position, rotation,
            new Vector3(radius, height * 0.5f, radius), material);
    }

    private static void AddBeam(Transform parent, string name, Vector3 from,
        Vector3 to, float radius, Material material)
    {
        var delta = to - from;
        AddCylinder(parent, name, (from + to) * 0.5f,
            Quaternion.FromToRotation(Vector3.up, delta.normalized),
            radius, delta.magnitude, material);
    }

    private static void FinishPiece(GameObject piece, Transform parent, string name,
        Vector3 position, Quaternion rotation, Vector3 scale, Material material)
    {
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = position;
        piece.transform.localRotation = rotation;
        piece.transform.localScale = scale;
        var collider = piece.GetComponent<Collider>();
        if (collider != null)
        {
            if (Application.isPlaying) Object.Destroy(collider);
            else Object.DestroyImmediate(collider);
        }
        var renderer = piece.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
            renderer.receiveShadows = true;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }
    }

    private static Material Material(string sourceName, Color fallback, bool emission = false)
    {
        var key = sourceName + (emission ? ":emission" : string.Empty);
        if (Materials.TryGetValue(key, out var cached) && cached != null) return cached;

        Material? source = null;
        var hut = Resources.Load<GameObject>(HutPrefabPath);
        if (hut != null)
        {
            foreach (var renderer in hut.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var candidate in renderer.sharedMaterials)
                {
                    if (candidate != null &&
                        candidate.name.IndexOf(sourceName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        source = candidate;
                        break;
                    }
                }
                if (source != null) break;
            }
        }

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = source != null ? new Material(source) : new Material(shader!);
        material.name = $"Hut furniture {sourceName}";
        if (source == null)
        {
            material.color = fallback;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", fallback);
        }
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.07f);
        if (emission)
        {
            material.EnableKeyword("_EMISSION");
            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", fallback * 0.45f);
        }
        material.enableInstancing = true;
        Materials[key] = material;
        return material;
    }
}

}
