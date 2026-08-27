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
    public enum Availability
    {
        Loading,
        Ready,
        Missing,
        Failed,
    }

    private static readonly Dictionary<string, ContentAssetHandle<GameObject>> Handles =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Availability> Terminal =
        new(StringComparer.Ordinal);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        foreach (var handle in Handles.Values)
        {
            handle?.Dispose();
        }

        Handles.Clear();
        Loading.Clear();
        Terminal.Clear();
    }

    public static void Prewarm(string type, string id)
    {
        _ = GetOrRequest(type, id);
    }

    /// <summary>Резидентность (ContentResidency): отпустить один префаб.
    /// Используется только для тяжёлой семьи actor — объекты, постройки и
    /// мобы резидентны. False — загрузка в полёте, вытеснение повторят
    /// позже. Terminal тоже чистится: после выгрузки запрос честно начинает
    /// сначала.</summary>
    public static bool Evict(string type, string id)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        var key = type + "/" + id;
        if (Loading.Contains(key))
        {
            return false;
        }

        if (Handles.TryGetValue(key, out var handle))
        {
            handle?.Dispose();
            Handles.Remove(key);
        }
        Terminal.Remove(key);
        return true;
    }

    public static GameObject GetOrRequest(string type, string id)
    {
        _ = Request(type, id, out var prefab);
        return prefab;
    }

    /// <summary>
    /// Starts (or observes) one session-pinned prefab request without conflating
    /// an asynchronous first miss with a missing object. Presentation callers
    /// must keep retrying while this returns <see cref="Availability.Loading"/>;
    /// they must not install a procedural replacement in the meantime.
    /// </summary>
    public static Availability Request(string type, string id, out GameObject prefab)
    {
        prefab = null;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            return Availability.Missing;
        }

        var key = type + "/" + id;
        if (Handles.TryGetValue(key, out var ready))
        {
            prefab = ready?.Asset;
            return prefab != null ? Availability.Ready : Availability.Failed;
        }

        if (Terminal.TryGetValue(key, out var terminal))
        {
            return terminal;
        }

        if (!Loading.Add(key))
        {
            return Availability.Loading;
        }

        ContentQueue.Begin(ContentQueue.Kind.Asset);
        ContentAssetService.Instance.LoadMain<GameObject>(type, id, loaded =>
        {
            Loading.Remove(key);
            ContentQueue.End(ContentQueue.Kind.Asset);
            if (loaded == null || loaded.Asset == null)
            {
                loaded?.Dispose();
                var missing = !ContentAssetService.Instance.TryGetRecord(type, id, out _);
                Terminal[key] = missing ? Availability.Missing : Availability.Failed;
                Debug.LogError(missing
                    ? $"[AtomicContent] Нет active record для {key}; визуальный fallback запрещён."
                    : $"[AtomicContent] Bundle {key} не дал renderable entry 'main'; визуальный fallback запрещён.");
                return;
            }

            Handles[key] = loaded;
        });
        return Availability.Loading;
    }
}

}
