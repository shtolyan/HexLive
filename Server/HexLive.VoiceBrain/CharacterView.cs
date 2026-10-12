using System.Text;
using System.Text.Json;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.7: describe_colonist is 30–80 KB; a voice turn cannot afford that many
/// prompt tokens. This projection keeps what the character can feel, see and
/// act on (~3 KB): body, mood, what is near with the ids the tools need.
/// </summary>
public static class CharacterView
{
    public const int MaxPerception = 2400;
    public const int MaxObjects = 24;
    public const int MaxPeople = 8;

    public static string Name(JsonElement d) => d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

    private static double? Need(JsonElement needs, string key) =>
        needs.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) ? inner.GetDouble() : null : null;

    /// <summary>Need scales turned into what she would say about herself.</summary>
    public static IEnumerable<string> Feelings(JsonElement needs)
    {
        static string? Bad(double? v, string mild, string strong, string severe) =>
            v is not { } x ? null : x >= .85 ? severe : x >= .6 ? strong : x >= .35 ? mild : null;
        static string? Low(double? v, string mild, string strong, string severe) =>
            v is not { } x ? null : x <= .15 ? severe : x <= .35 ? strong : x <= .6 ? mild : null;
        var list = new[]
        {
            Bad(Need(needs, "hunger"), "немного голодна", "голодна", "умирает от голода"),
            Bad(Need(needs, "thirst"), "хочет пить", "сильная жажда", "умирает от жажды"),
            Low(Need(needs, "energy"), "немного устала", "очень устала, хочет спать", "валится с ног"),
            Low(Need(needs, "stamina"), "чуть запыхалась", "выдохлась", "совсем нет сил"),
            Low(Need(needs, "health"), "побаливает", "ранена", "тяжело ранена"),
            Low(Need(needs, "blood"), "слабость от потери крови", "сильная кровопотеря", "на грани от кровопотери"),
            Low(Need(needs, "hygiene"), "немного грязная", "грязная", "очень грязная"),
        }.Where(x => x != null).Select(x => x!).ToList();
        return list.Count > 0 ? list : ["чувствует себя хорошо"];
    }

    public static string Build(JsonElement d)
    {
        var sb = new StringBuilder();
        sb.Append("Имя: ").Append(Name(d)).Append('\n');
        if (d.TryGetProperty("stateSummary", out var state)) sb.Append("Тело и занятие: ").Append(state.GetString()).Append('\n');
        if (d.TryGetProperty("bodyNeeds", out var needs))
            // Words, not numbers: the model read hunger 0.71 ("1 = starving") as "barely hungry".
            sb.Append("Самочувствие: ").Append(string.Join(", ", Feelings(needs))).Append('\n');
        if (d.TryGetProperty("inOwnCamp", out var camp) && camp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            sb.Append("В своём лагере: ").Append(camp.GetBoolean() ? "да" : "нет").Append('\n');
        if (d.TryGetProperty("campStock", out var stock)) sb.Append("Запасы лагеря: ").Append(stock.GetRawText()).Append('\n');
        if (d.TryGetProperty("execution", out var exec)) sb.Append("Текущее выполнение: ").Append(exec.GetRawText()).Append('\n');
        if (d.TryGetProperty("inventoryItems", out var inv) && inv.ValueKind == JsonValueKind.Array)
        {
            var items = inv.EnumerateArray().Select(i => i.TryGetProperty("itemId", out var id) ? id.GetString() : null)
                .Where(s => s != null).GroupBy(s => s!).Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key);
            sb.Append("В инвентаре: ").Append(string.Join(", ", items)).Append('\n');
        }
        if (d.TryGetProperty("visibleNpcs", out var npcs) && npcs.ValueKind == JsonValueKind.Array)
        {
            sb.Append("Люди рядом:\n");
            foreach (var n in npcs.EnumerateArray().Take(MaxPeople))
            {
                var name = n.TryGetProperty("names", out var names) && names.TryGetProperty("ru", out var ru) ? ru.GetString()
                    : n.TryGetProperty("nameId", out var nid) ? nid.GetString() : "?";
                sb.Append("- npcId=").Append(n.GetProperty("npcId").GetInt32()).Append(' ').Append(name)
                  .Append(" дистанция=").Append(n.TryGetProperty("distance", out var dist) ? dist.GetDouble().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "?");
                foreach (var flag in new[] { "hostile", "unconscious", "dying", "sameCamp" })
                    if (n.TryGetProperty(flag, out var f) && f.ValueKind == JsonValueKind.True) sb.Append(' ').Append(flag);
                if (n.TryGetProperty("aidKind", out var aid) && aid.GetString() is { Length: > 0 } a && a != "None") sb.Append(" нужна помощь=").Append(a);
                sb.Append('\n');
            }
        }
        if (d.TryGetProperty("visibleItems", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            sb.Append("Объекты (objectId, что это, действия):\n");
            foreach (var o in objects.EnumerateArray().Where(o => o.TryGetProperty("objectId", out var id) && id.ValueKind == JsonValueKind.Number).Take(MaxObjects))
            {
                var verbs = o.TryGetProperty("interactions", out var acts) && acts.ValueKind == JsonValueKind.Array
                    ? string.Join("/", acts.EnumerateArray().Select(a => a.TryGetProperty("type", out var t) ? t.GetString() : null).Where(t => t != null).Distinct())
                    : "";
                sb.Append("- ").Append(o.GetProperty("objectId").GetInt32()).Append(' ').Append(o.GetProperty("itemId").GetString())
                  .Append(verbs.Length > 0 ? " [" + verbs + "]" : "").Append('\n');
            }
        }
        if (d.TryGetProperty("perceptionSummary", out var perception) && perception.GetString() is { } p)
            sb.Append("Восприятие (ближайшие по типам): ").Append(p.Length > MaxPerception ? p[..MaxPerception] + "…" : p).Append('\n');
        return sb.ToString();
    }
}
