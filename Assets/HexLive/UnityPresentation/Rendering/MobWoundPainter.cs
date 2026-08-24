#nullable enable
using HexLive.UnityPresentation.Wearing;
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
public sealed class MobWoundPainter : MonoBehaviour, HexLive.UnityPresentation.Wearing.IPaintTarget
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

    /// <summary>Spec 40.8-G: load the shared wound art behind the loading
    /// curtain instead of on the first landed bite (File.Read burst).</summary>
    public static void Prewarm()
    {
        if (_artLoaded && _texSplash != null &&
            System.Array.TrueForAll(_woundOver, value => value != null))
        {
            return;
        }

        _artLoaded = true;
        _texSplash = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>("HexLive/Decals/blood_splash");
        _woundOver = new Texture2D?[WoundVariantNames.Length];
        for (var i = 0; i < WoundVariantNames.Length; i++)
        {
            _woundOver[i] = HexLive.UnityPresentation.Content.AtomicResources.Load<Texture2D>($"HexLive/Decals/{WoundVariantNames[i]}");
        }
    }

    private int _mobEntityId;
    private SkinnedMeshRenderer? _renderer;
    private Material? _material;
    private Texture2D? _originalAlbedo;
    private RenderTexture? _rt;
    private int _placed;
    private bool _initFailed;
    private bool _baseMapBound;

    // Spec 40.8-G: editor-baked surface points (area-weighted samples over
    // the pelt) — with a map the mesh is never read at runtime, so wounds
    // work even on meshes that ship without Read/Write (the legacy path
    // silently disabled them in builds).
    private PaintPointMap.Point[] _mapPoints = System.Array.Empty<PaintPointMap.Point>();

    // Legacy fallback (no map): cumulative triangle areas for weighted
    // random surface sampling — requires a CPU-readable mesh.
    private Vector2[]? _uv;
    private int[]? _triangles;
    private float[]? _cumulativeArea;

    // Spec 40.8-G repaint coalescing: state changes only mark dirty; the
    // actual blit+stamps+mips runs at most once per interval (a pack fight
    // lands several bites per second — one composite covers them all).
    private bool _repaintDirty;
    private float _lastRepaintTime;
    // Spec 40.8-K: same reasoning as the garment painter — one per mob, and
    // a pelt's wound field does not need four looks a second.
    private const float RepaintIntervalSeconds = 1f;

    public void Configure(int mobEntityId, int maxStamps)
    {
        _mobEntityId = mobEntityId;
        _maxStamps = Mathf.Max(1, maxStamps);
        enabled = false; // LateUpdate runs only while a repaint is pending
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
        _repaintDirty = true;
        enabled = true;
    }

    private void LateUpdate()
    {
        // Driven by SkinPaintScheduler (spec 40.8-K) — painting here too
        // would bypass its per-frame budget.
        enabled = false;
    }

    /// <summary>IPaintTarget: a pelt has no cheap "just appeared" path.</summary>
    public bool WantsFreshPass => false;

    public void PaintFresh()
    {
    }

    /// <summary>IPaintTarget: this mob's scheduled turn.</summary>
    public void PaintCycle()
    {
        if (!_repaintDirty)
        {
            return;
        }

        _repaintDirty = false;
        _lastRepaintTime = Time.unscaledTime;
        enabled = false;
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
        if (_renderer == null || mesh == null)
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

        Prewarm();

        // Spec 40.8-G: editor-baked surface points first — no mesh reads at
        // all (and the only path that works on non-readable meshes).
        var map = PaintPointMap.Load($"mob_{mesh.name}_{mesh.vertexCount}", mesh.vertexCount);
        _mapPoints = map != null
            ? map.PointsFor(PaintPointMap.MobSurfaceZone)
            : System.Array.Empty<PaintPointMap.Point>();

        if (_mapPoints.Length == 0)
        {
            // Legacy fallback: bind-pose topology + area table (stamps live
            // in UV space, animation moves the painted skin for free).
            if (!mesh.isReadable)
            {
                _initFailed = true;
                return false;
            }

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
                var uv = _mapPoints.Length > 0
                    ? _mapPoints[rng.Next(_mapPoints.Length)].Uv
                    : StampUv(i, rng);
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
            // Bind once — later repaints only refresh the RT contents.
            if (!_baseMapBound)
            {
                _baseMapBound = true;
                _material.SetTexture("_BaseMap", _rt);
            }
        }
        catch (System.Exception e)
        {
            RenderTexture.active = previous;
            _baseMapBound = false;
            _material.SetTexture("_BaseMap", _originalAlbedo);
            Debug.LogWarning($"[MobWounds] mob{_mobEntityId}: repaint failed, original restored — {e.Message}");
        }
    }

    private void Awake() =>
        HexLive.UnityPresentation.Wearing.SkinPaintScheduler.Register(this);

    private void OnDestroy()
    {
        HexLive.UnityPresentation.Wearing.SkinPaintScheduler.Unregister(this);

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
