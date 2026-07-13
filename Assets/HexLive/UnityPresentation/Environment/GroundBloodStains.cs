using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HexLive.UnityPresentation.Environment
{

// Spec 40.2-B: blood drips on the ground. While an NPC is actively losing
// blood (her sim Blood ticked DOWN — bandages/pills only raise it, so a
// drop is an unambiguous bleeding signal) she leaves droplets at her feet:
// each starts as a small drip, spreads out over ~half a sim minute, and
// then fades away over ONE game day (2400 ticks). Purely cosmetic and
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
    private const int MaxStains = 110;          // oldest recycled beyond this
    private const float LifetimeTicks = 2400f;  // one game day to vanish
    private const float SpreadTicks = 120f;     // drip -> full puddle, ~30 sim-s
    private const float DripScale = 0.09f;      // fresh droplet, metres
    // Bigger + more opaque than the first pass: on bright sand a 0.2 m,
    // 0.85-alpha brown smear read as a faint dirt smudge. Fresh-red material
    // + these makes it a clear wet pool.
    private const float PuddleScaleMin = 0.26f; // full spread, metres
    private const float PuddleScaleMax = 0.46f;
    private const float MaxAlpha = 1f;
    private const int DripIntervalTicks = 6;    // while bleeding, ~1.5 sim-s
    private const int BleedGraceTicks = 20;     // blood drops on SLOW ticks (16)
    private const float FootJitter = 0.12f;
    // The projector hovers above the foot point and projects down through
    // the ground: enough depth to catch a slope, not enough to bleed into
    // caves under overhangs.
    private const float ProjectorHover = 0.35f;
    private const float ProjectorDepth = 0.9f;

    private sealed class Stain
    {
        public Transform Root;
        public DecalProjector Projector;
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

            // Ease-out spread: a drip lands small and flows outward. Once
            // fully spread (age >= SpreadTicks) the size is fixed, so stop
            // rewriting it — only the fade keeps changing across the day.
            if (age < SpreadTicks)
            {
                var spread = age / SpreadTicks;
                spread = 1f - (1f - spread) * (1f - spread);
                var scale = Mathf.Lerp(DripScale, stain.FullScale, spread);
                stain.Projector.size = new Vector3(scale, scale, ProjectorDepth);
            }

            // Dry out: linear fade across the game day.
            stain.Projector.fadeFactor = MaxAlpha * (1f - age / LifetimeTicks);
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
        go.transform.position = at + new Vector3(0f, ProjectorHover, 0f);
        // Look straight down with a random spin so variants never repeat.
        go.transform.rotation = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);

        var projector = go.AddComponent<DecalProjector>();
        projector.material = _materials[Random.Range(0, _materials.Length)];
        projector.size = new Vector3(DripScale, DripScale, ProjectorDepth);
        projector.pivot = Vector3.zero;
        projector.fadeFactor = MaxAlpha;
        // Everything the pool touches catches it — terrain, grass, a foot
        // standing in it (that wrap is exactly why the pack projects decals).
        projector.renderingLayerMask = uint.MaxValue;

        _stains.Add(new Stain
        {
            Root = go.transform,
            Projector = projector,
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

        // Use the RVFX pack's OWN projector decal materials (the pretty
        // BloodFX_PBR_Projector_URP shadergraph — tuned albedo power, ambient
        // intensity, wet smoothness/specularity + normal), copied into
        // Resources as BloodStain_01..04. They render exactly like the pack's
        // demo `..._Static_Projected` prefabs; we only drive size + fadeFactor
        // per stain on our tick-driven lifecycle. Shared instances: fadeFactor
        // and size are DecalProjector fields, not material state, so one
        // material serves every stain of that variant.
        _materials = Resources.LoadAll<Material>("HexLive/BloodStainMats");
        System.Array.Sort(_materials, (a, b) => string.CompareOrdinal(a.name, b.name));
    }
}

}
