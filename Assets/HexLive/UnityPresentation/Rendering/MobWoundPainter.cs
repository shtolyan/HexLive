#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

// Persistent bite/strike wounds on any mob — SkinTexturePainter's stamp tech
// copied down to its albedo-only core (spec §29C.3): the pelt texture is
// blitted into a RenderTexture once and blood stamps (the SAME
// Resources/HexLive/Decals art the NPC skin uses — blood-only, per the art
// rule) are drawn into it at mesh-random UV spots. No zones/bones: a mob's
// readout doesn't need per-limb precision, so each stamp lands on a random
// surface triangle (area-weighted, so big flanks get more than ear tips).
//
// Wound COUNT is derived, not event-driven: stamps = ceil(lostHealth ×
// maxStamps), with every placement seeded from the mob id + stamp index —
// idempotent per snapshot, deterministic across save/load (a half-dead wolf
// reloads with the exact same wound pattern), and no per-hit bookkeeping.
public sealed class MobWoundPainter : MonoBehaviour
{
    // A wolf dies around 8-10 landed strikes — one stamp per ~12% lost HP
    // keeps a mauled mob visibly shredded without tiling the whole pelt.
    // Per-mob override via MobConfig.maxWoundStamps.
    private int _maxStamps = 8;
    private const int MaxRenderTextureSize = 1024;

    // Same variant table as SkinTexturePainter (blood-only art).
    private static readonly string[] WoundVariantNames =
    {
        "wound_scratch", "blood_splat",
        "wound_gash_slash", "wound_gash_streak", "wound_gash_smear",
        "wound_gash_fork", "wound_gash_torn",
    };

    private static Texture2D?[] _woundOver = System.Array.Empty<Texture2D?>();
    private static Texture2D? _texSplash;
    private static bool _artLoaded;

    // No-domain-reload runs keep statics between plays.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticArtCache()
    {
        _artLoaded = false;
        _texSplash = null;
        _woundOver = System.Array.Empty<Texture2D?>();
    }

    private int _mobEntityId;
    private SkinnedMeshRenderer? _renderer;
    private Material? _material;
    private Texture2D? _originalAlbedo;
    private RenderTexture? _rt;
    private int _placed;
    private bool _initFailed;

    // Cumulative triangle areas for weighted random surface sampling.
    private Vector2[]? _uv;
    private int[]? _triangles;
    private float[]? _cumulativeArea;

    public void Configure(int mobEntityId, int maxStamps)
    {
        _mobEntityId = mobEntityId;
        _maxStamps = Mathf.Max(1, maxStamps);
    }

    /// <summary>Renderer feeds every snapshot's health; stamps catch up.</summary>
    public void SetHealth(float health)
    {
        if (_initFailed)
        {
            return;
        }

        var desired = Mathf.Clamp(
            Mathf.CeilToInt((1f - Mathf.Clamp01(health)) * _maxStamps), 0, _maxStamps);
        if (desired <= _placed)
        {
            return; // wounds never un-paint (dogs don't heal mid-scene)
        }

        if (!EnsureInitialized())
        {
            return;
        }

        _placed = desired;
        Repaint();
    }

    private bool EnsureInitialized()
    {
        if (_rt != null)
        {
            return true;
        }

        _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
        var mesh = _renderer != null ? _renderer.sharedMesh : null;
        if (_renderer == null || mesh == null || !mesh.isReadable)
        {
            _initFailed = true;
            return false;
        }

        // .material (not shared): the RT must bind to THIS wolf only.
        _material = _renderer.material;
        _originalAlbedo = _material.GetTexture("_BaseMap") as Texture2D;
        if (_originalAlbedo == null)
        {
            _initFailed = true;
            return false;
        }

        if (!_artLoaded)
        {
            _artLoaded = true;
            _texSplash = Resources.Load<Texture2D>("HexLive/Decals/blood_splash");
            _woundOver = new Texture2D?[WoundVariantNames.Length];
            for (var i = 0; i < WoundVariantNames.Length; i++)
            {
                _woundOver[i] = Resources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}");
            }
        }

        // Bind-pose topology is enough — stamps live in UV space, animation
        // moves the painted skin with the mesh for free.
        _uv = mesh.uv;
        _triangles = mesh.triangles;
        if (_uv.Length == 0 || _triangles.Length < 3)
        {
            _initFailed = true;
            return false;
        }

