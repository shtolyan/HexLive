using System.Collections;
using System.Collections.Generic;
using HexLive.UnityPresentation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>§152 hair prefab and all colour materials share one hair bundle.</summary>
public static class HairContent
{
    private static readonly Dictionary<string, ContentAssetHandle<GameObject>> Hair = new();
    private static readonly Dictionary<string, ContentAssetHandle<Material>> Materials = new();
    private static readonly HashSet<string> Prewarming = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        foreach (var handle in Hair.Values) handle?.Dispose();
        foreach (var handle in Materials.Values) handle?.Dispose();
        Hair.Clear();
        Materials.Clear();
        Prewarming.Clear();
    }

    /// <summary>§155.5: готова ли причёска к мгновенной выдаче — тёплое
    /// появление не собирает вью, пока тяжёлые двери не прогреты.</summary>
    public static bool IsCached(string hairId) =>
        string.IsNullOrEmpty(hairId) || Hair.ContainsKey(hairId);

    public static string HairAddress(string hair) => $"hair/{hair}";
    public static string ColourAddress(string hair, string colour, string surface) =>
        $"hair/{hair}/colour/{colour}/{surface}";
    public static string WearAddress(string artId) => $"wear/{artId}";

    private static string ColourEntry(string colour, string surface) =>
        $"colour/{colour}/{surface}";

    public static void Prewarm(string hairId)
    {
        if (string.IsNullOrEmpty(hairId) || Hair.ContainsKey(hairId) || !Prewarming.Add(hairId))
        {
            return;
        }

        Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.Hair);
        ContentAssetService.Instance.LoadMain<GameObject>("hair", hairId, loaded =>
        {
            if (loaded != null)
            {
                Hair[hairId] = loaded;
            }
            Prewarming.Remove(hairId);
            Garments.ContentQueue.End(Garments.ContentQueue.Kind.Hair);
        });
    }

    public static IEnumerator LoadHair(string hairId, System.Action<Wear> done)
    {
        if (string.IsNullOrEmpty(hairId))
        {
            done(null);
            yield break;
        }
        if (Hair.TryGetValue(hairId, out var cached))
        {
            done(cached.Asset != null ? cached.Asset.GetComponent<Wear>() : null);
            yield break;
        }

        var finished = false;
        ContentAssetHandle<GameObject> result = null;
        Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.Hair);
        ContentAssetService.Instance.LoadMain<GameObject>("hair", hairId, loaded =>
        {
            result = loaded;
            finished = true;
            Garments.ContentQueue.End(Garments.ContentQueue.Kind.Hair);
        });
        while (!finished)
        {
            yield return null;
        }

        if (result == null || result.Asset == null)
        {
            Debug.LogWarning($"[HairContent] нет атомарного объекта hair/{hairId}.");
            done(null);
            yield break;
        }

        Hair[hairId] = result;
        done(result.Asset.GetComponent<Wear>());
    }

    /// <summary>Резидентность (ContentResidency): отпустить причёску, которую
    /// больше никто не носит в кадре, — префаб и все загруженные цвета одного
    /// бандла. False — загрузка в полёте, вытеснение повторят позже.</summary>
    public static bool Evict(string hairId)
    {
        if (string.IsNullOrEmpty(hairId))
        {
            return true;
        }
        if (Prewarming.Contains(hairId))
        {
            return false;
        }

        if (Hair.TryGetValue(hairId, out var handle))
        {
            handle?.Dispose();
            Hair.Remove(hairId);
        }

        var colourPrefix = hairId + "/";
        var stale = new List<string>();
        foreach (var pair in Materials)
        {
            if (pair.Key.StartsWith(colourPrefix, System.StringComparison.Ordinal))
            {
                pair.Value?.Dispose();
                stale.Add(pair.Key);
            }
        }
        foreach (var key in stale)
        {
            Materials.Remove(key);
        }
        return true;
    }

    public static IEnumerator LoadColour(
        string hair,
        ActorAppearanceCatalog.HairColour colour,
        System.Action<Dictionary<string, Material>> done)
    {
        var result = new Dictionary<string, Material>();
        if (colour == null)
        {
            done(result);
            yield break;
        }

        foreach (var surface in colour.surfaces)
        {
            var key = $"{hair}/{colour.colour}/{surface}";
            if (Materials.TryGetValue(key, out var cached))
            {
                result[surface] = cached.Asset;
                continue;
            }

            var finished = false;
            ContentAssetHandle<Material> loaded = null;
            Garments.ContentQueue.Begin(Garments.ContentQueue.Kind.HairColour);
            ContentAssetService.Instance.LoadAsset<Material>(
                "hair", hair, ColourEntry(colour.colour, surface), value =>
                {
                    loaded = value;
                    finished = true;
                    Garments.ContentQueue.End(Garments.ContentQueue.Kind.HairColour);
                });
            while (!finished)
            {
                yield return null;
            }

            if (loaded?.Asset != null)
            {
                Materials[key] = loaded;
                result[surface] = loaded.Asset;
            }
        }

        done(result);
    }
}

}
