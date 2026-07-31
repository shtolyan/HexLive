using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HexLive.UnityPresentation.Environment
{

// Spec 40.2-B: blood drips on the ground. While an NPC is actively losing
// blood (her sim Blood ticked DOWN — bandages/pills only raise it, so a
// drop is an unambiguous bleeding signal) she leaves droplets at her feet:
// each starts as a small drip, spreads out over ~half a sim minute, and
// then fades away over 7200 ticks (30 real minutes). Purely cosmetic and
// presentation-side: driven by snapshot ticks (respects pause/speed), not
// persisted in saves.
// Visuals are the RVFX Blood Effects Pack splatters rendered the way the
// pack's own demo does it — as URP **DecalProjectors** aimed down, so the
// pool hugs sloped hex prisms, pebbles and feet instead of floating as a
// flat quad. We use the pack's OWN projector decal materials
// (BloodFX_PBR_Projector_URP shadergraph, copied to
// Resources/HexLive/BloodStainMats as BloodStain_01..04) — the same rich
// wet look as the pack's `..._Static_Projected` demo prefabs — rather than
// a stock Shader-Graphs/Decal built from loose textures. The pack's
// realtime spawner scripts aren't used: they run on Time.deltaTime and
// would ignore sim pause/speed, so the tick-driven lifecycle here stays
// ours (we only drive DecalProjector size + fadeFactor).
public sealed class GroundBloodStains : MonoBehaviour
{
    // Each stain is a DecalProjector rendered into the DBuffer EVERY frame.
    // With a dog swarm many girls bleed at once and the cap is hit fast —
    // 160 big projectors was real overdraw. 110 bounds it (oldest recycled)
    // while the drip trail still reads.
    private const int MaxStains = 130;          // oldest recycled beyond this
    // Blood lingers a LONG time: a spilled pool you walk past should still be
    // there much later, slowly drying. Plain ticks — it does not follow the
    // visual clock, so the real-time lifetime is fixed.
    private const float LifetimeTicks = 7200f;  // 7200 ticks = 30 real minutes
    // As a stain dries its alpha fades — but a semi-transparent RED film over
    // yellow sand reads as bright "ketchup". So we also darken _BaseColor with
    // age toward deep dried bordo: old + faint = a dark stain, not orange.
    // Discrete buckets of shared materials (no per-stain material, no MPB —
    // DecalProjector doesn't take one): 4 variants x AgeBuckets instances.
    private const int AgeBuckets = 6;
    private static readonly Color DriedBlood = new(0.15f, 0.02f, 0.02f, 1f);
    private const float SpreadTicks = 120f;     // drip -> full puddle, ~30 sim-s
    private const float DripScale = 0.09f;      // fresh droplet, metres
    // Bigger + more opaque than the first pass: on bright sand a 0.2 m,
    // 0.85-alpha brown smear read as a faint dirt smudge. Fresh-red material
    // + these makes it a clear wet pool.
    private const float PuddleScaleMin = 0.26f; // full spread, metres
    private const float PuddleScaleMax = 0.46f;
    // A drip that lands inside an existing pool does NOT spawn a second
    // projector on top of it — it merges: the pool re-wets (fresh red, full
    // alpha) and its target size grows. A girl asleep with a bleed leaves ONE
    // wide spreading pool under her, not a stack of identical droplets.
    private const float PoolGrowth = 0.07f;     // metres added per merged drip
    private const float MaxPoolScale = 1.0f;    // widest a single pool gets
    private const float MergeRadiusMin = 0.16f; // even a fresh drip catches close hits
    private const float MaxAlpha = 1f;
    private const int DripIntervalTicks = 6;    // while bleeding, ~1.5 sim-s
    private const int BleedGraceTicks = 20;     // blood drops on SLOW ticks (16)
    private const float FootJitter = 0.12f;
    // The projector box must project DOWN INTO THE GROUND only, never up the
    // girl's legs/body/clothes (a +0.35 hover with a centered box reached
    // ~0.8 m up her shins → blood sprayed on her). Now it sits just above the
    // foot contact (small hover to still catch the ground when the foot floats
    // over a slope) and the box is pushed fully DOWNWARD via the pivot, so its
    // top face is at the foot and everything above — her — is outside it.
    private const float ProjectorHover = 0.06f;
    private const float ProjectorDepth = 0.9f;

    private sealed class Stain
    {
        public Transform Root;
        public DecalProjector Projector;
        public int BornTick;        // last time fresh blood landed (drives fade/darken)
        public float FullScale;     // target size the pool spreads toward
        public int Variant;   // which decal texture/material family
        public int Bucket;    // current darkening step (0 = fresh red)
        // Spread runs from (SpreadFromScale @ SpreadStartTick) toward FullScale
        // so a merge can restart the flow from the CURRENT size, not from a drip.
        public int SpreadStartTick;
        public float SpreadFromScale;
        public float CurrentScale;
    }

    private sealed class BleedTracker
    {
        public float PrevBlood = 1f;
        public int BleedingUntilTick = -1;
        public int NextDripTick;
    }

    private readonly List<Stain> _stains = new();
    private readonly Dictionary<int, BleedTracker> _trackers = new();
    // [variant, age bucket] — bucket 0 is the fresh red Resources asset, later
    // buckets are runtime copies darkened toward DriedBlood. Shared by stains.
    private Material[,] _stainMats;
    private int _variantCount;

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
            var at = footWorld + new Vector3(jitter.x, 0f, jitter.y);
            if (!TryMergeIntoPool(at, tick))
            {
                Spawn(at, tick);
            }
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

            // Ease-out spread: blood flows outward from wherever the pool was
            // when the last drip landed. Once fully spread the size is fixed,
            // so stop rewriting it — only the fade keeps changing.
            var spreadAge = tick - stain.SpreadStartTick;
            if (spreadAge < SpreadTicks)
            {
                var spread = spreadAge / SpreadTicks;
                spread = 1f - (1f - spread) * (1f - spread);
                var scale = Mathf.Lerp(stain.SpreadFromScale, stain.FullScale, spread);
                stain.CurrentScale = scale;
                stain.Projector.size = new Vector3(scale, scale, ProjectorDepth);
            }

            // Dry out: linear alpha fade across the whole lifetime.
            var t01 = age / LifetimeTicks;
            stain.Projector.fadeFactor = MaxAlpha * (1f - t01);

            // ...and darken with age so the fading film never goes ketchup.
            // Bucketed: swap to a darker shared material only when the step
            // changes (not every tick).
            var bucket = Mathf.Clamp((int)(t01 * AgeBuckets), 0, AgeBuckets - 1);
            if (bucket != stain.Bucket)
            {
                stain.Bucket = bucket;
                stain.Projector.material = _stainMats[stain.Variant, bucket];
            }
        }
    }

    // A drip landing inside (or right next to) an existing pool grows that
    // pool instead of stacking a new projector on it: the target size steps
    // up (capped), the spread restarts from the current size so the growth
    // flows smoothly, and BornTick resets — fresh blood re-wets the pool back
    // to full-alpha red. Returns false when no pool is close enough.
    private bool TryMergeIntoPool(Vector3 at, int tick)
    {
        Stain best = null;
        var bestDist = float.MaxValue;
        for (var i = 0; i < _stains.Count; i++)
        {
            var stain = _stains[i];
            var p = stain.Root.position;
            var dx = p.x - at.x;
            var dz = p.z - at.z;
            var dist = Mathf.Sqrt(dx * dx + dz * dz);
            // Inside the pool's footprint (half its target width), with a
            // floor so drips clustered around a fresh droplet still merge.
            var radius = Mathf.Max(stain.FullScale * 0.5f, MergeRadiusMin);
            if (dist <= radius && dist < bestDist)
            {
                best = stain;
                bestDist = dist;
            }
        }

        if (best == null)
        {
            return false;
        }

        best.FullScale = Mathf.Min(best.FullScale + PoolGrowth, MaxPoolScale);
        best.SpreadFromScale = best.CurrentScale;
        best.SpreadStartTick = tick;
        best.BornTick = tick; // re-wet: Advance resets fade + fresh-red bucket
        return true;
    }

    private void Spawn(Vector3 at, int tick)
    {
        EnsureAssets();
        if (_variantCount == 0)
        {
            return; // no textures shipped: silently no-op
        }

        if (_stains.Count >= MaxStains)
        {
            // Recycle the stalest stain — merges refresh BornTick, so list
            // order no longer implies age and index 0 may be an active pool.
            var oldest = 0;
            for (var i = 1; i < _stains.Count; i++)
            {
                if (_stains[i].BornTick < _stains[oldest].BornTick)
                {
                    oldest = i;
                }
            }

            Destroy(_stains[oldest].Root.gameObject);
            _stains.RemoveAt(oldest);
        }

        var go = new GameObject("BloodStain");
        go.transform.SetParent(transform, false);
        go.transform.position = at + new Vector3(0f, ProjectorHover, 0f);
        // Look straight down with a random spin so variants never repeat.
        go.transform.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);

        var variant = Random.Range(0, _variantCount);
        var projector = go.AddComponent<DecalProjector>();
        projector.material = _stainMats[variant, 0]; // fresh red
        projector.size = new Vector3(DripScale, DripScale, ProjectorDepth);
        // Push the projection box fully DOWNWARD (local +Z points down after
        // the 90° tilt): its top face lands at the foot and the whole volume
        // sits below, so the ground gets painted but the girl above never
        // does. pivot is local-space, so the depth axis is Z.
        projector.pivot = new Vector3(0f, 0f, ProjectorDepth * 0.5f);
        projector.fadeFactor = MaxAlpha;
        // Terrain, grass and a foot standing IN the pool still catch it (that
        // downward wrap is why the pack projects decals) — but only surfaces
        // at/below the foot, never the legs/body/clothes rising above it.
        projector.renderingLayerMask = uint.MaxValue;

        _stains.Add(new Stain
        {
            Root = go.transform,
            Projector = projector,
            BornTick = tick,
            FullScale = Random.Range(PuddleScaleMin, PuddleScaleMax),
            Variant = variant,
            Bucket = 0,
            SpreadStartTick = tick,
            SpreadFromScale = DripScale,
            CurrentScale = DripScale,
        });
    }

    private void EnsureAssets()
    {
        if (_stainMats != null)
        {
            return;
        }

        // Use the RVFX pack's OWN projector decal materials (the pretty
        // BloodFX_PBR_Projector_URP shadergraph — tuned albedo power, ambient
        // intensity, wet smoothness/specularity + normal), copied into
        // Resources as BloodStain_01..04. They render exactly like the pack's
        // demo `..._Static_Projected` prefabs; we only drive size + fadeFactor
        // per stain on our tick-driven lifecycle. Shared instances: fadeFactor
        // and size are DecalProjector fields, not material state.
        var bases = Resources.LoadAll<Material>("HexLive/BloodStainMats");
        System.Array.Sort(bases, (a, b) => string.CompareOrdinal(a.name, b.name));
        _variantCount = bases.Length;
        _stainMats = new Material[_variantCount, AgeBuckets];
        for (var v = 0; v < _variantCount; v++)
        {
            // Bucket 0 is the shared asset itself — never mutated. Later
            // buckets are copies with _BaseColor lerped toward dried bordo.
            _stainMats[v, 0] = bases[v];
            var fresh = bases[v].HasProperty("_BaseColor")
                ? bases[v].GetColor("_BaseColor")
                : Color.red;
            var dried = new Color(DriedBlood.r, DriedBlood.g, DriedBlood.b, fresh.a);
            for (var b = 1; b < AgeBuckets; b++)
            {
                var m = new Material(bases[v]);
                var c = Color.Lerp(fresh, dried, b / (float)(AgeBuckets - 1));
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                _stainMats[v, b] = m;
            }
        }
    }

    private void OnDestroy()
    {
        // Free the darkened runtime copies (bucket 0 is a shared asset).
        if (_stainMats == null)
        {
            return;
        }

        for (var v = 0; v < _variantCount; v++)
        {
            for (var b = 1; b < AgeBuckets; b++)
            {
                if (_stainMats[v, b] != null)
                {
                    Destroy(_stainMats[v, b]);
                }
            }
        }
    }
}

}
