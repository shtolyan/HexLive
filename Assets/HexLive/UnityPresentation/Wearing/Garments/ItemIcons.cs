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
    private static readonly Dictionary<string, float> RetryAfter = new();
    private static ContentAssetService _service;
    private static HashSet<string> _wearIds;
    public static int Revision { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        if (_service != null) _service.RegistryRefreshed -= RegistryRefreshed;
        _service = null;
        foreach (var handle in Handles.Values) handle?.Dispose();
        Cache.Clear();
        Handles.Clear();
        Loading.Clear();
        Missing.Clear();
        RetryAfter.Clear();
        _wearIds = null;
        Revision = 0;
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

    /// <summary>
    /// Reads an owner's icon as soon as its <c>main</c> entry opens the bundle.
    /// The service de-duplicates by SHA, so this pins a second asset from the
    /// already open bundle; it does not download or open an icon bundle.
    /// </summary>
    public static void PrewarmOwner(string type, string id)
    {
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
        {
            return;
        }

        var service = ObserveRegistry();
        if (service.TryGetRecord(type, id, out var record) && record.HasRealIcon)
        {
            // The owner's model has opened: retry from its already available bundle.
            Missing.Remove(id);
            RetryAfter.Remove(id);
        }
        StartLoad(id, type, id);
    }

    /// <summary>Immediate UI fallback when an owner has no authored icon.</summary>
    public static string FallbackGlyph(string id) => ItemCatalog.Resolve(id).Emoji;

    public static Sprite Load(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        var localModern = LocalModernContent.Icon(id);
        if (localModern != null) return localModern;
        ObserveRegistry();
        if (Missing.Contains(id)) return null;
        if (Cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var (type, objectId) = Owner(id);
        StartLoad(id, type, objectId);
        return null;
    }

    private static void StartLoad(string cacheId, string type, string objectId)
    {
        if (RetryAfter.TryGetValue(cacheId, out var retryAt) && Time.realtimeSinceStartup < retryAt)
            return;
        if (Missing.Contains(cacheId) || Cache.ContainsKey(cacheId) ||
            !Loading.Add(cacheId))
        {
            return;
        }

        ContentQueue.Begin(ContentQueue.Kind.Icon);
        var service = ObserveRegistry();
        service.LoadIcon(type, objectId, loaded =>
        {
            var icon = loaded?.Asset;
            if (icon != null)
            {
                Cache[cacheId] = icon;
                Handles[cacheId] = loaded;
                RetryAfter.Remove(cacheId);
            }
            else
            {
                // A failed advertised icon is a transport/load failure, not
                // proof that its art is absent. Back off instead of poisoning
                // this session's cache or retrying on every UI refresh.
                if (service.TryGetRecord(type, objectId, out var record) && record.HasRealIcon)
                    RetryAfter[cacheId] = Time.realtimeSinceStartup + 4f;
                else
                    Missing.Add(cacheId);
                loaded?.Dispose();
            }
            Loading.Remove(cacheId);
            Revision++;
            ContentQueue.End(ContentQueue.Kind.Icon);
        });
    }

    private static ContentAssetService ObserveRegistry()
    {
        var service = ContentAssetService.Instance;
        if (!ReferenceEquals(_service, service))
        {
            if (_service != null) _service.RegistryRefreshed -= RegistryRefreshed;
            _service = service;
            _service.RegistryRefreshed += RegistryRefreshed;
            RegistryRefreshed();
        }
        return service;
    }

    private static void RegistryRefreshed()
    {
        if (_service != null && _service.LastError.Length != 0) return;
        if (Missing.Count == 0 && RetryAfter.Count == 0) return;
        Missing.Clear();
        RetryAfter.Clear();
        Revision++;
    }

    private static (string Type, string Id) Owner(string id)
    {
        if (ProstheticContent.TryWorldDropObjectId(id, 0, out var prostheticId))
            return ("prosthetic", prostheticId);
        // A crafted garment with no bundle of its own shows its donor's icon
        // (WearArtAliases) — the emoji fallback read as "item lost".
        if (WearArtAliases.IsAliased(id))
        {
            return ("wear", WearArtAliases.ArtId(id));
        }
        if (ContentAssetService.Instance.TryGetRecord("wear", id, out _))
        {
            return ("wear", id);
        }
        foreach (var type in new[] { "object", "prosthetic", "building", "mob" })
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
