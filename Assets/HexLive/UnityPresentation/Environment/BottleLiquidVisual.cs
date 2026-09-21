using HexLive.Simulation.Agents;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HexLive.UnityPresentation.Environment
{
/// <summary>§55.5: liquid inside the authored bottle, independent of its collision/fit bounds.</summary>
[ExecuteAlways]
public sealed class BottleLiquidVisual : MonoBehaviour
{
    private Transform _liquid;
    private Renderer _renderer;
    private Mesh _mesh;
    private Material _material;
    private MeshFilter _bottleFilter;
    private Renderer _bottleRenderer;
    private Mesh _originalBottleMesh;
    private Mesh _bottleMesh;
    private Material[] _originalBottleMaterials;
    private Material _glassMaterial;
    private float _height;
    private Vector3 _bottom;
    private Vector3 _up;
    private float _shown = -1f;
    private float _target;

    public static void Sync(GameObject bottle, float fill, WaterKind appearance)
    {
        if (bottle == null) return;
        var visual = bottle.GetComponent<BottleLiquidVisual>();
        if (visual == null) visual = bottle.AddComponent<BottleLiquidVisual>();
        visual.SetContents(fill, appearance);
    }

    public void SetContents(float fill, WaterKind appearance)
    {
        if (_liquid == null && !Initialize()) return;
        _target = Mathf.Clamp01(float.IsNaN(fill) ? 0f : fill);
        // First snapshot is exact; later snapshots ease without quantizing percentages.
        if (_shown < 0f || !Application.isPlaying) _shown = _target;
        var color = appearance switch
        {
            WaterKind.Coconut => new Color(0.74f, 0.79f, 0.56f, 1f),
            WaterKind.Raw => new Color(0.29f, 0.39f, 0.20f, 1f),
            WaterKind.Boiled => new Color(0.37f, 0.65f, 0.75f, 1f),
            _ => new Color(0.15f, 0.55f, 0.72f, 1f)
        };
        _material.SetColor("_BaseColor", color);
        _material.SetColor("_Color", color);
        ApplyLevel();
    }

    private void Update()
    {
        if (_liquid == null || Mathf.Approximately(_shown, _target)) return;
        _shown = Mathf.MoveTowards(_shown, _target, Time.deltaTime * 2f);
        ApplyLevel();
    }

    private void ApplyLevel()
    {
        _renderer.enabled = _shown > 0f;
        _liquid.localPosition = _bottom;
        _liquid.localRotation = Quaternion.FromToRotation(Vector3.up, _up);
        _liquid.localScale = new Vector3(1f, _height * _shown, 1f);
    }

    private bool Initialize()
    {
        // Read the source mesh BEFORE creating liquid; never feed liquid back into ObjectFit.
        // Verified canonical tool_bottle_native: one lowpoly mesh, 2266 vertices,
        // local bounds (.30988, .30855, 1.00096), +Z up behind the X=-90 wrapper.
        // The 5..75% height/36% radius profile clears its indented base and shoulder.
        var filter = GetComponentInChildren<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return false;
        var bounds = filter.sharedMesh.bounds;
        var axis = bounds.size.z > bounds.size.y && bounds.size.z > bounds.size.x ? 2
            : bounds.size.x > bounds.size.y ? 0 : 1;
        var localUp = axis == 2 ? Vector3.forward : axis == 0 ? Vector3.right : Vector3.up;
        MakeBodyTransparent(filter, axis);
        _up = transform.InverseTransformDirection(filter.transform.TransformDirection(localUp));
        var basePoint = bounds.center - localUp * (bounds.size[axis] * 0.45f);
        _bottom = transform.InverseTransformPoint(filter.transform.TransformPoint(basePoint));
        _height = transform.InverseTransformVector(filter.transform.TransformVector(localUp * bounds.size[axis] * 0.70f)).magnitude;
        var radialA = (axis + 1) % 3;
        var radialB = (axis + 2) % 3;
        // Keep a visible air gap at the shoulder and clear the bottle wall.
        var radius = Mathf.Min(bounds.size[radialA], bounds.size[radialB]) * 0.36f;
        var scale = transform.InverseTransformVector(filter.transform.TransformVector(Vector3.one)).magnitude / Mathf.Sqrt(3f);
        radius *= scale;
        var liquid = new GameObject("Bottle liquid");
        _liquid = liquid.transform;
        _liquid.SetParent(transform, false);
        _mesh = BuildMesh(radius);
        liquid.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = liquid.AddComponent<MeshRenderer>();
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        _material = new Material(shader) { name = "Bottle liquid (instance)" };
        _material.SetFloat("_Smoothness", 0.25f);
        _renderer.sharedMaterial = _material;
        return true;
    }

    private void MakeBodyTransparent(MeshFilter filter, int axis)
    {
        var renderer = filter.GetComponent<Renderer>();
        var source = filter.sharedMesh;
        // The canonical bottle has one readable mesh and one texture/material for
        // body AND cap. Preserve the cap/neck's original material: changing alpha
        // of the entire renderer would make the blue screw cap see-through too.
        if (renderer == null || !source.isReadable || source.subMeshCount != 1 ||
            renderer.sharedMaterials.Length != 1 ||
            !renderer.sharedMaterial.name.StartsWith("BottleTransparent", System.StringComparison.Ordinal)) return;
        _bottleFilter = filter;
        _bottleRenderer = renderer;
        _originalBottleMesh = source;
        _originalBottleMaterials = renderer.sharedMaterials;
        _glassMaterial = new Material(renderer.sharedMaterial) { name = "BottleTransparent body (instance)" };
        var color = _glassMaterial.GetColor("_BaseColor");
        color.a = .28f;
        _glassMaterial.SetColor("_BaseColor", color);
        _glassMaterial.SetColor("_Color", color);
        var vertices = source.vertices;
        var triangles = source.triangles;
        var body = new List<int>();
        var cap = new List<int>();
        var capBottom = source.bounds.min[axis] + source.bounds.size[axis] * .90f;
        for (var i = 0; i < triangles.Length; i += 3)
        {
            // Keep any triangle touching the top 10% on the original material.
            var target = vertices[triangles[i]][axis] >= capBottom ||
                vertices[triangles[i + 1]][axis] >= capBottom ||
                vertices[triangles[i + 2]][axis] >= capBottom ? cap : body;
            target.Add(triangles[i]); target.Add(triangles[i + 1]); target.Add(triangles[i + 2]);
        }
        _bottleMesh = Instantiate(source);
        _bottleMesh.name = source.name + " (liquid visibility)";
        _bottleMesh.subMeshCount = 2;
        _bottleMesh.SetTriangles(body, 0);
        _bottleMesh.SetTriangles(cap, 1);
        filter.sharedMesh = _bottleMesh;
        renderer.sharedMaterials = new[] { _glassMaterial, _originalBottleMaterials[0] };
    }

    // Twelve sides, flat normals and a closed top. Vertices span y=0..1.
    private static Mesh BuildMesh(float radius)
    {
        const int sides = 12;
        var vertices = new Vector3[sides * 12];
        var triangles = new int[vertices.Length];
        var index = 0;
        for (var i = 0; i < sides; i++)
        {
            var a = 2f * Mathf.PI * i / sides;
            var b = 2f * Mathf.PI * (i + 1) / sides;
            var p = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            var q = new Vector3(Mathf.Cos(b) * radius, 0f, Mathf.Sin(b) * radius);
            foreach (var v in new[] { p, p + Vector3.up, q + Vector3.up, p, q + Vector3.up, q,
                Vector3.up, q + Vector3.up, p + Vector3.up, Vector3.zero, p, q })
            {
                vertices[index] = v;
                triangles[index] = index++;
            }
        }
        var mesh = new Mesh { name = "Bottle liquid volume" };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void OnDestroy()
    {
        if (_bottleFilter != null && _bottleFilter.sharedMesh == _bottleMesh)
            _bottleFilter.sharedMesh = _originalBottleMesh;
        if (_bottleRenderer != null && _originalBottleMaterials != null)
            _bottleRenderer.sharedMaterials = _originalBottleMaterials;
        Dispose(_mesh); Dispose(_material); Dispose(_bottleMesh); Dispose(_glassMaterial);
        if (_liquid != null) Dispose(_liquid.gameObject);
    }

    private static void Dispose(Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }
}
}
