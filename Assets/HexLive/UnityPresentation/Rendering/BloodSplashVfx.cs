using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

// The ONE hit-blood splash for everybody: humans (NpcActorView) and mobs
// (MobView) spawn the same Epic Toon FX burst through SpawnHitSplash so the
// look — prefab pool, up-tilt, lifetime, size — can never drift apart.
public static class BloodSplashVfx
{
    // The pack's authored splash reads too big in-game — a global shrink
    // applied to every hit splash. Hierarchy scaling means sizes AND
    // velocities shrink together, so the burst stays compact.
    public const float SizeScale = 0.5f;

    // Epic Toon FX blood (from w-empire): 1-2 particle systems, 1 s life —
    // replaces the heavy 8 s RVFX sprays and fits the flat/cartoon style.
    private static GameObject[] _prefabs;

    // No-domain-reload runs keep statics between plays — a pre-import null
    // load must not stick forever.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticPrefabCache()
    {
        _prefabs = null;
    }

    public static GameObject[] Prefabs
    {
        get
        {
            if (_prefabs == null || System.Array.TrueForAll(_prefabs, value => value == null))
            {
                _prefabs = new[]
                {
                    Resources.Load<GameObject>("HexLive/VFX/ToonBlood/BloodSplatDirectional"),
                    Resources.Load<GameObject>("HexLive/VFX/ToonBlood/BloodSplatDirectional2"),
                    Resources.Load<GameObject>("HexLive/VFX/ToonBlood/BloodSplatWide")
                };
            }
            return _prefabs;
        }
    }

    /// <summary>Deterministic prefab pick from any seed (zone hash, tick…).</summary>
    public static GameObject Pick(int seed)
    {
        var prefabs = Prefabs;
        return prefabs[(seed & int.MaxValue) % prefabs.Length] ?? prefabs[0];
    }

    /// <summary>
    /// Spawn the shared hit splash. <paramref name="outward"/> is the
    /// horizontal fly-off direction (a small upward tilt is added here);
    /// <paramref name="actorScale"/> is the body size the pack's
    /// full-size-human authoring is scaled down to (root lossyScale /
    /// mob body length).
    /// </summary>
    public static void SpawnHitSplash(Vector3 origin, Vector3 outward, float actorScale, int seed)
    {
        var prefab = Pick(seed);
        if (prefab == null)
        {
            return;
        }

        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f)
        {
            outward = Vector3.forward;
        }

        var vfx = Object.Instantiate(prefab, origin,
            Quaternion.LookRotation(outward.normalized + Vector3.up * 0.35f));
        vfx.transform.localScale = Vector3.one * (actorScale * SizeScale);
        foreach (var ps in vfx.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        }

        Object.Destroy(vfx, 2f); // toon splat finishes in ~1 s
    }
}

}
