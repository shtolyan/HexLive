using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{

/// <summary>
/// §152.2 icons have no catalog and no bundle of their own. `icon` is loaded
/// from the exact same type/id record and SHA as the represented object.
/// </summary>
public static class ItemIcons
{
    private static readonly Dictionary<string, Sprite> Cache = new();
    private static readonly Dictionary<string, ContentAssetHandle<Sprite>> Handles = new();
    private static readonly HashSet<string> Loading = new();
    private static readonly HashSet<string> Missing = new();
    private static HashSet<string> _wearIds;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        foreach (var handle in Handles.Values) handle?.Dispose();
        Cache.Clear();
        Handles.Clear();
        Loading.Clear();
        Missing.Clear();
        _wearIds = null;
    }

    public static string Address(string id)
    {
        var (type, objectId) = Owner(id);
        return $"{type}/{objectId}#icon";
    }

    /// <summary>
    /// Compatibility entry point. The world-aware prewarm lives in
    /// <see cref="ScenePrewarm"/>; loading every registry icon here would also
    /// download every owning model and break atomic laziness.
    /// </summary>
    public static void PrewarmAll() => ContentAssetService.Instance.RefreshRegistry();

    /// <summary>
    /// Starts icon loads for exactly the objects the current world can show.
    /// Every request participates in <see cref="ContentQueue"/>, so the loading
    /// curtain cannot open while a real owner icon is still in flight.
    /// </summary>
    public static void Prewarm(IEnumerable<string> ids)
    {
        if (ids == null)
        {
            return;
        }

        foreach (var id in ids)
        {
            Load(id);
        }
    }

    /// <summary>Immediate UI fallback when an owner has no authored icon.</summary>
    public static string FallbackGlyph(string id) => ItemCatalog.Resolve(id).Emoji;

    public static Sprite Load(string id)
    {
        if (string.IsNullOrEmpty(id) || Missing.Contains(id))
        {
            return null;
        }
        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }
        if (!Loading.Add(id))
        {
            return null;
        }

        var (type, objectId) = Owner(id);
        ContentQueue.Begin(ContentQueue.Kind.Icon);
        ContentAssetService.Instance.LoadIcon(type, objectId, loaded =>
        {
            var icon = loaded?.Asset;
            if (icon != null)
            {
                Cache[id] = icon;
                Handles[id] = loaded;
            }
            else
            {
                // Missing icon is an authoritative terminal result for this
                // session. Do not restart the same owner-bundle request on
                // every 4 Hz UI rebuild; every caller can show its emoji now.
                Missing.Add(id);
                loaded?.Dispose();
            }
            Loading.Remove(id);
            ContentQueue.End(ContentQueue.Kind.Icon);
        });
        return null;
    }

    private static (string Type, string Id) Owner(string id)
    {
        if (ContentAssetService.Instance.TryGetRecord("wear", id, out _))
        {
            return ("wear", id);
        }
        foreach (var type in new[] { "object", "building", "mob" })
        {
            if (ContentAssetService.Instance.TryGetRecord(type, id, out _))
            {
                return (type, id);
            }
        }
        _wearIds ??= BuildWearIds();
        if (_wearIds.Contains(id))
        {
            return ("wear", id);
        }
        if (id.StartsWith("building.", System.StringComparison.Ordinal))
        {
            return ("building", id);
        }
        if (id.StartsWith("mob.", System.StringComparison.Ordinal))
        {
            return ("mob", id.Substring("mob.".Length));
        }

        return ("object", id);
    }

    private static HashSet<string> BuildWearIds()
    {
        var result = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var garment in GarmentLibrary.Active)
        {
            result.Add(garment.Id);
        }
        return result;
    }
}

}
