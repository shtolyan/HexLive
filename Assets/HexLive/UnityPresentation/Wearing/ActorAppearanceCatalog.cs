using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// §74: the hairstyle library, as a runtime-loadable asset.
    ///
    /// Hair prefabs live under <c>Assets/ImportedActors/Wear/&lt;Name&gt;/</c> and
    /// are deliberately NOT in a Resources folder — until §74 the only way a
    /// hairstyle reached the game was the <c>hair</c> field on an actor prefab,
    /// so exactly four of the sixteen shipped. That is fine for a fixed cast and
    /// fatal for a rolled one: <c>Resources.Load</c> cannot see them, and a
    /// build strips every prefab nothing references. This asset IS the
    /// reference — it pulls all sixteen into the build and gives the view one
    /// place to resolve an id from.
    ///
    /// Rebuilt by <b>HexLive ▸ Actors ▸ Rebuild Appearance Catalog</b>; the id
    /// of a hairstyle is simply its prefab name, matching
    /// <c>ColonistAppearance.Hairstyles</c> on the simulation side.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Actor Appearance Catalog", fileName = "ActorAppearanceCatalog")]
    public sealed class ActorAppearanceCatalog : ScriptableObject
    {
        public const string ResourcePath = "HexLive/ActorAppearanceCatalog";

        [Tooltip("Все причёски: обычные Wear-префабы без слотов. Заполняется меню HexLive ▸ Actors ▸ Rebuild Appearance Catalog.")]
        public List<Wear> hairstyles = new();

        private static ActorAppearanceCatalog _instance;
        private static bool _tried;
        private Dictionary<string, Wear> _byId;

        /// <summary>The shipped catalog, or null when the asset is missing.</summary>
        public static ActorAppearanceCatalog Instance
        {
            get
            {
                if (_tried)
                {
                    return _instance;
                }

                _tried = true;
                _instance = Resources.Load<ActorAppearanceCatalog>(ResourcePath);
                if (_instance == null)
                {
                    Debug.LogWarning(
                        $"[§74] ActorAppearanceCatalog not found at Resources/{ResourcePath} — " +
                        "run HexLive ▸ Actors ▸ Rebuild Appearance Catalog. " +
                        "Every girl keeps the hairstyle authored on her actor prefab.");
                }

                return _instance;
            }
        }

        /// <summary>
        /// Hairstyle by prefab name. Null when the id is unknown OR when it is
        /// the explicit "bald" id — the caller cannot tell the difference and
        /// should not: both mean "no hair prefab to spawn".
        /// </summary>
        public Wear Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            if (_byId == null)
            {
                _byId = new Dictionary<string, Wear>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var hair in hairstyles)
                {
                    if (hair != null)
                    {
                        _byId[hair.name] = hair;
                    }
                }
            }

            return _byId.TryGetValue(id, out var found) ? found : null;
        }
    }
}
