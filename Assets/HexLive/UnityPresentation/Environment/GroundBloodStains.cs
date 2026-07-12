using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{

// Spec 40.2-B: blood drips on the ground. While an NPC is actively losing
// blood (her sim Blood ticked DOWN — bandages/pills only raise it, so a
// drop is an unambiguous bleeding signal) she leaves droplets at her feet:
// each starts as a small drip, spreads out over ~half a sim minute, and
// then fades away over ONE game day (2400 ticks). Purely cosmetic and
// presentation-side: driven by snapshot ticks (respects pause/speed), not
// persisted in saves. Textures are AI-generated flat cartoon stains in
// Resources/HexLive/BloodStains (4 variants, picked at random).
public sealed class GroundBloodStains : MonoBehaviour
{
    private const int MaxStains = 160;          // oldest recycled beyond this
    private const float LifetimeTicks = 2400f;  // one game day to vanish
    private const float SpreadTicks = 120f;     // drip -> full puddle, ~30 sim-s
    private const float DripScale = 0.05f;      // fresh droplet, metres
    private const float PuddleScaleMin = 0.16f; // full spread, metres
    private const float PuddleScaleMax = 0.30f;
    private const float MaxAlpha = 0.85f;
    private const int DripIntervalTicks = 6;    // while bleeding, ~1.5 sim-s
    private const int BleedGraceTicks = 20;     // blood drops on SLOW ticks (16)
    private const float FootJitter = 0.12f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
    private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
    private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

    private sealed class Stain
    {
        public Transform Root;
        public MeshRenderer Renderer;
        public int BornTick;
        public float FullScale;
    }

    private sealed class BleedTracker
    {
        public float PrevBlood = 1f;
        public int BleedingUntilTick = -1;
        public int NextDripTick;
    }

    private readonly List<Stain> _stains = new();
    private readonly Dictionary<int, BleedTracker> _trackers = new();
    private Material[] _materials; // one per texture variant, shared by stains
    private Mesh _quad;
    private MaterialPropertyBlock _mpb;

    // Per-NPC, once per rendered sim tick: detect blood loss and drip.
    public void OnNpcTick(int npcId, float blood, Vector3 footWorld, int tick)
    {
        if (!_trackers.TryGetValue(npcId, out var tracker))
        {
            tracker = new BleedTracker { PrevBlood = blood };
            _trackers[npcId] = tracker;
        }

        if (blood < tracker.PrevBlood - 0.0001f)
        {
            tracker.BleedingUntilTick = tick + BleedGraceTicks;
        }

        tracker.PrevBlood = blood;

        if (tick <= tracker.BleedingUntilTick && tick >= tracker.NextDripTick)
        {
            tracker.NextDripTick = tick + DripIntervalTicks;
            var jitter = Random.insideUnitCircle * FootJitter;
            Spawn(footWorld + new Vector3(jitter.x, 0f, jitter.y), tick);
        }
    }

    // Once per rendered sim tick, after the NPC loop: spread + fade + expire.
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

            // Ease-out spread: a drip lands small and flows outward.
            var spread = Mathf.Clamp01(age / SpreadTicks);
            spread = 1f - (1f - spread) * (1f - spread);
            var scale = Mathf.Lerp(DripScale, stain.FullScale, spread);
            stain.Root.localScale = new Vector3(scale, scale, scale);

            // Dry out: linear fade across the game day.
            var alpha = MaxAlpha * (1f - age / LifetimeTicks);
            _mpb ??= new MaterialPropertyBlock();
            stain.Renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, new Color(1f, 1f, 1f, alpha));
            stain.Renderer.SetPropertyBlock(_mpb);
        }
    }

    private void Spawn(Vector3 at, int tick)
    {
        EnsureAssets();
        if (_materials.Length == 0)
        {
            return; // no textures shipped: silently no-op
        }

        if (_stains.Count >= MaxStains)
        {
            Destroy(_stains[0].Root.gameObject);
            _stains.RemoveAt(0);
        }

        var go = new GameObject("BloodStain");
        go.transform.SetParent(transform, false);
        // Tiny per-stain lift so overlapping puddles never z-fight.
        var lift = 0.012f + (_stains.Count % 16) * 0.0006f;
        go.transform.position = at + new Vector3(0f, lift, 0f);
        go.transform.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
        go.transform.localScale = Vector3.one * DripScale;

        go.AddComponent<MeshFilter>().sharedMesh = _quad;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _materials[Random.Range(0, _materials.Length)];
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        _stains.Add(new Stain
        {
            Root = go.transform,
            Renderer = renderer,
            BornTick = tick,
            FullScale = Random.Range(PuddleScaleMin, PuddleScaleMax),
        });
    }

    private void EnsureAssets()
    {
        if (_materials != null)
        {
            return;
        }

        var textures = Resources.LoadAll<Texture2D>("HexLive/BloodStains");
        _materials = new Material[textures.Length];
        for (var i = 0; i < textures.Length; i++)
        {
            _materials[i] = CreateStainMaterial(textures[i]);
        }

        // Unity's Quad primitive mesh without the collider dance.
        var probe = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad = probe.GetComponent<MeshFilter>().sharedMesh;
        Destroy(probe);
    }

    // URP Unlit, transparent: the stain is flat colour from the texture —
    // lighting variance on a decal-like overlay reads as z-fighting noise.
    private static Material CreateStainMaterial(Texture2D texture)
    {
        var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        material.SetTexture(BaseMapId, texture);
        material.SetFloat(SurfaceId, 1f);
        material.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetFloat(ZWriteId, 0f);
        material.SetOverrideTag("RenderType", "Transparent");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return material;
    }
}

}
