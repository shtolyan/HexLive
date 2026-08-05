using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using HexLive.UnityPresentation.Rendering;

namespace HexLive.UnityPresentation.Environment
{

// Spec 40.2-C (iteration 1): blood in WATER. When a bleeding girl is in the
// water (wading or swimming) her blood doesn't pool at her feet on the ground —
// it billows into the water around her: a soft scarlet disc that spreads
// outward and slowly dilutes away. Unlike the ground stains (RVFX projectors
// that dry to bordo on sand), water blood is a flat translucent quad floating
// ON the water surface, riding the live wave swell (WaterWave), so it reads as
// blood diffusing in the sea rather than a painted puddle. It stays red/pink
// (it dilutes, it doesn't dry) and needs no cooperation from the water shader.
//
// Purely cosmetic and presentation-side: driven by snapshot ticks (respects
// pause/speed), not persisted. Detection of "in water" is the renderer's job
// (HexWorldRenderer._npcOnWater); this class only spawns/animates the discs.
//
// Behaviour: a drop grows from nothing to ~3x a land puddle's width while its
// alpha fades in lock-step (wider = more transparent), reaching alpha 0 exactly
// as it hits full spread — then it's removed. The whole billow-and-vanish takes
// 1200 ticks / 5 real minutes (~6x faster than land blood).
public sealed class WaterBloodStains : MonoBehaviour
{
    private const int MaxStains = 60;           // oldest recycled beyond this
    // 1200 ticks = 5 real minutes: the drop grows AND fades over this whole
    // span — radius 0 -> max while alpha max -> 0, then it's gone from view.
    // ~6x faster than a land stain, which lingers much longer.
    private const float LifetimeTicks = 1200f;
    private const float DripScale = 0f;         // starts from nothing, grows out
    // ~3x the earlier spread — blood billows wide as it disperses in water.
    private const float BillowScaleMin = 1.65f;
    private const float BillowScaleMax = 2.85f;
    private const float MaxAlpha = 0.75f;       // fresh; fades to 0 at full spread
    private const int DripIntervalTicks = 6;    // while bleeding, ~1.5 sim-s
    private const int BleedGraceTicks = 20;     // matches the ground stains
    private const float DropJitter = 0.14f;
    // Fresh arterial scarlet; as it dilutes it fades toward a pale pink film.
    private static readonly Color FreshBlood = new(0.72f, 0.02f, 0.04f, 1f);
    private static readonly Color DilutePink = new(0.85f, 0.30f, 0.34f, 1f);
    // A tiny lift above the surface so the disc never z-fights the water mesh.
    private const float SurfaceLift = 0.02f;

    private sealed class Stain
    {
        public Transform Root;
        public MeshRenderer Renderer;
        public MaterialPropertyBlock Mpb;
        public int BornTick;
        public float FullScale;
        public float BaseX;
        public float BaseZ;
        public float SurfaceY;
    }

    private sealed class BleedTracker
    {
        public float PrevBlood = 1f;
        public int BleedingUntilTick = -1;
        public int NextDripTick;
    }

    private readonly List<Stain> _stains = new();
    private readonly Dictionary<int, BleedTracker> _trackers = new();

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private Mesh _quad;
    private Material _material;

    // Per-NPC, once per rendered sim tick: detect blood loss and drip into the
    // water. surfaceY is the water SURFACE height at the girl (not her sunk
    // body root); x/z come from her world position.
    public void OnNpcTick(int npcId, float blood, Vector3 posWorld, float surfaceY, int tick)
    {
        var tracker = TrackerFor(npcId, blood);

        if (blood < tracker.PrevBlood - 0.0001f)
        {
            tracker.BleedingUntilTick = tick + BleedGraceTicks;
        }

        tracker.PrevBlood = blood;

        if (tick <= tracker.BleedingUntilTick)
        {
            DripIfDue(tracker, posWorld, surfaceY, tick);
        }
    }

    public void OnBleedingSourceTick(
        int sourceId, Vector3 posWorld, float surfaceY, int tick)
    {
        DripIfDue(TrackerFor(sourceId, 0f), posWorld, surfaceY, tick);
    }

    private BleedTracker TrackerFor(int sourceId, float initialBlood)
    {
        if (_trackers.TryGetValue(sourceId, out var tracker))
        {
            return tracker;
        }

        tracker = new BleedTracker { PrevBlood = initialBlood };
        _trackers[sourceId] = tracker;
        return tracker;
    }

    private void DripIfDue(
        BleedTracker tracker, Vector3 posWorld, float surfaceY, int tick)
    {
        if (tick < tracker.NextDripTick)
        {
            return;
        }

        tracker.NextDripTick = tick + DripIntervalTicks;
        var jitter = Random.insideUnitCircle * DropJitter;
        Spawn(posWorld.x + jitter.x, posWorld.z + jitter.y, surfaceY, tick);
    }

