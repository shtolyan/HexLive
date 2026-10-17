#nullable enable
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>
/// §173.2: small hexagonal cursor for choosing an exact planting junction.
/// Green means the snapshot sees a free dry point; red means the click will
/// stay in placement mode. The simulation validates the same point again.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlantingPlacementMarker : MonoBehaviour
{
    private const float OuterRadius = 0.34f;
    private const float InnerRadius = 0.25f;

    private static readonly Color ValidColor = new(0.42f, 0.86f, 0.31f, 0.92f);
    private static readonly Color InvalidColor = new(0.92f, 0.25f, 0.20f, 0.92f);
    private static PlantingPlacementMarker? _instance;

    private MeshRenderer _renderer = null!;
    private MaterialPropertyBlock _block = null!;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _instance = null;

    public static void Show(Vector3 groundPosition, bool valid)
    {
        var marker = _instance != null ? _instance : Create();
        if (marker == null)
        {
            return;
        }

        marker.transform.position = groundPosition + new Vector3(0f, 0.035f, 0f);
        marker._renderer.enabled = true;
        marker.ApplyColor(valid ? ValidColor : InvalidColor);
    }

    public static void Hide()
    {
        if (_instance != null)
        {
            _instance._renderer.enabled = false;
        }
    }

    private static PlantingPlacementMarker? Create()
    {
        var go = new GameObject("HexLive/PlantingPlacementMarker");
        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = BuildRingMesh();

        var marker = go.AddComponent<PlantingPlacementMarker>();
        marker._renderer = go.AddComponent<MeshRenderer>();
        marker._renderer.shadowCastingMode = ShadowCastingMode.Off;
        marker._renderer.receiveShadows = false;
        marker._renderer.enabled = false;
        marker._renderer.sortingOrder = 50;
        marker._block = new MaterialPropertyBlock();

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
            Shader.Find("Unlit/Color");
        if (shader != null)
        {
            var material = new Material(shader) { name = "PlantingPlacementMarker" };
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            marker._renderer.sharedMaterial = material;
        }

        _instance = marker;
        return marker;
    }

    private void ApplyColor(Color color)
    {
        _renderer.GetPropertyBlock(_block);
        _block.SetColor("_BaseColor", color);
        _block.SetColor("_Color", color);
        _renderer.SetPropertyBlock(_block);
    }

    private static Mesh BuildRingMesh()
    {
        var vertices = new Vector3[12];
        for (var i = 0; i < 6; i++)
        {
            // Thirty degrees puts a flat edge at the top/bottom of the cursor,
            // matching the pointy-top world hexes.
            var angle = Mathf.Deg2Rad * (30f + i * 60f);
            var x = Mathf.Cos(angle);
            var z = Mathf.Sin(angle);
            vertices[i] = new Vector3(x * OuterRadius, 0f, z * OuterRadius);
            vertices[i + 6] = new Vector3(x * InnerRadius, 0f, z * InnerRadius);
        }

        var triangles = new int[36];
        for (var i = 0; i < 6; i++)
        {
            var next = (i + 1) % 6;
            var offset = i * 6;
            triangles[offset] = i;
            triangles[offset + 1] = next;
            triangles[offset + 2] = next + 6;
            triangles[offset + 3] = i;
            triangles[offset + 4] = next + 6;
            triangles[offset + 5] = i + 6;
        }

        var mesh = new Mesh { name = "PlantingPlacementHex" };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}

}
