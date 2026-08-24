using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{

/// <summary>Applies small live-record metadata without opening object bundles.</summary>
public static class WardrobeMeta
{
    private static bool _subscribed;
    public static int LoadedItems { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _subscribed = false;
        LoadedItems = 0;
    }

    public static void Load()
    {
        var service = ContentAssetService.Instance;
        if (!_subscribed)
        {
            _subscribed = true;
            service.RegistryRefreshed += Apply;
        }
        Apply();
        service.RefreshRegistry();
    }

    private static void Apply()
    {
        var records = ContentAssetService.Instance.Records("wear");
        var remote = new Dictionary<string, GarmentParams>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var parameters = ToParams(record);
            if (parameters == null)
            {
                continue;
            }
            remote[parameters.Id] = parameters;
            RegisterSlots(record.id, record.metadata?["slots"] as JArray);
        }
        if (remote.Count == 0)
        {
            return;
        }

        var merged = GarmentLibrary.Active
            .Where(value => !remote.ContainsKey(value.Id)).ToList();
        merged.AddRange(remote.Values);
        GarmentLibrary.Override(merged);
        LoadedItems = remote.Count;
        Debug.Log($"[Мета] из live registry применено вещей: {LoadedItems}; " +
                  "sidecar JSON и Addressables catalog не читаются.");
    }

    private static GarmentParams ToParams(ContentRecord record)
    {
        var metadata = record.metadata;
        if (metadata == null || metadata["layer"] == null)
        {
            return null;
        }

        Enum.TryParse(metadata.Value<string>("layer"), out WearLayer layer);
        Enum.TryParse(metadata.Value<string>("sex"), out GarmentSex sex);
        var covers = Values<BodyPart>(metadata["covers"] as JArray);
        return new GarmentParams(
            record.id,
            metadata.Value<string>("displayName") ?? record.id,
            layer,
            metadata.Value<float?>("warmth") ?? 0f,
            metadata.Value<float?>("armor") ?? 0f,
            metadata.Value<float?>("thermalDelta") ?? 0f,
            metadata.Value<int?>("dressDurationTicks") ?? 8,
            metadata.Value<int?>("capacity") ?? 0,
            sex,
            covers)
        {
            // One logical item owns one independently versioned payload even
            // when its authoring prefab was cloned from an art prototype.
            PrototypeId = record.id,
        };
    }

    private static void RegisterSlots(string id, JArray values)
    {
        var slots = Values<WearSlot>(values);
        if (slots.Length > 0)
        {
            WearSlotCatalog.Register(id, slots);
        }
    }

    private static T[] Values<T>(JArray values) where T : struct
    {
        if (values == null)
        {
            return Array.Empty<T>();
        }
        var result = new List<T>();
        foreach (var value in values)
        {
            if (Enum.TryParse(value.Value<string>(), out T parsed))
            {
                result.Add(parsed);
            }
        }
        return result.ToArray();
    }
}

}
