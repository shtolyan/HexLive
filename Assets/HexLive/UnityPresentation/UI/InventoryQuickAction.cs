#nullable enable
using System;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Bootstrap;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{

/// <summary>§128.4 what a double click on one inventory cell means.</summary>
public enum InventoryQuickAction
{
    /// <summary>Nothing sensible to do — the panel says so instead of guessing.</summary>
    None,

    /// <summary>Put a carried garment on, displacing conflicts the §123 way.</summary>
    Wear,

    /// <summary>Take a worn garment off into the same person's carry.</summary>
    TakeOff,

    /// <summary>Pull the cell out of the lying person and into the looter.</summary>
    TakeFromOther
}

/// <summary>
/// §128.4 the ONE place that decides what a double click does and how long a
/// double click is. Both the exchange window and the ordinary colonist inventory
/// ask it; each still sends its own already-validated command afterwards.
/// </summary>
public static class InventoryQuickActions
{
    public const float DoubleClickSeconds = 0.35f;

    /// <summary>A thing is wearable exactly when its definition names a layer —
    /// the same test <c>PlayerInventoryMath</c> applies when it accepts Wear.</summary>
    public static bool IsWearable(ISimulationSource? runner, string definitionId) =>
        !string.IsNullOrEmpty(definitionId) && runner != null &&
        runner.TryGetObjectDefinition(definitionId, out var definition) &&
        definition?.Layer is not null;

    /// <summary>
    /// §128.4: the side decides, not the thing. Someone else's cell is always
    /// «take it to me»; my own cell is «wear it / take it off» and never «give
    /// it away», so a stray double click cannot dump my gear into a corpse.
    /// </summary>
    public static InventoryQuickAction Resolve(bool ownSide, bool worn, bool wearable)
    {
        if (!ownSide) return InventoryQuickAction.TakeFromOther;
        if (worn) return InventoryQuickAction.TakeOff;
        return wearable ? InventoryQuickAction.Wear : InventoryQuickAction.None;
    }

    /// <summary>
    /// Cell identity for <see cref="DoubleClickWatch"/>. The definition id is
    /// part of it on purpose: a worn cell is addressed by name rather than by
    /// index, so without it two different garments would share the key «-1» and
    /// one click on each would read as a double click on one.
    /// </summary>
    public static string CellKey(
        int ownerId, InventoryItemSource source, int index, string definitionId) =>
        ownerId + ":" + (int)source + ":" + index + ":" + definitionId;
}

/// <summary>
/// §128.4 second press on the same cell within <see cref="InventoryQuickActions
/// .DoubleClickSeconds"/>. The event's own <c>clickCount</c> is honoured when the
/// event system fills it in, and the timer stands in when it does not.
/// </summary>
public sealed class DoubleClickWatch
{
    private string _key = string.Empty;
    private float _time = float.NegativeInfinity;

    public bool Accept(string key, int clickCount)
    {
        if (string.IsNullOrEmpty(key))
        {
            Reset();
            return false;
        }

        var now = Time.unscaledTime;
        var same = string.Equals(key, _key, StringComparison.Ordinal);
        var isDouble = same && (clickCount >= 2 || now - _time <= InventoryQuickActions.DoubleClickSeconds);
        // A completed pair starts over: three presses are one double click and
        // one lone press, never two overlapping doubles.
        _key = isDouble ? string.Empty : key;
        _time = isDouble ? float.NegativeInfinity : now;
        return isDouble;
    }

    public void Reset()
    {
        _key = string.Empty;
        _time = float.NegativeInfinity;
    }
}

}
