using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Content;
using UnityEngine;
using HexLive.UnityPresentation.Environment;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Bridges the per-item <see cref="GearConfig"/> assets and (a) the
    /// engine-free <see cref="GearCatalog"/> the simulation reads, (b) the
    /// presentation-side <see cref="GearLibrary"/> (prefabs + animation clips).
    /// Called once at startup (PrototypeRuntimeBootstrap) BEFORE the world is
    /// built. A missing folder leaves the built-in defaults intact.
    /// </summary>
    public static class GearTuning
    {
        public static void LoadAndApply()
        {
            GearLibrary.Clear();
            GearConfig[] configs;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                configs = UnityEditor.AssetDatabase.FindAssets(
                        "t:GearConfig", new[] { "Assets/HexLiveContent/RuntimeSource/Gear" })
                    .Select(guid => UnityEditor.AssetDatabase.LoadAssetAtPath<GearConfig>(
                        UnityEditor.AssetDatabase.GUIDToAssetPath(guid)))
                    .Where(value => value != null)
                    .ToArray();
            }
            else
#endif
            {
                configs = HexLive.UnityPresentation.Content.AtomicResources.LoadAll<GearConfig>(
                    GearConfig.ResourceFolder);
            }
            if (configs == null || configs.Length == 0)
            {
                return;
            }

            foreach (var config in configs)
            {
                if (config == null)
                {
                    continue;
                }

                GearCatalog.Override(config.ToStats());
                GearLibrary.Register(config);
                config.ApplyRecipe();
            }
        }

        /// <summary>
        /// Player bootstrap may warm presentation assets, but must never apply
        /// their simulation values over server-authoritative simdata. The
        /// first miss is retained by AtomicResources across the async registry
        /// refresh; ConfigFor polls the same handle on combat frames.
        /// </summary>
        public static void PrewarmPresentation()
        {
            var fist = GearLibrary.LoadPublishedFist();
            if (fist != null)
            {
                GearLibrary.Register(fist);
            }
        }
    }

    /// <summary>
    /// Presentation-side lookup for a gear item's VISUALS — the prefab and the
    /// animation clips its GearConfig declares. Views ask here first and fall
    /// back to the old conventions (Resources/HexLive/Objects/&lt;id&gt;,
    /// NpcAnimSet rows, the procedural swing) when a field is empty — so
    /// hand-made assets with no clips assigned keep today's look.
    /// </summary>
    public static class GearLibrary
    {
        private static readonly Dictionary<string, GearConfig> Configs = new();

        public static void Clear() => Configs.Clear();

        public static void Register(GearConfig config)
        {
            if (config != null && config.gearId != null)
            {
                Configs[config.gearId] = config;
            }
        }

        public static GearConfig ConfigFor(string gearId)
        {
            // Null is still "no item supplied". Only the explicit empty
            // string is GearCatalog.Fist; collapsing both values made optional
            // presentation lookups unexpectedly start loading fists.
            if (gearId == null)
            {
                return null;
            }
            if (Configs.TryGetValue(gearId, out var cached))
            {
                return cached;
            }

            // Atomic content owns GearConfig as the `gear-config` entry of the
            // SAME object record as the model. The retired directory scan
            // cannot synchronously enumerate a live registry, so request the
            // exact owner lazily and let the view retry while it is pending.
            var loaded = gearId.Length == 0
                ? LoadPublishedFist()
                : HexLive.UnityPresentation.Content.AtomicResources.Load<GearConfig>(
                    "HexLive/Objects/" + gearId);
            if (loaded != null)
            {
                Register(loaded);
            }
            return loaded;
        }

        internal static GearConfig LoadPublishedFist() =>
            HexLive.UnityPresentation.Content.AtomicResources.Load<UnityEngine.Object>(
                GearConfig.FistContentPath) as GearConfig;

        /// <summary>True for the finite authored gear table. These items must
        /// wait for their atomic GearConfig instead of freezing a generic grip
        /// while the companion entry is still downloading.</summary>
        public static bool RequiresAuthoredConfig(string gearId) =>
            !string.IsNullOrEmpty(gearId) && GearCatalog.Defaults.ContainsKey(gearId);

        /// <summary>The item's model: the config's DIRECT prefab reference
        /// first, its legacy Resources path second, then the
        /// Resources/HexLive/Objects/&lt;id&gt; convention.</summary>
        public static GameObject LoadPrefab(string gearId)
        {
            // Runtime geometry has exactly one owner: object/<gearId>. The old
            // NativeName inequality accidentally discarded perfectly valid
            // bundles such as tool.spear (its native name equals its id), then
            // permanently installed a procedural prop on the first async miss.
            return WorldPropResources.Load(gearId);
        }

        public static AnimationClip[] AttackClipsFor(string gearId)
        {
            var config = ConfigFor(gearId);
            if (config == null)
            {
                return null;
            }

            // Strike rows first (fists: punches/kicks) — their order matches
            // the sim's StrikeVariants, so the view can index by StrikeIndex.
            if (config.strikes != null && config.strikes.Length > 0)
            {
                var clips = new AnimationClip[config.strikes.Length];
                for (var i = 0; i < clips.Length; i++)
                {
                    clips[i] = config.strikes[i]?.clip;
                }

                return clips;
            }

            return config.attackClips != null && config.attackClips.Length > 0
                ? config.attackClips
                : null;
        }

        public static AnimationClip ArmedIdleFor(string gearId) => ConfigFor(gearId)?.armedIdle;

        public static AnimationClip ArmedWalkFor(string gearId) => ConfigFor(gearId)?.armedWalk;

        /// <summary>§142: the item's own jog take (gait slot 0.5), or null.</summary>
        public static AnimationClip ArmedSlowRunFor(string gearId) => ConfigFor(gearId)?.armedSlowRun;

        /// <summary>§142: the item's own run take (gait slot 1), or null.</summary>
        public static AnimationClip ArmedRunFor(string gearId) => ConfigFor(gearId)?.armedRun;

        public static AnimationClip WorkClipFor(string gearId) => ConfigFor(gearId)?.workClip;

        /// <summary>§142: does this item ride in BOTH hands? Drives the upper-body
        /// carry layer and the off-hand IK; everything else stays one-handed.</summary>
        public static bool TwoHandedCarry(string gearId) => ConfigFor(gearId)?.twoHandedCarry == true;

        /// <summary>§142: the frozen pose the carry layer holds while the item is
        /// carried, plus WHERE in that clip to freeze. The asset's own
        /// <c>carryPose</c> first; failing that the item's first ATTACK clip, whose
        /// opening frame is the artist's own "weapon ready" stance (the spear's
        /// Bayonet Stab starts with both hands on the shaft). Null = no layer.</summary>
        public static AnimationClip CarryPoseFor(string gearId, out float normalizedTime)
        {
            normalizedTime = 0f;
            var config = ConfigFor(gearId);
            if (config == null || !config.twoHandedCarry)
            {
                return null;
            }

            normalizedTime = config.carryPoseTime;
            if (config.carryPose != null)
            {
                return config.carryPose;
            }

            var attacks = AttackClipsFor(gearId);
            return attacks != null && attacks.Length > 0 ? attacks[0] : null;
        }

        /// <summary>§142: off-hand IK settings — whether the free hand is pulled
        /// onto the shaft at all, and how far along the item it grips.</summary>
        public static bool TryGetOffHandGrip(string gearId, out float fraction)
        {
            fraction = 0.3f;
            var config = ConfigFor(gearId);
            if (config == null || !config.twoHandedCarry || !config.offHandGripIk)
            {
                return false;
            }

            fraction = config.offHandGripFraction;
            return true;
        }

        /// <summary>Tuned in-hand pose for a gear item, in the acting hand's
        /// local space (merged from the retired ItemAttachConfig). Left hand
        /// mirrors the right across the sagittal plane unless the asset
        /// authors an explicit left grip. False = caller falls back
        /// (AttachPoint → default table → palm-fit).</summary>
        public static bool TryGetHandPose(string gearId, bool leftHanded,
            out Vector3 localPosition, out Quaternion localRotation, out Vector3 localScale)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            localScale = Vector3.one;

            var config = ConfigFor(gearId);
            if (config == null || !config.handPoseAuthored)
            {
                return false;
            }

            localScale = config.handLocalScale == Vector3.zero ? Vector3.one : config.handLocalScale;

            if (leftHanded && config.handHasLeftOverride)
            {
                localPosition = config.leftHandLocalPosition;
                localRotation = Quaternion.Euler(config.leftHandLocalEuler);
            }
            else if (leftHanded)
            {
                var p = config.handLocalPosition;
                var e = config.handLocalEuler;
                localPosition = new Vector3(-p.x, p.y, p.z);
                localRotation = Quaternion.Euler(e.x, -e.y, -e.z);
            }
            else
            {
                localPosition = config.handLocalPosition;
                localRotation = Quaternion.Euler(config.handLocalEuler);
            }

            return true;
        }
    }
}
