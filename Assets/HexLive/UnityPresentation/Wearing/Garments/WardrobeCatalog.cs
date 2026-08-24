using System.Collections.Generic;
using System.Linq;
using HexLive.UnityPresentation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{

/// <summary>Names and metadata come from the live §152 registry, not a locator.</summary>
public static class WardrobeCatalog
{
    private static List<string> _wear;
    private static List<string> _hair;
    private static bool _subscribed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _subscribed = false;
        Forget();
    }

    public static IReadOnlyList<string> Wear => _wear ??= Collect("wear");
    public static IReadOnlyList<string> Hair => _hair ??= Collect("hair");

    public static void Forget()
    {
        _wear = null;
        _hair = null;
    }

    private static List<string> Collect(string type)
    {
        var service = ContentAssetService.Instance;
        if (!_subscribed)
        {
            _subscribed = true;
            service.RegistryRefreshed += Forget;
        }
        service.RefreshRegistry();
        return service.Records(type).Select(value => value.id)
            .OrderBy(value => value, System.StringComparer.Ordinal).ToList();
    }

    public static void Report()
    {
        Debug.Log($"[Гардероб] в live registry: вещей {Wear.Count}, причёсок {Hair.Count}; " +
                  "каждая запись версионируется и загружается отдельно.");
    }
}

}
