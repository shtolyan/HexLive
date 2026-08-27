using System.Collections.Generic;
using HexLive.UnityPresentation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>§152 single door to independently versioned garment objects.</summary>
public static class ActorWardrobe
{
    private static readonly Dictionary<string, List<Wear>> Cache = new();
    private static readonly Dictionary<string, ContentAssetHandle<GameObject>> Handles = new();
    private static readonly HashSet<string> Loading = new();
    private static readonly IReadOnlyList<Wear> Empty = new List<Wear>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        foreach (var handle in Handles.Values)
        {
            handle?.Dispose();
        }
        Handles.Clear();
        Cache.Clear();
        Loading.Clear();
    }

    public static void PrewarmAsync(string simDefinitionId)
    {
        if (string.IsNullOrEmpty(simDefinitionId) || Cache.ContainsKey(simDefinitionId) ||
            !Loading.Add(simDefinitionId))
        {
            return;
        }

        Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.Wear);
        // A crafted sim garment may borrow another garment's bundle
        // (WearArtAliases). Cache stays keyed by the SIM id — the rest of the
        // wardrobe pipeline never learns the substitution happened.
        var artId = WearArtAliases.ArtId(simDefinitionId);
        // Metadata is part of the same owner bundle. Load it first so variant
        // materials are ready before a visual can be attached to a body.
        Garments.GarmentVariants.PrewarmAsync(artId, () =>
            ContentAssetService.Instance.LoadMain<GameObject>("wear", artId, loaded =>
        {
            var result = new List<Wear>();
            if (loaded?.Asset != null)
            {
                var wear = loaded.Asset.GetComponent<Wear>();
                if (wear != null)
                {
                    result.Add(wear);
                    Handles[simDefinitionId] = loaded;
                }
                else
                {
                    loaded.Dispose();
                }
            }

            Cache[simDefinitionId] = result;
            Loading.Remove(simDefinitionId);
            Garments.ContentQueue.End(Garments.ContentQueue.Kind.Wear);
        }));
    }

    public static IReadOnlyList<Wear> GetVisuals(string simDefinitionId)
    {
        return TryGetVisuals(simDefinitionId, out var visuals) ? visuals : Empty;
    }

    /// <summary>
    /// Separates an authoritative empty result from the temporary empty shape
    /// returned while this object's bundle is still travelling. Callers which
    /// cache presentation state must use this door: treating a pending load as
    /// "this item has no art" leaves the simulation dressed and the body nude.
    /// </summary>
    public static bool TryGetVisuals(
        string simDefinitionId,
        out IReadOnlyList<Wear> visuals)
    {
        if (string.IsNullOrEmpty(simDefinitionId))
        {
            visuals = Empty;
            return true;
        }
        if (Cache.TryGetValue(simDefinitionId, out var cached))
        {
            visuals = cached;
            return true;
        }

        // Lazy misses remain retryable: completion fills Cache; no permanent
        // empty result is stored while this object's own blob travels.
        PrewarmAsync(simDefinitionId);
        visuals = Empty;
        return false;
    }

    /// <summary>Резидентность (ContentResidency): отпустить комплект шмотки,
    /// на которую не осталось живых вью. False — загрузка ещё в полёте,
    /// вытеснение повторят позже. Следующий TryGetVisuals начнёт загрузку
    /// заново — это тот же ленивый путь, что и первый взгляд.</summary>
    public static bool Evict(string simDefinitionId)
    {
        if (string.IsNullOrEmpty(simDefinitionId))
        {
            return true;
        }
        if (Loading.Contains(simDefinitionId))
        {
            return false;
        }

        if (Handles.TryGetValue(simDefinitionId, out var handle))
        {
            handle?.Dispose();
            Handles.Remove(simDefinitionId);
        }
        Cache.Remove(simDefinitionId);
        return true;
    }
}

}