    // Once per rendered sim tick, after the NPC loop: spread + dilute + expire.
    public void Advance(int tick)
    {
        for (var i = _stains.Count - 1; i >= 0; i--)
        {
            var stain = _stains[i];
            var age = tick - stain.BornTick;
            if (age >= LifetimeTicks || age < 0) // negative: save reloaded
            {
                Destroy(stain.Root.gameObject);
                _stains.RemoveAt(i);
                continue;
            }

            // One continuous billow over the whole lifetime: the radius grows
            // from 0 to its max while the alpha fades in lock-step — the wider
            // it spreads the more transparent it gets, hitting alpha 0 exactly
            // as it reaches full spread, then it's removed (view + record).
            var spread = Mathf.Clamp01(age / LifetimeTicks);
            var scale = Mathf.Lerp(DripScale, stain.FullScale, spread);
            stain.Root.localScale = new Vector3(scale, scale, scale);

            // Dilute: hue washes scarlet -> pale pink, alpha inversely to spread.
            var col = Color.Lerp(FreshBlood, DilutePink, spread);
            col.a = MaxAlpha * (1f - spread);
            stain.Mpb.SetColor(BaseColorId, col);
            stain.Renderer.SetPropertyBlock(stain.Mpb);
        }
    }

    // Per render frame: ride the live wave swell so the discs sit ON the moving
    // surface (matches how the swimmer's body bobs with WaterWave).
    private void LateUpdate()
    {
        for (var i = 0; i < _stains.Count; i++)
        {
            var stain = _stains[i];
            var y = stain.SurfaceY + WaterWave.HeightNow(stain.BaseX, stain.BaseZ) + SurfaceLift;
            stain.Root.position = new Vector3(stain.BaseX, y, stain.BaseZ);
        }
    }

    private void Spawn(float x, float z, float surfaceY, int tick)
    {
        EnsureAssets();

        if (_stains.Count >= MaxStains)
        {
            Destroy(_stains[0].Root.gameObject);
            _stains.RemoveAt(0);
        }

        var go = new GameObject("WaterBlood");
        go.transform.SetParent(transform, false);
        // Lie flat on the surface, facing up, with a random spin so overlapping
        // discs never tile visibly.
        go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        go.transform.localScale = new Vector3(DripScale, DripScale, DripScale);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _quad;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;

        var mpb = new MaterialPropertyBlock();
        mpb.SetColor(BaseColorId, FreshBlood);
        mr.SetPropertyBlock(mpb);

        var stain = new Stain
        {
            Root = go.transform,
            Renderer = mr,
            Mpb = mpb,
            BornTick = tick,
            FullScale = Random.Range(BillowScaleMin, BillowScaleMax),
            BaseX = x,
            BaseZ = z,
            SurfaceY = surfaceY,
        };
        go.transform.position = new Vector3(x, surfaceY + SurfaceLift, z);
        _stains.Add(stain);
    }

    private void EnsureAssets()
    {
        if (_material != null && _quad != null)
        {
            return;
        }

        _quad = BuildFlatQuad();
        _material = BuildBloodMaterial();
    }

    // A 1x1 quad in the XZ plane, normal +Y, UV 0..1 — scales by localScale to
    // a disc of the wanted diameter.
    private static Mesh BuildFlatQuad()
    {
        var mesh = new Mesh { name = "WaterBloodQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 0f, -0.5f),
            new Vector3(0.5f, 0f, 0.5f),
            new Vector3(-0.5f, 0f, 0.5f),
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f),
        };
        mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateBounds();
        return mesh;
    }

    // URP Unlit, transparent, double-sided; a soft radial red disc texture, the
    // per-stain scarlet/alpha driven by a MaterialPropertyBlock on _BaseColor.
    private static Material BuildBloodMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var m = new Material(shader) { name = "WaterBlood" };
        m.SetTexture("_BaseMap", BuildDiscTexture());
        m.SetColor("_BaseColor", Color.white);
        // Transparent (alpha) surface, no depth write, no back-face cull.
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_Cull", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
        return m;
    }

    // Soft-edged filled disc: opaque core, alpha feathering to nothing at the
    // rim — a spreading circle of blood, not a hard-edged coin.
    private static Texture2D BuildDiscTexture()
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "water_blood_disc",
        };
        var px = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size - 0.5f;
                var v = (y + 0.5f) / size - 0.5f;
                var r = Mathf.Sqrt(u * u + v * v) * 2f; // 0 center .. 1 at edge
                // Dense core out to ~0.5, then a soft feathered falloff. A gentle
                // extra hotspot in the very middle reads as the fresh drip point.
                var a = Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.45f, 1f, r));
                a *= 0.85f + 0.15f * Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0f, 0.2f, r));
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
        }

        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
        if (_quad != null) Destroy(_quad);
    }
}

}
