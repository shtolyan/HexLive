using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;

namespace HexLive.UnityPresentation.Content
{

/// <summary>
/// Session-pinned prefab entries loaded through <see cref="ContentAssetService"/>.
/// Synchronous presentation code may ask for a prefab without ever falling back
/// to Resources: the first request starts an async load and a later frame sees it.
/// </summary>
public static class ContentPrefabCache
{
    private static readonly Dictionary<string, ContentAssetHandle<GameObject>> Handles =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        foreach (var handle in Handles.Values)
        {
            handle?.Dispose();
        }

        Handles.Clear();
        Loading.Clear();
    }

    public static void Prewarm(string type, string id)
    {
        _ = GetOrRequest(type, id);
    }

    public static GameObject GetOrRequest(string type, string id)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var key = type + "/" + id;
        if (Handles.TryGetValue(key, out var ready))
        {
            return ready?.Asset;
        }

        if (!Loading.Add(key))
        {
            return null;
        }

        ContentQueue.Begin(ContentQueue.Kind.Asset);
        ContentAssetService.Instance.LoadMain<GameObject>(type, id, loaded =>
        {
            Loading.Remove(key);
            ContentQueue.End(ContentQueue.Kind.Asset);
            if (loaded == null || loaded.Asset == null)
            {
                loaded?.Dispose();
                return;
            }

            Handles[key] = loaded;
        });
        return null;
    }
}

}
