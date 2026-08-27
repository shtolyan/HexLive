using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using System.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Bridges the per-mob <see cref="MobConfig"/> assets and the engine-free
    /// <see cref="MobCatalog"/> the simulation reads — the mob mirror of
    /// <see cref="GarmentTuning"/>. Called once at startup
    /// (PrototypeRuntimeBootstrap) BEFORE the world is built.
    ///
    /// Simulation values come from config/simdata. Visual MobConfig assets live
    /// inside their owning mob bundles and are never scanned from Resources.
    /// </summary>
    public static class MobTuning
    {
        public static void LoadAndApply()
        {
            MobLibrary.Clear();
            MobConfig[] configs;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                configs = UnityEditor.AssetDatabase.FindAssets(
                        "t:MobConfig", new[] { "Assets/HexLiveContent/RuntimeSource/Mobs" })
                    .Select(guid => UnityEditor.AssetDatabase.LoadAssetAtPath<MobConfig>(
                        UnityEditor.AssetDatabase.GUIDToAssetPath(guid)))
                    .Where(value => value != null)
                    .ToArray();
            }
            else
#endif
            {
                configs = AtomicResources.LoadAll<MobConfig>(MobConfig.ResourceFolder);
            }

            foreach (var config in configs)
            {
                MobCatalog.Override(config.ToStats());
                MobLibrary.Register(config);
            }
        }
    }

    /// <summary>Presentation-side lookup for a mob's VISUALS (its prefab) —
    /// the mob mirror of <see cref="GearLibrary"/>. Views ask here and fall
    /// resolves every prefab through one atomic mob record.</summary>
    public static class MobLibrary
    {
        private static readonly System.Collections.Generic.Dictionary<string, MobConfig> Configs = new();

        /// <summary>Spec 40.8-G prewarm: every registered mob id, so the
        /// runner can pull all mob prefabs into memory behind the loading
        /// curtain (the first wolf spawn used to hitch on the disk read).</summary>
        public static System.Collections.Generic.IEnumerable<string> Ids => MobCatalog.Active.Keys;

        public static void Clear() => Configs.Clear();

        public static void Register(MobConfig config)
        {
            if (config != null)
            {
                Configs[config.MobId] = config;
            }
        }

        /// <summary>Mob ids retired from content that can still arrive over the
        /// wire: a server running a pre-retirement build, or one whose save was
        /// written before the retirement, keeps streaming them. They own no mob
        /// bundle any more, so requesting one would terminally log a false
        /// missing-record error; the honest render for retired content is
        /// nothing at all. Save loading purges them on the simulation side
        /// (WorldSaveSerializer.MigrateRetiredContent) — this guard covers the
        /// window until every server has restarted on that code.</summary>
        public static bool IsRetired(string mobId) => mobId == "shark";

        /// <summary>The mob's prefab: the asset's direct reference first, the
        /// legacy Resources path second, null = caller's hardcoded fallback.</summary>
        public static GameObject LoadPrefab(string mobId)
        {
            return string.IsNullOrEmpty(mobId) || IsRetired(mobId)
                ? null
                : ContentPrefabCache.GetOrRequest("mob", mobId);
        }

        /// <summary>The mob's whole config asset (view tuning), or null.</summary>
        public static MobConfig Get(string mobId)
        {
            return mobId != null && Configs.TryGetValue(mobId, out var config) ? config : null;
        }
    }
}