        var vertices = mesh.vertices;
        var triCount = _triangles.Length / 3;
        _cumulativeArea = new float[triCount];
        var total = 0f;
        for (var t = 0; t < triCount; t++)
        {
            var a = vertices[_triangles[t * 3]];
            var b = vertices[_triangles[t * 3 + 1]];
            var c = vertices[_triangles[t * 3 + 2]];
            total += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            _cumulativeArea[t] = total;
        }

        if (total <= 0f)
        {
            _initFailed = true;
            return false;
        }

        var w = Mathf.Min(_originalAlbedo.width, MaxRenderTextureSize);
        var h = Mathf.Min(_originalAlbedo.height, MaxRenderTextureSize);
        // Explicit sRGB, mips regenerated after stamping — same recipe as
        // SkinTexturePainter.RepaintSlot.
        _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB)
        {
            name = $"MobWounds_{_mobEntityId}",
            useMipMap = true,
            autoGenerateMips = false,
            filterMode = _originalAlbedo.filterMode,
            wrapMode = _originalAlbedo.wrapMode
        };
        _rt.Create();
        return true;
    }

    // Deterministic random UV on the pelt for stamp #index.
    private Vector2 StampUv(int index, System.Random rng)
    {
        // Area-weighted triangle pick.
        var target = (float)rng.NextDouble() * _cumulativeArea![^1];
        var lo = 0;
        var hi = _cumulativeArea.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_cumulativeArea[mid] < target) { lo = mid + 1; } else { hi = mid; }
        }

        // Uniform barycentric point inside it.
        var r1 = Mathf.Sqrt((float)rng.NextDouble());
        var r2 = (float)rng.NextDouble();
        var w0 = 1f - r1;
        var w1 = r1 * (1f - r2);
        var w2 = r1 * r2;
        var uv0 = _uv![_triangles![lo * 3]];
        var uv1 = _uv[_triangles[lo * 3 + 1]];
        var uv2 = _uv[_triangles[lo * 3 + 2]];
        return uv0 * w0 + uv1 * w1 + uv2 * w2;
    }

    // Graphics.DrawTexture doubles the colour, so 0.5 gray = neutral
    // (SkinTexturePainter.StampTint verbatim).
    private static Color StampTint(float alpha) => new(0.5f, 0.5f, 0.5f, alpha * 0.5f);

    private void Repaint()
    {
        if (_rt == null || _originalAlbedo == null || _material == null)
        {
            return;
        }

        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(_originalAlbedo, _rt);
            RenderTexture.active = _rt;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, 1f, 1f, 0f); // (0,0) top-left, UV v flips below

            for (var i = 0; i < _placed; i++)
            {
                var rng = new System.Random(_mobEntityId * 7919 + i * 131);
                var uv = StampUv(i, rng);
                var cx = uv.x;
                var cy = 1f - uv.y;
                var size = 0.10f + 0.06f * (float)rng.NextDouble();

                // Wider pale splash under, detailed gash art on top — the
                // NPC skin recipe.
                if (_texSplash != null)
                {
                    var s = size * 1.6f;
                    Graphics.DrawTexture(new Rect(cx - s * 0.5f, cy - s * 0.5f, s, s),
                        _texSplash, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(0.5f));
                }

                var over = _woundOver.Length > 0 ? _woundOver[rng.Next(_woundOver.Length)] : null;
                if (over != null)
                {
                    Graphics.DrawTexture(new Rect(cx - size * 0.5f, cy - size * 0.5f, size, size),
                        over, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, StampTint(1f));
                }
            }

            GL.PopMatrix();
            RenderTexture.active = previous;
            _rt.GenerateMips();
            _material.SetTexture("_BaseMap", _rt);
        }
        catch (System.Exception e)
        {
            RenderTexture.active = previous;
            _material.SetTexture("_BaseMap", _originalAlbedo);
            Debug.LogWarning($"[MobWounds] mob{_mobEntityId}: repaint failed, original restored — {e.Message}");
        }
    }

    private void OnDestroy()
    {
        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
        }

        if (_material != null)
        {
            Destroy(_material); // the .material instance we created
        }
    }
}

}
