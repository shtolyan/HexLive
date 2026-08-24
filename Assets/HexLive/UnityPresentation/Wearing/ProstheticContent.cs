using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>Each fitted prosthetic is an independent §152 object and bundle.</summary>
internal static class ProstheticContent
{
    private static readonly Dictionary<string, ContentAssetHandle<GameObject>> Prefabs = new();
    private static readonly HashSet<string> Loading = new();
    private static readonly Dictionary<string, List<Action<GameObject>>> Waiters = new();
    private static readonly HashSet<string> ReportedFailures = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        foreach (var handle in Prefabs.Values) handle?.Dispose();
        Prefabs.Clear();
        Loading.Clear();
        Waiters.Clear();
        ReportedFailures.Clear();
    }

    internal static string Address(BodyPart part, string definitionId, bool mechanical)
    {
        var id = ObjectId(part, definitionId, mechanical);
        return string.IsNullOrEmpty(id) ? string.Empty : "prosthetic/" + id;
    }

    private static string ObjectId(BodyPart part, string definitionId, bool mechanical)
    {
        if (part is not (BodyPart.ArmL or BodyPart.ArmR or BodyPart.LegL or BodyPart.LegR))
        {
            return string.Empty;
        }

        var limb = part is BodyPart.ArmL or BodyPart.ArmR ? "arm" : "leg";
        var tier = mechanical ||
                   (!string.IsNullOrEmpty(definitionId) &&
                    definitionId.IndexOf("mechanical", StringComparison.OrdinalIgnoreCase) >= 0)
            ? "mechanical"
            : "wood";
        var side = part is BodyPart.ArmL or BodyPart.LegL ? "l" : "r";
        return $"{limb}.{tier}.{side}";
    }

    internal static void Load(
        BodyPart part,
        string definitionId,
        bool mechanical,
        Action<GameObject> completed)
    {
        var id = ObjectId(part, definitionId, mechanical);
        if (string.IsNullOrEmpty(id))
        {
            completed?.Invoke(null);
            return;
        }

        if (Prefabs.TryGetValue(id, out var cached))
        {
            completed?.Invoke(cached.Asset);
            return;
        }

        if (!Waiters.TryGetValue(id, out var callbacks))
        {
            callbacks = new List<Action<GameObject>>();
            Waiters[id] = callbacks;
        }
        if (completed != null)
        {
            callbacks.Add(completed);
        }
        if (!Loading.Add(id))
        {
            return;
        }

        ContentQueue.Begin(ContentQueue.Kind.Prosthetic);
        ContentAssetService.Instance.LoadMain<GameObject>("prosthetic", id, loaded =>
        {
            ContentQueue.End(ContentQueue.Kind.Prosthetic);
            Loading.Remove(id);
            var prefab = loaded?.Asset;
            if (loaded != null)
            {
                Prefabs[id] = loaded;
            }
            else if (ReportedFailures.Add(id))
            {
                Debug.LogError(
                    $"[ProstheticContent] объект prosthetic/{id} не загрузился; " +
                    "общего prosthetic/catalog fallback больше нет.");
            }

            var pending = Waiters[id].ToArray();
            Waiters.Remove(id);
            foreach (var callback in pending)
            {
                callback(prefab);
            }
        });
    }
}

}
