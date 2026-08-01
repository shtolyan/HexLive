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
/// catalog is static, identical on server and client, and the client builds one
/// anyway to render item names.
/// </para>
/// <para>
/// Nothing is negotiated. Both ends derive the same table from the same catalog,
/// sorted by ordinal, so index N means the same thing on both sides by
/// construction — no handshake payload, nothing to get out of sync.
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
