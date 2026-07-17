using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

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
            var configs = Resources.LoadAll<GearConfig>(GearConfig.ResourceFolder);
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

        public static GearConfig ConfigFor(string gearId) =>
            gearId != null && Configs.TryGetValue(gearId, out var c) ? c : null;

        /// <summary>The item's model: the config's DIRECT prefab reference
        /// first, its legacy Resources path second, then the
        /// Resources/HexLive/Objects/&lt;id&gt; convention.</summary>
        public static GameObject LoadPrefab(string gearId)
        {
            var config = ConfigFor(gearId);
            if (config != null)
            {
                if (config.prefab != null)
                {
                    return config.prefab;
                }

                if (!string.IsNullOrEmpty(config.prefabResourcePath))
                {
                    var fromPath = Resources.Load<GameObject>(config.prefabResourcePath);
                    if (fromPath != null)
                    {
                        return fromPath;
                    }
                }
            }

            return Resources.Load<GameObject>($"HexLive/Objects/{gearId}");
        }

        public static AnimationClip[] AttackClipsFor(string gearId)
        {
            var config = ConfigFor(gearId);
            return config != null && config.attackClips != null && config.attackClips.Length > 0
                ? config.attackClips
                : null;
        }

        public static AnimationClip ArmedIdleFor(string gearId) => ConfigFor(gearId)?.armedIdle;

        public static AnimationClip ArmedWalkFor(string gearId) => ConfigFor(gearId)?.armedWalk;

        public static AnimationClip WorkClipFor(string gearId) => ConfigFor(gearId)?.workClip;

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
