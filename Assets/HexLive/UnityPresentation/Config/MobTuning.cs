using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Bridges the per-mob <see cref="MobConfig"/> assets and the engine-free
    /// <see cref="MobCatalog"/> the simulation reads — the mob mirror of
    /// <see cref="GarmentTuning"/>. Called once at startup
    /// (PrototypeRuntimeBootstrap) BEFORE the world is built.
    ///
    /// Loads EVERY MobConfig under <c>Resources/HexLive/Mobs/</c> and overrides
    /// its catalog entry. A missing folder leaves the built-in defaults intact,
    /// so the game can never boot with a blank mob table.
    /// </summary>
    public static class MobTuning
    {
        public static void LoadAndApply()
        {
            MobLibrary.Clear();
            var configs = Resources.LoadAll<MobConfig>(MobConfig.ResourceFolder);
            if (configs == null || configs.Length == 0)
            {
                // No assets yet — keep MobCatalog's built-in defaults.
                return;
            }

            foreach (var config in configs)
            {
                if (config == null)
                {
                    continue;
                }

                MobCatalog.Override(config.ToStats());
                MobLibrary.Register(config);
            }
        }
    }

    /// <summary>Presentation-side lookup for a mob's VISUALS (its prefab) —
    /// the mob mirror of <see cref="GearLibrary"/>. Views ask here and fall
    /// back to their old hardcoded Resources paths when the asset declares
    /// nothing, so the wolf keeps rendering even with an empty field.</summary>
    public static class MobLibrary
    {
        private static readonly System.Collections.Generic.Dictionary<string, MobConfig> Configs = new();

        /// <summary>Spec 40.8-G prewarm: every registered mob id, so the
        /// runner can pull all mob prefabs into memory behind the loading
        /// curtain (the first wolf spawn used to hitch on the disk read).</summary>
        public static System.Collections.Generic.IEnumerable<string> Ids => Configs.Keys;

        public static void Clear() => Configs.Clear();

        public static void Register(MobConfig config)
        {
            if (config != null)
            {
                Configs[config.MobId] = config;
            }
        }

        /// <summary>The mob's prefab: the asset's direct reference first, the
        /// legacy Resources path second, null = caller's hardcoded fallback.</summary>
        public static GameObject LoadPrefab(string mobId)
        {
            if (mobId == null || !Configs.TryGetValue(mobId, out var config))
            {
                return null;
            }

            if (config.prefab != null)
            {
                return config.prefab;
            }

            return string.IsNullOrEmpty(config.prefabResourcePath)
                ? null
                : Resources.Load<GameObject>(config.prefabResourcePath);
        }

        /// <summary>The mob's whole config asset (view tuning), or null.</summary>
        public static MobConfig Get(string mobId)
        {
            return mobId != null && Configs.TryGetValue(mobId, out var config) ? config : null;
        }
    }
}
