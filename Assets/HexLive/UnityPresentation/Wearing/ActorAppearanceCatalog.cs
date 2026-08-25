using System.Collections.Generic;
using System.Linq;
using HexLive.UnityPresentation.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// §74: указатель причёсок и их расцветок — ТОЛЬКО ИМЕНА, ни одной ссылки
    /// на ассет.
    ///
    /// Раньше здесь лежали прямые ссылки, и это было осознанно: причёски не в
    /// Resources, а без ссылки сборка выкинула бы их — в редакторе всё
    /// работает, в билде все девушки лысые. Ссылка втягивала их в билд, и это
    /// было ровно то, что требовалось.
    ///
    /// С переходом на внешний атомарный контент то же свойство стало проблемой: втягивала
    /// она их ВСЕГДА и ЦЕЛИКОМ — 16 причёсок, 254 расцветки, 1754 материала и
    /// все их текстуры, независимо от того, наденет ли кто-то хоть одну. Теперь
    /// содержимое доезжает по адресу и только когда понадобилось, а этот ассет
    /// отвечает на единственный вопрос: ЧТО вообще бывает.
    ///
    /// Объект имеет id причёски, а материалы — entries
    /// <c>colour/&lt;Цвет&gt;/&lt;Поверхность&gt;</c> внутри того же bundle.
    /// Грузит их <see cref="HairContent"/>.
    ///
    /// Перестраивается меню <b>HexLive ▸ Actors ▸ Rebuild Appearance Catalog</b>.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Actor Appearance Catalog", fileName = "ActorAppearanceCatalog")]
    public sealed class ActorAppearanceCatalog : ScriptableObject
    {
        [Tooltip("Имена причёсок (они же имена префабов и часть адреса). Заполняется меню HexLive ▸ Actors ▸ Rebuild Appearance Catalog.")]
        public List<string> hairstyles = new();

        /// <summary>
        /// Одна расцветка одной причёски: имя папки и ИМЕНА ПОВЕРХНОСТЕЙ,
        /// которые она перекрашивает.
        ///
        /// Поверхности перечислены не для красоты: подмена цвета идёт по имени
        /// поверхности (порядок сабмешей у причёски не гарантирован), а адрес
        /// материала собирается из причёски, цвета и этого имени. Поверхность,
        /// которой в папке нет, остаётся прототипной — так и задумано, пресет
        /// красит только то, чего касается.
        /// </summary>
        [System.Serializable]
        public sealed class HairColour
        {
            public string hair = string.Empty;
            public string colour = string.Empty;
            public List<string> surfaces = new();
        }

        [Tooltip("Расцветки причёсок. Прототипный (не перекрашенный) вариант в список НЕ входит.")]
        public List<HairColour> hairColours = new();

        private static ActorAppearanceCatalog _instance;
        private static bool _tried;
        private static bool _subscribed;
        private Dictionary<string, List<HairColour>> _coloursByHair;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _tried = false;
            _subscribed = false;
        }

        /// <summary>Каталог, или null, если ассета нет.</summary>
        public static ActorAppearanceCatalog Instance
        {
            get
            {
                if (_tried)
                {
                    return _instance;
                }

                _tried = true;
                _instance = CreateInstance<ActorAppearanceCatalog>();
                _instance.hideFlags = HideFlags.DontSave;
                var service = ContentAssetService.Instance;
                if (!_subscribed)
                {
                    _subscribed = true;
                    service.RegistryRefreshed += RebuildFromRegistry;
                }
                RebuildFromRegistry();
                service.RefreshRegistry();

                return _instance;
            }
        }

        private static void RebuildFromRegistry()
        {
            if (_instance == null)
            {
                return;
            }

            _instance.hairstyles.Clear();
            _instance.hairColours.Clear();
            foreach (var record in ContentAssetService.Instance.Records("hair"))
            {
                _instance.hairstyles.Add(record.id);
                if (record.metadata?["colours"] is not JArray colours)
                {
                    continue;
                }

                foreach (var token in colours.OfType<JObject>())
                {
                    var colour = token.Value<string>("id");
                    var surfaces = token["surfaces"]?.Values<string>();
                    if (string.IsNullOrWhiteSpace(colour) || surfaces == null)
                    {
                        continue;
                    }

                    _instance.hairColours.Add(new HairColour
                    {
                        hair = record.id,
                        colour = colour,
                        surfaces = new List<string>(surfaces),
                    });
                }
            }

            _instance._coloursByHair = null;
        }

        /// <summary>Знает ли каталог такую причёску.</summary>
        public bool Has(string hairId) =>
            !string.IsNullOrEmpty(hairId) &&
            hairstyles.Exists(h => string.Equals(h, hairId, System.StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Расцветки одной причёски, в устойчивом порядке. Пусто — законно: у
        /// половины причёсок расцветка одна, «как из коробки».
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
                    if (entry == null || string.IsNullOrEmpty(entry.hair) || entry.surfaces.Count == 0)
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
