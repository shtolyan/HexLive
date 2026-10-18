using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Wire
{

/// <summary>
/// Sends <c>"resource.palm_leaf"</c> as one byte instead of nineteen.
/// <para>
/// Object definition ids repeat across every object in the world — 218 of them
/// drawn from a vocabulary of about forty. Spelling each one out costs ~3 KB a
/// frame, 12 KB/s, for a value both ends already know by heart: the content
/// server supplies its exact id vocabulary, while the client also builds the
/// definitions needed to render item names.
/// </para>
/// <para>
/// Both ends derive the table from the SERVER catalog, sorted by ordinal.
/// A remote viewer must exclude its client-only code defaults using the
/// explicit ObjectDefinitionIds in the handshake (§83.2.7, #487).
/// Equal protocol versions alone do not imply equal local default catalogs.
/// </para>
/// <para>
/// An id the table does not know still travels as a literal string (index 0 is
/// the escape). That matters: a definition can be added at runtime by a tuned
/// asset, and a wire format that assumed a closed vocabulary would corrupt those
/// objects instead of merely failing to compress them.
/// </para>
/// </summary>
public static class DefinitionIdTable
{
    private static string[] _byIndex = Array.Empty<string>();
    private static Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    public const int MaxIds = 65535;

    public static bool IsCanonical(IReadOnlyList<string> ids, bool allowEmpty = false)
    {
        if (ids == null || ids.Count > MaxIds || (!allowEmpty && ids.Count == 0)) return false;
        for (var i = 0; i < ids.Count; i++)
            if (string.IsNullOrWhiteSpace(ids[i]) || ids[i].Length > 1024 ||
                (i > 0 && StringComparer.Ordinal.Compare(ids[i - 1], ids[i]) >= 0)) return false;
        return true;
    }

    /// <summary>A detached snapshot of the actual encoder table, without escape slot 0.</summary>
    public static IReadOnlyList<string> ExportIds()
    {
        var ids = new string[Math.Max(0, _byIndex.Length - 1)];
        if (ids.Length > 0) Array.Copy(_byIndex, 1, ids, 0, ids.Length);
        return Array.AsReadOnly(ids);
    }

    public static bool Matches(IReadOnlyList<string> ids)
    {
        if (!IsCanonical(ids) || ids.Count != _byIndex.Length - 1) return false;
        for (var i = 0; i < ids.Count; i++)
            if (!StringComparer.Ordinal.Equals(ids[i], _byIndex[i + 1])) return false;
        return true;
    }

    /// <summary>
    /// Rebuilds the table from a catalog. Call once, after content is loaded and
    /// BEFORE the first frame is encoded or decoded — both ends must build from
    /// the same catalog, which is why the server ships its <c>simdata.json</c> in
    /// the handshake and the client applies it before worldgen.
    /// </summary>
    public static void Build(ContentCatalog catalog)
    {
        if (catalog == null)
        {
            return;
        }

        var names = new List<string>(catalog.ObjectDefinitions.Count);
        foreach (var pair in catalog.ObjectDefinitions)
        {
            names.Add(pair.Key);
        }

        // Ordinal sort: the one ordering that cannot depend on culture, on
        // dictionary internals, or on the order content happened to be loaded in.
        names.Sort(StringComparer.Ordinal);

        // Index 0 is reserved for "not in the table, string follows".
        _byIndex = new string[names.Count + 1];
        _byName = new Dictionary<string, int>(names.Count, StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            _byIndex[i + 1] = names[i];
            _byName[names[i]] = i + 1;
        }
    }

    public static int Count => _byIndex.Length;

    /// <summary>0 when the id is unknown and must be spelled out.</summary>
    public static int IndexOf(string id) =>
        id != null && _byName.TryGetValue(id, out var index) ? index : 0;

    public static string Resolve(int index) =>
        index > 0 && index < _byIndex.Length ? _byIndex[index] : string.Empty;
}

}
