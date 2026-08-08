#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

/// <summary>
/// §118: куда приказали идти. Кольцо на земле, гаснущее за полсекунды.
///
/// Зачем вообще: приказ идти на дальний край острова не даёт мгновенного
/// отклика — колонистка разворачивается через тик-другой, и без метки клик
/// читается как непринятый. Это единственная задача метки, поэтому она и
/// живёт ровно столько, сколько нужно глазу.
///
/// Проекторов и декалей в проекте нет, поэтому это просто квад с прозрачным
/// материалом, лежащий чуть выше земли.
/// </summary>
[DisallowMultipleComponent]
public sealed class DestinationMarker : MonoBehaviour
{
    private const float LifetimeSeconds = 0.55f;
    private const float StartScale = 1.1f;
    private const float EndScale = 0.45f;

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

    private static DestinationMarker? Create()
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "HexLive/DestinationMarker";
        // Примитив приезжает с коллайдером, а физического пикинга в проекте
        // нет — коллайдер здесь только мешал бы будущим лучам.
        var collider = quad.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }

        quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        var marker = quad.AddComponent<DestinationMarker>();
        marker._renderer = quad.GetComponent<MeshRenderer>();
        marker._renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        marker._renderer.receiveShadows = false;
        marker._renderer.enabled = false;
        marker._block = new MaterialPropertyBlock();

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        if (shader != null)
        {
            var material = new Material(shader) { name = "DestinationMarker" };
            material.SetFloat("_Surface", 1f); // прозрачный
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
        transform.localScale = new Vector3(scale, scale, 1f);

        _renderer.GetPropertyBlock(_block);
        _block.SetColor("_BaseColor", new Color(_color.r, _color.g, _color.b, 1f - age));
        _renderer.SetPropertyBlock(_block);
    }
}

}
