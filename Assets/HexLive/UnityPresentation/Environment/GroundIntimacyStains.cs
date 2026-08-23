#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HexLive.UnityPresentation.Environment
{

/// <summary>§127: a pale blood-shaped ground decal spawned once at the
/// completed scene's female pelvis. It stays where it landed and exposed rain
/// washes it away through simulation ticks, just like body soil.</summary>
public sealed class GroundIntimacyStains : MonoBehaviour
{
    private const int MaxStains = 32;
    private const float RainWashPerTick = 0.0003125f;
    private const float ProjectorHover = 0.06f;
    private const float ProjectorDepth = 0.9f;
    private const float Alpha = 0.82f;
    private const float ScaleMin = 0.18f;
    private const float ScaleMax = 0.28f;

    private sealed class Stain
    {
        public GameObject Root = null!;
        public DecalProjector Projector = null!;
        public int BornTick;
        public int LastTick;
        public float Soil = 1f;
        public bool RainExposed;
    }

    private readonly List<Stain> _stains = new();
    private Material? _material;
    private Texture2D? _texture;
    private bool _shaderWarningShown;

    public void Spawn(int sourceNpcId, Vector3 groundWorld, int tick,
        bool rainExposed)
    {
        EnsureMaterial();
        if (_material == null)
        {
            return;
        }

        if (_stains.Count >= MaxStains)
        {
            var oldest = 0;
            for (var i = 1; i < _stains.Count; i++)
            {
                if (_stains[i].BornTick < _stains[oldest].BornTick)
                {
                    oldest = i;
                }
            }

            Destroy(_stains[oldest].Root);
            _stains.RemoveAt(oldest);
        }

        var seed = unchecked((uint)(sourceNpcId * 73856093 ^ tick * 19349663));
        var spin = Seed01(seed) * 360f;
        var scale = Mathf.Lerp(ScaleMin, ScaleMax, Seed01(seed ^ 0x9e3779b9u));
        var go = new GameObject("IntimacyGroundStain");
        go.transform.SetParent(transform, false);
        go.transform.position = groundWorld + Vector3.up * ProjectorHover;
        go.transform.rotation = Quaternion.Euler(90f, spin, 0f);

        var projector = go.AddComponent<DecalProjector>();
        projector.material = _material;
        projector.size = new Vector3(scale, scale, ProjectorDepth);
        // After the 90° tilt local +Z points down. Keeping the whole projection
        // box below its top face lets the puddle hug terrain without painting
        // the woman's legs above it.
        projector.pivot = new Vector3(0f, 0f, ProjectorDepth * 0.5f);
        projector.fadeFactor = Alpha;
        projector.renderingLayerMask = uint.MaxValue;

        _stains.Add(new Stain
        {
            Root = go,
            Projector = projector,
            BornTick = tick,
            LastTick = tick,
            RainExposed = rainExposed
        });
    }

    public void Advance(int tick, bool raining)
    {
        for (var i = _stains.Count - 1; i >= 0; i--)
        {
            var stain = _stains[i];
            var elapsed = tick - stain.LastTick;
            if (elapsed < 0)
            {
                Destroy(stain.Root);
                _stains.RemoveAt(i);
                continue;
            }

            stain.LastTick = tick;
            if (raining && stain.RainExposed && elapsed > 0)
            {
                stain.Soil = Mathf.Max(0f,
                    stain.Soil - elapsed * RainWashPerTick);
            }

            if (stain.Soil <= 0.01f)
            {
                Destroy(stain.Root);
                _stains.RemoveAt(i);
                continue;
            }

            stain.Projector.fadeFactor = Alpha * stain.Soil;
        }
    }

    private void EnsureMaterial()
    {
        if (_material != null)
        {
            return;
        }

        var shader = Shader.Find("Shader Graphs/Decal");
        if (shader == null)
        {
            if (!_shaderWarningShown)
            {
                _shaderWarningShown = true;
                Debug.LogWarning("[GroundIntimacyStains] decal shader missing; ground stains disabled.");
            }
            return;
        }

        _texture = MakeTexture();
        _material = new Material(shader) { name = "Ground Intimacy Stain" };
        _material.SetTexture("Base_Map", _texture);
        _material.SetTexture("_BaseMap", _texture);
        var white = new Color(1f, 0.99f, 0.94f, 1f);
        if (_material.HasProperty("_BaseColor"))
        {
            _material.SetColor("_BaseColor", white);
        }
        if (_material.HasProperty("_Color"))
        {
            _material.SetColor("_Color", white);
        }
    }

    private static Texture2D MakeTexture()
    {
        const int size = 256;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "Ground Intimacy Stain Texture",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4
        };
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size - 0.5f;
                var v = (y + 0.5f) / size - 0.5f;
                pixels[y * size + x] = IntimacyPixel(u, v);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(true);
        return texture;
    }

    // The same ragged-pool silhouette as blood: one central puddle plus nine
    // satellite droplets, recoloured ivory with a translucent wet edge.
    private static Color IntimacyPixel(float u, float v)
    {
        var angle = Mathf.Atan2(v, u);
        var radius = Mathf.Sqrt(u * u + v * v);
        var raggedRadius = 0.24f + 0.06f * Mathf.Sin(angle * 6f + 1.3f) +
            0.035f * Mathf.Sin(angle * 13f + 4.1f);
        var pool = Mathf.Clamp01((raggedRadius - radius) / 0.05f);
        var drops = 0f;
        for (var i = 0; i < 9; i++)
        {
            var dropAngle = i * 2.399f + 0.7f;
            var dropRadius = 0.28f + 0.16f * Frac(
                Mathf.Sin(i * 12.9898f) * 43758.5453f);
            var dx = u - Mathf.Cos(dropAngle) * dropRadius;
            var dy = v - Mathf.Sin(dropAngle) * dropRadius;
            var dropSize = 0.012f + 0.02f * Frac(
                Mathf.Sin(i * 78.233f) * 12543.123f);
            drops = Mathf.Max(drops, Mathf.Clamp01(
                (dropSize - Mathf.Sqrt(dx * dx + dy * dy)) /
                (dropSize * 0.5f)));
        }

        var alpha = Mathf.Max(pool, drops * 0.9f);
        var center = Mathf.Clamp01((0.13f - radius) / 0.13f);
        var color = Color.Lerp(
            new Color(0.86f, 0.89f, 0.84f),
            new Color(1f, 0.99f, 0.94f), center);
        return new Color(color.r, color.g, color.b, alpha * 0.68f);
    }

    private static float Frac(float value) => value - Mathf.Floor(value);

    private static float Seed01(uint seed)
    {
        seed ^= seed >> 16;
        seed *= 0x7feb352du;
        seed ^= seed >> 15;
        seed *= 0x846ca68bu;
        seed ^= seed >> 16;
        return (seed & 0x00ffffffu) / 16777216f;
    }

    private void OnDestroy()
    {
        if (_material != null)
        {
            Destroy(_material);
        }
        if (_texture != null)
        {
            Destroy(_texture);
        }
    }
}

}
