#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>
/// §121: куда приказали идти. Кольцо на земле, гаснущее за полсекунды.
///
/// Зачем вообще: приказ идти на дальний край острова не даёт мгновенного
/// отклика — колонистка разворачивается через тик-другой, и без метки клик
/// читается как непринятый. Это единственная задача метки, поэтому она и
/// живёт ровно столько, сколько нужно глазу.
///
/// Проекторов и декалей в проекте нет, поэтому это плоский меш с прозрачным
/// материалом, лежащий чуть выше земли. §121.13 r2: форма — шестиугольное
/// кольцо, повёрнутое как гексы мира (вершины на ±Z, как в
/// <c>PointInsideHex</c>), а не квадрат: метка говорит на языке карты.
/// </summary>
[DisallowMultipleComponent]
public sealed class DestinationMarker : MonoBehaviour
{
    private const float LifetimeSeconds = 0.55f;
    private const float StartScale = 1.1f;
    private const float EndScale = 0.45f;
    // Радиусы кольца в долях единичной метки: внешний — до вершины.
    internal const float HexOuterRadius = 0.55f;
    internal const float HexInnerRadius = 0.36f;

    private static DestinationMarker? _instance;

    private MeshRenderer _renderer = null!;
    private MaterialPropertyBlock _block = null!;
    private float _shownAt = -1f;
    private Color _color = new(0.941f, 0.706f, 0.361f, 1f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => _instance = null;

    public static void Show(Vector3 groundPosition)
    {
        var marker = _instance != null ? _instance : Create();
        if (marker == null)
        {
            return;
        }

        marker.transform.position = groundPosition + new Vector3(0f, 0.03f, 0f);
        marker._shownAt = Time.unscaledTime;
        marker._renderer.enabled = true;
    }

    /// <summary>
    /// Шестиугольное кольцо в плоскости XZ, нормаль вверх. Вершина k — под
    /// углом 90° + 60°·k от оси X, то есть «острым верхом» по Z, как тайлы
    /// (<c>HexSpatialMath</c>: |dz| ≤ R и √3·|dx| + |dz| ≤ √3·R).
    /// </summary>
    internal static Mesh BuildHexRingMesh(float outer, float inner)
    {
        var vertices = new Vector3[12];
        for (var k = 0; k < 6; k++)
        {
            var angle = Mathf.Deg2Rad * (90f + 60f * k);
            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            vertices[k] = direction * outer;
            vertices[k + 6] = direction * inner;
        }

        var triangles = new int[36];
        for (var k = 0; k < 6; k++)
        {
            var next = (k + 1) % 6;
            var t = k * 6;
            // По часовой стрелке при взгляде сверху — лицевая сторона к камере.
            triangles[t] = k; triangles[t + 1] = k + 6; triangles[t + 2] = next;
            triangles[t + 3] = next; triangles[t + 4] = k + 6; triangles[t + 5] = next + 6;
        }

        var normals = new Vector3[12];
        for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
        var mesh = new Mesh { name = "DestinationMarkerHex", vertices = vertices, triangles = triangles, normals = normals };
        mesh.RecalculateBounds();
        return mesh;
    }

    private static DestinationMarker? Create()
    {
        var go = new GameObject("HexLive/DestinationMarker");
        // Ни коллайдера, ни физического пикинга: метка не должна ловить лучи.
        go.AddComponent<MeshFilter>().sharedMesh = BuildHexRingMesh(HexOuterRadius, HexInnerRadius);
        go.AddComponent<MeshRenderer>();

        var marker = go.AddComponent<DestinationMarker>();
        marker._renderer = go.GetComponent<MeshRenderer>();
        marker._renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        marker._renderer.receiveShadows = false;
        marker._renderer.enabled = false;
        marker._block = new MaterialPropertyBlock();

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        if (shader != null)
        {
            var material = new Material(shader) { name = "DestinationMarker" };
            material.SetFloat("_Surface", 1f); // прозрачный
            material.SetFloat("_Cull", 0f); // кольцо видно с любой стороны
            material.renderQueue = 3000;
            marker._renderer.sharedMaterial = material;
        }

        _instance = marker;
        return marker;
    }

    private void Update()
    {
        if (_shownAt < 0f)
        {
            return;
        }

        var age = (Time.unscaledTime - _shownAt) / LifetimeSeconds;
        if (age >= 1f)
        {
            _renderer.enabled = false;
            _shownAt = -1f;
            return;
        }

        var scale = Mathf.Lerp(StartScale, EndScale, age);
        transform.localScale = new Vector3(scale, 1f, scale);

        _renderer.GetPropertyBlock(_block);
        _block.SetColor("_BaseColor", new Color(_color.r, _color.g, _color.b, 1f - age));
        _renderer.SetPropertyBlock(_block);
    }
}

}
