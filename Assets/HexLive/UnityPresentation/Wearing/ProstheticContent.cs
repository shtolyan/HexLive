using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HexLive.UnityPresentation.Wearing
{

/// <summary>
/// The eight fitted-prosthetic models live in the external HexLiveContent
/// catalog. Handles stay cached for the session: the set is tiny and the same
/// model can be requested by the world actor and the health doll.
/// </summary>
internal static class ProstheticContent
{
    private static readonly Dictionary<string, AsyncOperationHandle<GameObject>> Prefabs = new();
    private static readonly HashSet<string> ReportedFailures = new();

    internal static string Address(BodyPart part, string definitionId, bool mechanical)
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
        return $"prosthetic/{limb}/{tier}/{side}";
    }

    internal static void Load(
        BodyPart part,
        string definitionId,
        bool mechanical,
        Action<GameObject> completed)
    {
        var address = Address(part, definitionId, mechanical);
        if (string.IsNullOrEmpty(address))
        {
            completed?.Invoke(null);
            return;
        }

        if (Prefabs.TryGetValue(address, out var cached) && !cached.IsValid())
        {
            Prefabs.Remove(address);
        }

        if (!Prefabs.TryGetValue(address, out var handle))
        {
            ContentQueue.Begin(ContentQueue.Kind.Prosthetic);
            handle = Addressables.LoadAssetAsync<GameObject>(address);
            Prefabs[address] = handle;
            handle.Completed += _ => ContentQueue.End(ContentQueue.Kind.Prosthetic);
        }

        if (handle.IsDone)
        {
            Complete(address, handle, completed);
            return;
        }

        handle.Completed += loaded => Complete(address, loaded, completed);
    }

    private static void Complete(
        string address,
        AsyncOperationHandle<GameObject> handle,
        Action<GameObject> completed)
    {
        if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
        {
            completed?.Invoke(handle.Result);
            return;
        }

        if (ReportedFailures.Add(address))
        {
            Debug.LogError($"[ProstheticContent] внешний контент не загрузился по адресу " +
                           $"«{address}». Проверь группу HexLive.Prosthetics, полный каталог " +
                           "и установленный prosthetic bundle.");
        }

        completed?.Invoke(null);
    }
}

}
