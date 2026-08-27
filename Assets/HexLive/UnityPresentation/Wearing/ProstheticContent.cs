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

    /// <summary>
    /// A loose prosthetic keeps one simulation id for both sides, while its
    /// atomic art is side-specific. Use the same deterministic side selection
    /// as <see cref="ProstheticWorldDropView"/> so prewarm resolves the exact
    /// bundle which the world view will request.
    /// </summary>
    internal static bool TryWorldDropObjectId(
        string definitionId, int objectId, out string contentId)
    {
        if (!TryDescribeWorldDrop(
                definitionId, objectId, out var part, out var mechanical))
        {
            contentId = string.Empty;
            return false;
        }

        contentId = ObjectId(part, definitionId, mechanical);
        return !string.IsNullOrEmpty(contentId);
    }

    internal static bool TryDescribeWorldDrop(
        string definitionId, int objectId, out BodyPart part, out bool mechanical)
    {
        mechanical = definitionId is ContentIds.MechanicalArm or ContentIds.MechanicalLeg;
        var left = (objectId & 1) == 0;
        if (definitionId is ContentIds.WoodenArm or ContentIds.MechanicalArm)
        {
            part = left ? BodyPart.ArmL : BodyPart.ArmR;
            return true;
        }
        if (definitionId is ContentIds.WoodenLeg or ContentIds.MechanicalLeg)
        {
            part = left ? BodyPart.LegL : BodyPart.LegR;
            return true;
        }

        part = default;
        return false;
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
