using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexLive.VoiceBrain;

/// <summary>§171.24: bounded retrieval from this body's current sight, never global world state.</summary>
public static class SurroundingsView
{
    public const int PageSize = 16;
    private static readonly (string Stem, string Terms)[] Aliases =
    [
        ("лиф", "bra"), ("бюст", "bra"), ("трус", "brief panty"),
        ("палк", "stick"), ("кам", "stone"), ("кокос", "coconut"),
        ("вод", "water bottle collector Drink"), ("бутыл", "bottle"),
        ("сборщик", "collector"), ("коллектор", "collector"),
        ("кроват", "bed Sleep"), ("посп", "bed Sleep"),
        ("стро", "build construction"), ("достро", "build construction"), ("пальм", "palm"), ("юкк", "yucca"),
        ("кост", "campfire"), ("сушил", "rack dryer"), ("краб", "crab"), ("собак", "dog")
    ];

    public static string Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public static bool HasAction(JsonElement row, string action) => row.TryGetProperty("interactions", out var acts) &&
        acts.ValueKind == JsonValueKind.Array && acts.EnumerateArray().Any(a => Text(a, "type") == action);

    public static JsonElement[] CurrentObjects(JsonElement describe) => describe.TryGetProperty("visibleItems", out var items) &&
        items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object &&
            o.TryGetProperty("objectId", out var id) && id.TryGetInt32(out _) &&
            (!o.TryGetProperty("fromMemory", out var memory) || memory.ValueKind != JsonValueKind.True)).ToArray() : [];

    public static double Distance(JsonElement row) => row.TryGetProperty("distance", out var d) && d.ValueKind==JsonValueKind.Number &&
        d.TryGetDouble(out var value) && double.IsFinite(value) ? value : double.MaxValue;

    private static string[] Terms(string query)
    {
        var words = Regex.Matches(query[..Math.Min(query.Length, 160)].ToLowerInvariant(), @"[\p{L}\p{N}_]+")
            .Select(m => m.Value).Where(w => w.Length >= 3).ToList();
        foreach (var (stem, terms) in Aliases)
            if (words.Any(w => w.StartsWith(stem, StringComparison.Ordinal))) words.AddRange(terms.ToLowerInvariant().Split(' '));
        return words.Distinct().ToArray();
    }

    public static int Relevance(JsonElement row,string query)=>Score(row,Terms(query));
    private static int Score(JsonElement row,string[] words)
    {
        var label=(Text(row,"itemId")+" "+Text(row,"definitionId")+" "+Text(row,"displayName")+" "+Text(row,"name")+" "+
            (row.TryGetProperty("interactions",out var acts) ? acts.GetRawText() : "")+
            (row.TryGetProperty("construction",out _) ? " construction build" : "")).ToLowerInvariant();
        return words.Count(label.Contains);
    }

    public static IEnumerable<JsonElement> Select(JsonElement describe, string query)
    {
        var terms=Terms(query);
        return CurrentObjects(describe).OrderByDescending(o=>Score(o,terms)).ThenByDescending(o=>HasAction(o,"Sleep"))
            .ThenBy(Distance).ThenBy(o=>o.GetProperty("objectId").GetInt32());
    }

    public static object Inspect(JsonElement describe, string query, int offset)
    {
        var terms=Terms(query);
        var all = Select(describe,query).Where(o => query.Length==0 || Score(o,terms)>0).ToArray();
        return new { source="currentSight", tick=describe.TryGetProperty("tick",out var t) ? t.GetInt64() : (long?)null,
            query, offset, totalMatches=all.Length, truncated=offset+PageSize<all.Length,
            nextOffset=offset+PageSize<all.Length ? offset+PageSize : (int?)null,
            meaning="Только вижу сейчас. Пустой результат не означает отсутствие в мире. Память не проверялась.",
            objects=all.Skip(offset).Take(PageSize).Select(Compact).ToArray(),
            counts=all.GroupBy(o=>Text(o,"itemId")).OrderBy(g=>g.Key).Take(16)
                .Select(g=>new{itemId=g.Key,count=g.Count()}).ToArray(),
            mobs=describe.TryGetProperty("mobs",out var mobs) && mobs.ValueKind==JsonValueKind.Array
                ? mobs.EnumerateArray().Where(m=>query.Length==0 || Score(m,terms)>0).Take(8).ToArray() : [],
            execution=describe.TryGetProperty("execution",out var execution) ? (object)execution : null };
    }

    private static Dictionary<string,JsonElement> Compact(JsonElement row)
    {
        string[] keys=["objectId","itemId","displayName","distance","reachable","occupied","perceivedTick",
            "position","tile","ownerNpcId","ownerAlive","sameCamp","interactions","collector","fire","construction",
            "waterKind","waterSips","waterCapacity"];
        return keys.Where(k => row.TryGetProperty(k,out _)).ToDictionary(k=>k,k=>row.GetProperty(k));
    }
}
