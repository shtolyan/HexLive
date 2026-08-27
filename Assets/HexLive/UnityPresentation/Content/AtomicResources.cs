using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.UnityPresentation.Config;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.Content
{

/// <summary>
/// §152 bridge for presentation assets that used to be addressed by a
/// Resources path. The path is converted to one stable content object; there
/// is deliberately no call to <see cref="Resources"/> and no local fallback.
/// A first synchronous miss starts the request, while prewarm/loading keeps the
/// resulting handle pinned for the session.
/// </summary>
public static class AtomicResources
{
    private static readonly Dictionary<string, ContentAssetHandle<UnityEngine.Object>> Handles =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<ContentAssetHandle<UnityEngine.Object>>> AllHandles =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset()
    {
        foreach (var handle in Handles.Values)
        {
            handle?.Dispose();
        }
        foreach (var handles in AllHandles.Values)
        {
            foreach (var handle in handles)
            {
                handle?.Dispose();
            }
        }

        Handles.Clear();
        AllHandles.Clear();
        Loading.Clear();
    }

    public static T Load<T>(string formerResourcePath) where T : UnityEngine.Object
    {
        if (!TryIdentity(formerResourcePath, out var type, out var id))
        {
            return null;
        }

        if (ContentAssetService.Instance.TryResolveLegacyPath(
                formerResourcePath, out var resolved))
        {
            type = resolved.type;
            id = resolved.id;
        }

        return LoadIdentity<T>(type, id, EntryFor<T>());
    }

    private static T LoadIdentity<T>(string type, string id, string entry = null)
        where T : UnityEngine.Object
    {

        var key = type + "/" + id + "#" + (entry ?? "main");
        if (Handles.TryGetValue(key, out var ready))
        {
            return ready.Asset as T;
        }

        if (Loading.Add(key))
        {
            ContentAssetService.Instance.LoadAsset<UnityEngine.Object>(type, id, entry, loaded =>
            {
                Loading.Remove(key);
                if (loaded?.Asset == null)
                {
                    loaded?.Dispose();
                    return;
                }

                Handles[key] = loaded;
            });
        }

        return null;
    }

    /// <summary>
    /// Old directory scans now enumerate independent live records whose
    /// metadata carries <c>legacyResourceFolder</c>. Each record is still
    /// downloaded/versioned separately; this method never creates a bundle for
    /// the folder as a whole.
    /// </summary>
    public static T[] LoadAll<T>(string formerResourceFolder) where T : UnityEngine.Object
    {
        if (!TryIdentity(formerResourceFolder, out var type, out _))
        {
            return Array.Empty<T>();
        }

        TryIdentity(formerResourceFolder, out type, out var id);
        if (ContentAssetService.Instance.TryResolveLegacyPath(
                formerResourceFolder, out var exact))
        {
            type = exact.type;
            id = exact.id;
        }
        if (ContentAssetService.Instance.TryGetRecord(type, id, out _))
        {
            return LoadAllIdentity<T>(type, id);
        }

        var records = ContentAssetService.Instance.Records(type)
            .Where(record => MetadataContains(
                record.metadata, "legacyResourceFolder", "legacyResourceFolders",
                formerResourceFolder))
            .ToArray();
        var values = new List<T>(records.Length);
        foreach (var record in records)
        {
            var value = LoadIdentity<T>(record.type, record.id, EntryFor<T>());
            if (value != null)
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static T[] LoadAllIdentity<T>(string type, string id)
        where T : UnityEngine.Object
    {
        var key = type + "/" + id + "#all";
        if (AllHandles.TryGetValue(key, out var ready))
        {
            return ready.Select(handle => handle.Asset).OfType<T>().ToArray();
        }

        if (Loading.Add(key))
        {
            ContentAssetService.Instance.LoadAllAssets<UnityEngine.Object>(type, id, loaded =>
            {
                Loading.Remove(key);
                AllHandles[key] = loaded?.ToList() ??
                                  new List<ContentAssetHandle<UnityEngine.Object>>();
            });
        }
        return Array.Empty<T>();
    }

    public static void Prewarm(string formerResourcePath) =>
        _ = Load<UnityEngine.Object>(formerResourcePath);

    /// <summary>Резидентность (ContentResidency): отпустить все хэндлы одного
    /// бывшего Resources-пути (карты покраски актрисы и т.п.). False —
    /// какая-то загрузка этого объекта ещё в полёте, вытеснение повторят
    /// позже.</summary>
    public static bool EvictPath(string formerResourcePath)
    {
        if (!TryIdentity(formerResourcePath, out var type, out var id))
        {
            return true;
        }
        if (ContentAssetService.Instance.TryResolveLegacyPath(
                formerResourcePath, out var resolved))
        {
            type = resolved.type;
            id = resolved.id;
        }

        var prefix = type + "/" + id + "#";
        foreach (var loading in Loading)
        {
            if (loading.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var stale = new List<string>();
        foreach (var pair in Handles)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                pair.Value?.Dispose();
                stale.Add(pair.Key);
            }
        }
        foreach (var key in stale)
        {
            Handles.Remove(key);
        }

        stale.Clear();
        foreach (var pair in AllHandles)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                foreach (var handle in pair.Value)
                {
                    handle?.Dispose();
                }
                stale.Add(pair.Key);
            }
        }
        foreach (var key in stale)
        {
            AllHandles.Remove(key);
        }
        return true;
    }

    private static string EntryFor<T>() where T : UnityEngine.Object => typeof(T).Name switch
    {
        "StyleSheet" => "style",
        nameof(WorldObjectConfig) => "world-config",
        nameof(GearConfig) => "gear-config",
        nameof(MobConfig) => "mob-config",
        _ => null,
    };

    private static bool MetadataContains(
        JObject metadata, string scalarName, string arrayName, string expected)
    {
        if (metadata == null)
        {
            return false;
        }
        if (string.Equals((string)metadata[scalarName], expected, StringComparison.Ordinal))
        {
            return true;
        }
        return metadata[arrayName] is JArray values && values.Values<string>()
            .Any(value => string.Equals(value, expected, StringComparison.Ordinal));
    }

    private static bool TryIdentity(string path, out string type, out string id)
    {
        type = string.Empty;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("HexLive/", StringComparison.Ordinal))
        {
            normalized = normalized[8..];
        }

        var slash = normalized.IndexOf('/');
        var family = slash < 0 ? normalized : normalized[..slash];
        var tail = slash < 0 ? string.Empty : normalized[(slash + 1)..];
        switch (family)
        {
            case "Actors": type = "actor"; id = tail; break;
            case "Mobs": type = "mob"; id = tail; break;
            case "Animals": type = "mob"; id = tail; break;
            case "Wear": type = "wear"; id = tail; break;
            case "Objects": type = tail.StartsWith("building.", StringComparison.Ordinal)
                    || tail.StartsWith("architecture.", StringComparison.Ordinal)
                ? "building" : "object"; id = tail; break;
            case "WorldObjects": type = "object"; id = tail; break;
            case "Gear": type = "object"; id = tail; break;
            case "UI": type = "ui"; id = Stable(tail); break;
            case "VFX": type = "vfx"; id = Stable(tail); break;
            case "Decals": type = "vfx"; id = "decal." + Stable(tail); break;
            case "BloodStainMats": type = "vfx"; id = "blood-material." + Stable(tail); break;
            case "BloodStains": type = "vfx"; id = "blood." + Stable(tail); break;
            case "BloodStainNormals": type = "vfx"; id = "blood-normal." + Stable(tail); break;
            case "Remains": type = "vfx"; id = "remains." + Stable(tail); break;
            case "Water": type = "vfx"; id = "water." + Stable(tail); break;
            case "Shaders": type = "vfx"; id = "shader." + Stable(tail); break;
            default: type = "config"; id = Stable(normalized); break;
        }

        id = id.Trim('/');
        return type.Length != 0 && id.Length != 0;
    }

    private static string Stable(string value) => value
        .Replace('/', '.')
        .Replace(' ', '-')
        .ToLowerInvariant();
}

}
