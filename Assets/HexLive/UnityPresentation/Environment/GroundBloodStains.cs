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
// flat quad — WITH the pack's normal maps for a wet-relief glint. The
// pack's realtime spawner scripts themselves aren't used: they run on
// Time.deltaTime and would ignore sim pause/speed, so the tick-driven
// lifecycle here stays ours.
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

            // Ease-out spread: a drip lands small and flows outward.
            var spread = Mathf.Clamp01(age / SpreadTicks);
            spread = 1f - (1f - spread) * (1f - spread);
            var scale = Mathf.Lerp(DripScale, stain.FullScale, spread);
            stain.Projector.size = new Vector3(scale, scale, ProjectorDepth);

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

        // Albedo + matching pack normal maps live in sibling folders; pair
        // them by sorted name (LoadAll gives no order guarantee).
        var textures = Resources.LoadAll<Texture2D>("HexLive/BloodStains");
        var normals = Resources.LoadAll<Texture2D>("HexLive/BloodStainNormals");
        System.Array.Sort(textures, (a, b) => string.CompareOrdinal(a.name, b.name));
        System.Array.Sort(normals, (a, b) => string.CompareOrdinal(a.name, b.name));

        _materials = new Material[textures.Length];
        for (var i = 0; i < textures.Length; i++)
        {
            _materials[i] = CreateStainMaterial(textures[i],
                i < normals.Length ? normals[i] : null);
        }
    }

    // URP Decal shadergraph material (same "Base_Map" reference quirk as the
    // skin decals). The pack normal map gives the pool its wet relief; the
    // DBuffer runs Albedo+Normal, smoothness stays untouched.
    private static Material CreateStainMaterial(Texture2D texture, Texture2D normal)
    {
        var shader = Shader.Find("Shader Graphs/Decal");
        var material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"));
        material.SetTexture("Base_Map", texture);
        material.SetTexture("_BaseMap", texture);
        var blend = normal != null ? 0.6f : 0f;
        if (normal != null)
        {
            material.SetTexture("Normal_Map", normal);
            material.SetTexture("_NormalMap", normal);
        }

        material.SetFloat("Normal_Blend", blend);
        material.SetFloat("_NormalBlend", blend);
        material.SetFloat("_DecalNormalBlendFactor", blend);
        return material;
    }
}

}
