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
        ContentAssetService.Instance.LoadMain<GameObject>("wear", simDefinitionId, loaded =>
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
        });
    }

    public static IReadOnlyList<Wear> GetVisuals(string simDefinitionId)
    {
        if (string.IsNullOrEmpty(simDefinitionId))
        {
            return Empty;
        }
        if (Cache.TryGetValue(simDefinitionId, out var cached))
        {
            return cached;
        }

        // Lazy misses remain retryable: completion fills Cache; no permanent
        // empty result is stored while this object's own blob travels.
        PrewarmAsync(simDefinitionId);
        return Empty;
    }
}

}
