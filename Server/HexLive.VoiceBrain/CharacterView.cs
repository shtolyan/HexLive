using System.Text;
using System.Text.Json;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.7: describe_colonist is 30–80 KB; a voice turn cannot afford that many
/// prompt tokens. This bounded projection keeps body, mood and current sight,
/// with the ids the tools need; remembered objects stay explicitly separate.
/// </summary>
public static class CharacterView
{
    public const int MaxPerception = 2400;
    public const int MaxObjects = 24;
    public const int MaxPeople = 8;
    public const int MaxWornItems = 32;
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };

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

    public static string Build(JsonElement d, string query = "")
    {
        var sb = new StringBuilder();
        sb.Append("Имя: ").Append(Name(d)).Append('\n');
        if (d.TryGetProperty("tick", out var tick) && tick.TryGetInt64(out var t))
            sb.Append("Свежий снимок сервера, tick=").Append(t).Append('\n');
        if(d.TryGetProperty("perceptionTick",out var pt) && pt.TryGetInt32(out var observed))
            sb.Append("Текущее восприятие обновлено на tick=").Append(observed).Append('\n');
        if(d.TryGetProperty("environment",out var environment) && environment.ValueKind==JsonValueKind.Object)
            sb.Append("Окружение сейчас: ").Append(environment.GetRawText()).Append('\n');
        AppendWardrobe(sb, d);
        if (d.TryGetProperty("stateSummary", out var state)) sb.Append("Тело и занятие: ").Append(state.GetString()).Append('\n');
        if (d.TryGetProperty("bodyNeeds", out var needs))
            // Words, not numbers: the model read hunger 0.71 ("1 = starving") as "barely hungry".
            sb.Append("Самочувствие: ").Append(string.Join(", ", Feelings(needs))).Append('\n');
        if (d.TryGetProperty("inOwnCamp", out var camp) && camp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            sb.Append("В своём лагере: ").Append(camp.GetBoolean() ? "да" : "нет").Append('\n');
        if (d.TryGetProperty("campStock", out var stock)) sb.Append("Запасы лагеря: ").Append(stock.GetRawText()).Append('\n');
        AppendCamps(sb, d);
        AppendKnownPeople(sb, d);
        if (d.TryGetProperty("execution", out var exec)) sb.Append("Текущее выполнение: ").Append(exec.GetRawText()).Append('\n');
        if (d.TryGetProperty("inventoryItems", out var inv) && inv.ValueKind == JsonValueKind.Array)
        {
            var items = inv.EnumerateArray().Select(ItemLabel).GroupBy(s => s)
                .Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key).ToArray();
            sb.Append("В инвентаре (это НЕ надето): ").Append(string.Join(", ", items.Take(MaxObjects)));
            if (items.Length > MaxObjects) sb.Append("; прочие вещи опущены");
            sb.Append('\n');
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
                if (CampLabel(Text(n, "faction")) is { } campLabel) sb.Append(' ').Append(campLabel);
                if (n.TryGetProperty("aidKind", out var aid) && aid.GetString() is { Length: > 0 } a && a != "None") sb.Append(" нужна помощь=").Append(a);
                // §171.25: the server always sent her side of the bond; the projection used to drop it.
                if (n.TryGetProperty("observerRelationship", out var rel) && Relation(rel) is { Length: > 0 } words)
                    sb.Append(" отношение: ").Append(words);
                sb.Append('\n');
            }
        }
        if (d.TryGetProperty("visibleItems", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            sb.Append("Вижу сейчас — объекты (objectId, что это, действия):\n");
            var candidates = SurroundingsView.CurrentObjects(d);
            var groups=candidates.GroupBy(o=>SurroundingsView.Text(o,"itemId")).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key).ToArray();
            sb.Append("Всего в текущем зрении: ").Append(candidates.Length).Append(" объектов; по типам: ")
                .Append(string.Join(", ",groups.Take(12).Select(g=>$"{g.Key}×{g.Count()}"))).Append(groups.Length>12 ? "; прочие типы опущены" : "").Append('\n');
            // Facilities are often newer than loose items: an ID prefix hid
            // beds behind the first 24 sticks. Keep a bounded deterministic list.
            foreach (var o in SurroundingsView.Select(d,query).Take(MaxObjects))
            {
                var verbs = o.TryGetProperty("interactions", out var acts) && acts.ValueKind == JsonValueKind.Array
                    ? string.Join("/", acts.EnumerateArray().Select(a => a.TryGetProperty("type", out var t) ? t.GetString() : null).Where(t => t != null).Distinct())
                    : "";
                sb.Append("- ").Append(o.GetProperty("objectId").GetInt32()).Append(' ').Append(o.GetProperty("itemId").GetString())
                  .Append(verbs.Length > 0 ? " [" + verbs + "]" : "").Append('\n');
                if(o.TryGetProperty("interactions",out var available) && available.ValueKind==JsonValueKind.Array)
                    foreach(var a in available.EnumerateArray())
                        if(a.TryGetProperty("toolRequirementMet",out var met) && met.ValueKind==JsonValueKind.False)
                            sb.Append("  Для ").Append(SurroundingsView.Text(a,"type")).Append(" отсутствует нужный инструмент\n");
                foreach(var key in new[]{"displayName","distance","reachable","occupied","sameCamp","collector","fire","construction"})
                    if(o.TryGetProperty(key,out var value)) sb.Append("  ").Append(key).Append('=').Append(value.GetRawText()).Append('\n');
            }
            if (candidates.Length > MaxObjects)
                sb.Append("Этот список неполный; используй inspect_surroundings по названию или типу, прежде чем говорить «не вижу».\n");
        }
        if (d.TryGetProperty("knownSleepPlaces", out var known) && known.ValueKind == JsonValueKind.Object &&
            known.TryGetProperty("objects", out var beds) && beds.ValueKind == JsonValueKind.Array)
        {
            sb.Append("Лично известные места сна (память, не подтверждает наличие или доступность сейчас):\n");
            foreach (var bed in beds.EnumerateArray().Take(8))
            {
                sb.Append("- ");
                foreach (var key in new[] { "objectId", "definitionId", "lastSeenTick", "lastKnownTile", "provenance" })
                    if (bed.ValueKind == JsonValueKind.Object && bed.TryGetProperty(key, out var value))
                        sb.Append(key).Append('=').Append(value.GetRawText()).Append(' ');
                sb.Append('\n');
            }
            sb.Append("Можно попытаться Sleep по известному objectId; отказ сервера означает, что место сейчас недоступно. Не объявляй сон начатым по одному accepted.\n");
        }
        if(d.TryGetProperty("mobs",out var mobs) && mobs.ValueKind==JsonValueKind.Array)
            sb.Append("Вижу сейчас — звери (distanceTiles в гексах): ")
                .Append(JsonSerializer.Serialize(mobs.EnumerateArray().Take(8))).Append('\n');
        sb.Append("Память и архив не являются текущим зрением. Нет предмета в коротком списке — уточни inspect_surroundings.\n");
        return sb.ToString();
    }

    // §171.25: common knowledge about the island's camps. Never residents.
    private static void AppendCamps(StringBuilder sb, JsonElement d)
    {
        if (!d.TryGetProperty("camps", out var camps) || camps.ValueKind != JsonValueKind.Array || camps.GetArrayLength() == 0) return;
        sb.Append("Лагеря острова (общеизвестно; кто живёт в чужих лагерях, знаешь только по встречам):\n");
        foreach (var c in camps.EnumerateArray())
        {
            sb.Append("- Лагерь ").Append(Number(c, "number")).Append(" — ");
            var here = Flag(c, "here");
            if (Flag(c, "own")) sb.Append(here ? "твой, ты сейчас в нём" : "твой");
            if (!here)
            {
                if (Flag(c, "own")) sb.Append(", ");
                sb.Append(DirectionWords(Text(c, "direction"))).Append(", ").Append(DistanceWords(Number(c, "distanceTiles")));
            }
            if (!Flag(c, "own"))
            {
                sb.Append(Text(c, "stance") == "hostile" ? "; враждует с вашим лагерем" : "; соседи, не враги");
                if (here) sb.Append("; ты сейчас в нём");
                else sb.Append(Flag(c, "visited") ? "; бывать там уже доводилось" : "; бывать там не доводилось");
            }
            sb.Append('\n');
        }
    }

    // §171.25: personal experience — people she has seen or dealt with.
    private static void AppendKnownPeople(StringBuilder sb, JsonElement d)
    {
        if (!d.TryGetProperty("knownPeople", out var people) || people.ValueKind != JsonValueKind.Array) return;
        sb.Append("Знакомые люди (личный опыт; где они сейчас и живы ли, знаешь, только если видишь):\n");
        if (people.GetArrayLength() == 0) sb.Append("- пока никого\n");
        foreach (var p in people.EnumerateArray())
        {
            var name = p.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Object &&
                names.TryGetProperty("ru", out var ru) && ru.GetString() is { Length: > 0 } r ? r : Text(p, "nameId");
            sb.Append("- ").Append(name.Length > 0 ? name : "?").Append(" (npcId=").Append(Number(p, "npcId")).Append(')');
            if (CampLabel(Text(p, "camp")) is { } camp) sb.Append(", ").Append(camp).Append(Flag(p, "sameCamp") ? " (твой)" : "");
            sb.Append(": ").Append(p.TryGetProperty("relationship", out var rel) && Relation(rel) is { Length: > 0 } words
                ? words : "видели друг друга, больше ничего");
            if (Flag(p, "visibleNow")) sb.Append("; сейчас рядом");
            else if (Hours(p, "hoursSinceSeen") is { } seen) sb.Append("; виделись ").Append(Ago(seen));
            sb.Append(Hours(p, "hoursSinceTalk") is { } talk ? "; говорили " + Ago(talk) : "; ни разу не говорили").Append('\n');
        }
        var total = d.TryGetProperty("knownPeopleTotal", out var t) && t.TryGetInt32(out var n) ? n : people.GetArrayLength();
        if (total > people.GetArrayLength())
            sb.Append("- и ещё ").Append(total - people.GetArrayLength()).Append(" человек, с кем вы почти не знакомы\n");
    }

    /// <summary>Her side of a bond as words: familiarity 0..1, trust and affinity −1..1, authority 0..1.</summary>
    internal static string Relation(JsonElement rel)
    {
        if (rel.ValueKind != JsonValueKind.Object) return "";
        double Axis(string key) => rel.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var familiarity = Axis("familiarity"); var affinity = Axis("affinity"); var trust = Axis("trust");
        var parts = new List<string>
        {
            familiarity >= .7 ? "близко знакомы" : familiarity >= .4 ? "хорошо знакомы" : familiarity >= .1 ? "немного знакомы" : "почти не знакомы",
            affinity <= -.5 ? "сильная неприязнь" : affinity <= -.15 ? "неприязнь" : affinity >= .5 ? "сильная симпатия" : affinity >= .15 ? "симпатия" : "ровное отношение",
        };
        if (trust <= -.3) parts.Add("недоверие");
        else if (trust >= .6) parts.Add("полное доверие");
        else if (trust >= .2) parts.Add("доверие");
        if (Axis("authority") >= .5) parts.Add("к советам этого человека прислушиваешься");
        return string.Join(", ", parts);
    }

    internal static string? CampLabel(string faction) => faction switch
    {
        "Colony" => "Лагерь 1",
        "Outsiders" => "чужаки",
        _ when faction.StartsWith("Colony", StringComparison.Ordinal) && int.TryParse(faction["Colony".Length..], out var n) => "Лагерь " + n,
        _ => null,
    };

    private static string DirectionWords(string direction) => direction switch
    {
        "N" => "на севере", "NE" => "на северо-востоке", "E" => "на востоке", "SE" => "на юго-востоке",
        "S" => "на юге", "SW" => "на юго-западе", "W" => "на западе", "NW" => "на северо-западе",
        _ => "сторона неизвестна",
    };

    // Bands from §171.25: ≤8 tiles near, ≤25 not far, otherwise far.
    private static string DistanceWords(int tiles) => tiles <= 8 ? "рядом" : tiles <= 25 ? "недалеко" : "далеко";

    private static string Ago(int hours) => hours < 1 ? "только что" : hours < 24 ? hours + " ч назад" : hours / 24 + " дн назад";

    private static int? Hours(JsonElement row, string key) =>
        row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var h) ? h : null;

    private static string Text(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Number(JsonElement row, string key) =>
        row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static bool Flag(JsonElement row, string key) =>
        row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool HasSleep(JsonElement item) => item.TryGetProperty("interactions", out var acts) &&
        acts.ValueKind == JsonValueKind.Array && acts.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object &&
            a.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Sleep");

    // §171.22: absence of the field is an unknown wardrobe, not a naked body.
    // A bounded prefix must never claim to be a complete empty/full outfit.
    private static void AppendWardrobe(StringBuilder sb, JsonElement d)
    {
        sb.Append("Надето сейчас (только wornItems): ");
        if (!d.TryGetProperty("wornItems", out var worn) || worn.ValueKind != JsonValueKind.Array)
        {
            sb.Append("нет достоверных данных; не угадывай одежду или наготу.\n");
            return;
        }
        var count = worn.GetArrayLength();
        if (count == 0) { sb.Append("ничего, список надетых вещей пуст.\n"); return; }
        sb.Append(count).Append(" вещей\n");
        foreach (var item in worn.EnumerateArray().Take(MaxWornItems))
        {
            sb.Append("- ").Append(ItemLabel(item));
            foreach (var key in new[] { "layer", "covers", "conditionPercent", "catalogKnown" })
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value))
                    sb.Append(' ').Append(key).Append('=').Append(value.GetRawText());
            sb.Append('\n');
        }
        if (count > MaxWornItems) sb.Append("Часть надетых вещей опущена; список выше неполный.\n");
    }

    private static string ItemLabel(JsonElement item)
    {
        static string Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "") : "";
        static string Quoted(string value) => JsonSerializer.Serialize(value.Length > 96 ? value[..96] + "…" : value, ReadableJson);
        var id = Text(item, "itemId"); var name = Text(item, "displayName");
        return "itemId=" + Quoted(id) + (name.Length > 0 ? " название=" + Quoted(name) : " название неизвестно");
    }
}
