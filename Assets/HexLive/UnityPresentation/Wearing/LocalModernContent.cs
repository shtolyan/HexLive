using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using UnityEngine;

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using HexLive.UnityPresentation.Bootstrap;
using Newtonsoft.Json.Linq;
using UnityEditor;
#endif

namespace HexLive.UnityPresentation.Wearing
{
    // Local authoring preview of the selected §176 clothing and object owners.
    // The player continues to load every shipped asset through its own bundle.
    internal static class LocalModernContent
    {
#if UNITY_EDITOR
        private const string CatalogPath = "Assets/HexLiveContent/People/Wardrobe/release-catalog.json";
        private static readonly Dictionary<string, (string prefab, string definition)> Wardrobe = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Sprite> Icons = new(StringComparer.Ordinal);
        private static bool _loaded;

        private static bool IsLocal => SimulationSource.Current is { } source && !source.Link.IsRemote;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Wardrobe.Clear();
            Icons.Clear();
            _loaded = false;
        }

        private static bool TryGetWear(string id, out (string prefab, string definition) record)
        {
            record = default;
            if (!IsLocal) return false;
            if (!_loaded)
            {
                _loaded = true;
                if (File.Exists(CatalogPath))
                {
                    var catalog = JObject.Parse(File.ReadAllText(CatalogPath));
                    if ((string)catalog["stage"] == "prepared-only" && (string)catalog["contentOrigin"] == "modern")
                        foreach (var item in catalog["records"])
                            if ((string)item["type"] == "wear")
                                Wardrobe.Add((string)item["id"], ((string)item["main"], (string)item["definition"]));
                }
            }
            return Wardrobe.TryGetValue(id, out record);
        }
#endif

        public static Wear WearPrefab(string id)
        {
#if UNITY_EDITOR
            if (TryGetWear(id, out var record))
                return AssetDatabase.LoadAssetAtPath<GameObject>(record.prefab)?.GetComponent<Wear>();
#endif
            return null;
        }

        public static Garments.GarmentDefinition WearDefinition(string id)
        {
#if UNITY_EDITOR
            if (TryGetWear(id, out var record))
                return AssetDatabase.LoadAssetAtPath<Garments.GarmentDefinition>(record.definition);
#endif
            return null;
        }

        public static Sprite Icon(string id)
        {
#if UNITY_EDITOR
            if (IsLocal && (ModernLootCatalog.IsModernProp(id) || PortableFurniture.CanCarry(id) ||
                id == PortalWorld.DoorDefinitionId || TryGetWear(id, out _)))
            {
                if (!Icons.TryGetValue(id, out var sprite))
                    Icons[id] = sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"Assets/HexLiveContent/Icons/{id}.png");
                return sprite;
            }
#endif
            return null;
        }
    }
}

