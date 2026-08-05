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

        /// <summary>
        /// Одна расцветка одной причёски: папка
        /// <c>ImportedActors/Hair/&lt;hair&gt;/Materials/&lt;colour&gt;/</c> целиком.
        ///
        /// Материалы здесь ССЫЛАЮТСЯ, а не копируются — по той же причине, по
        /// которой в каталоге лежат сами причёски: они не в Resources, и без
        /// ссылки сборка выкинула бы их, оставив всех девушек одного цвета.
        /// </summary>
        [System.Serializable]
        public sealed class HairColour
        {
            public string hair = string.Empty;
            public string colour = string.Empty;
            public List<Material> materials = new();
        }

        [Tooltip("Расцветки причёсок. Заполняется тем же меню; прототипный (не перекрашенный) вариант в список НЕ входит.")]
        public List<HairColour> hairColours = new();

        private static ActorAppearanceCatalog _instance;
        private static bool _tried;
        private Dictionary<string, Wear> _byId;
        private Dictionary<string, List<HairColour>> _coloursByHair;

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

        /// <summary>
        /// Расцветки одной причёски, в устойчивом порядке. Пусто — законно:
        /// у половины причёсок расцветка одна, «как из коробки».
        ///
        /// Порядок важен: цвет выбирается индексом от хеша колониста, и
        /// перетасовка списка перекрасила бы всех уже живущих.
        /// </summary>
        public IReadOnlyList<HairColour> ColoursFor(string hairId)
        {
            if (string.IsNullOrEmpty(hairId))
            {
                return System.Array.Empty<HairColour>();
            }

            if (_coloursByHair == null)
            {
                _coloursByHair = new Dictionary<string, List<HairColour>>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var entry in hairColours)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.hair) || entry.materials.Count == 0)
                    {
                        continue;
                    }

                    if (!_coloursByHair.TryGetValue(entry.hair, out var list))
                    {
                        list = new List<HairColour>();
                        _coloursByHair[entry.hair] = list;
                    }

                    list.Add(entry);
                }

                foreach (var list in _coloursByHair.Values)
                {
                    list.Sort((a, b) => string.CompareOrdinal(a.colour, b.colour));
                }
            }

            return _coloursByHair.TryGetValue(hairId, out var found)
                ? found
                : (IReadOnlyList<HairColour>)System.Array.Empty<HairColour>();
        }
    }
}
