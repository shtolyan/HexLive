using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Journal;

namespace HexLive.Server.Mcp;

/// <summary>
/// §171.26: the body's own diary (§136) as ready first-person Russian phrases,
/// built by the same key ladder as the player's <c>JournalFormatter</c> from
/// terms exported out of I2 (<c>Tools/export_journal_terms.py</c>).
/// </summary>
internal static class McpJournalObservations
{
    internal const int MaxEntries = 16;
    private const int MaxVariants = 8;
    private const string Language = "ru";

    private static readonly Lazy<Dictionary<string, string>> Terms = new(LoadTerms);

    private static readonly string[] ExtraPrefixes =
        { "journal.extra.", "part.", "history.aid.", "history.topic.", "history.cause." };

    public static List<object> Recent(WorldState world, NPCState npc)
    {
        var entries = npc.Journal.Entries;
        var hour = Math.Max(1, Spec136.HourTicks);
        var rows = new List<object>();
        for (var i = Math.Max(0, entries.Count - MaxEntries); i < entries.Count; i++)
        {
            var entry = entries[i];
            rows.Add(new Dictionary<string, object?>
            {
                ["tick"] = entry.Tick,
                ["hoursAgo"] = Math.Max(0, world.Tick - entry.Tick) / hour,
                ["quietHours"] = (int)entry.QuietHours,
                ["type"] = entry.Type ?? string.Empty,
                ["mood"] = RegisterSlug(entry.Register),
                ["text"] = Text(world, npc, entry),
            });
        }
        return rows;
    }

    internal static string Text(WorldState world, NPCState npc, JournalEntry entry)
    {
        if (entry.Type == "CompanionNarrative") return entry.Extra ?? string.Empty;
        var self = npc.Sex == GarmentSex.Male;
        return entry.IsQuiet ? Quiet(entry, self) : Event(world, entry, self);
    }

    private static string Event(WorldState world, JournalEntry entry, bool self)
    {
        // §58.9: «s» — автор дневника, «0»/«1» — тот, кто стоит в {0}/{1}.
        var other = SubjectMale(world, entry.SubjectNameId);
        var who = string.IsNullOrEmpty(entry.SubjectNameId) ? string.Empty
            : McpNpcObservations.LocalizedNames(entry.SubjectNameId)[Language];
        var what = ExtraText(world, entry.Extra);
        var key = BestKey(entry);
        if (key != null) return Safe(Get(key, new GenderSlots(self, other)), who, what, string.Empty);

        // Same fallback as the player's diary: the colony feed's words, «я» as
        // the doer, or «X … мне» when it was done TO her.
        var fallback = "history." + entry.Type;
        if (!Terms.Value.ContainsKey(fallback)) return entry.Type ?? string.Empty;
        return entry.Perspective == JournalPerspective.Received && who.Length > 0
            ? Safe(Get(fallback, new GenderSlots(self, other, self)), who, Get("journal.me.dative"), what)
            : Safe(Get(fallback, new GenderSlots(self, self, other)), Get("journal.me"), who, what);
    }

    private static string Quiet(JournalEntry entry, bool self)
    {
        var slots = GenderSlots.Of(self);
        var chores = new List<string>(3);
        foreach (var chore in new[] { entry.Chore0, entry.Chore1, entry.Chore2 })
            if (!string.IsNullOrEmpty(chore) && Terms.Value.TryGetValue("journal.chore." + chore, out var word))
                chores.Add(GenderedText.Resolve(word, slots));
        var head = entry.QuietHours > 1
            ? Safe(Get(Pick("journal.quiet.span", entry.Variant) ?? "journal.quiet.span", slots), entry.QuietHours.ToString(), "", "")
            : Get(Pick("journal.quiet", entry.Variant) ?? "journal.quiet", slots);
        return chores.Count == 0 ? head
            : head + " " + Safe(Get(Pick("journal.quiet.chores", entry.Variant) ?? "journal.quiet.chores", slots),
                string.Join(", ", chores), "", "");
    }

    private static string? BestKey(JournalEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Type)) return null;
        var head = "journal." + entry.Type;
        var perspective = head + (entry.Perspective == JournalPerspective.Received ? ".got" : ".did");
        var register = perspective + "." + RegisterSlug(entry.Register);
        return Pick(register + "." + BondSlug(entry.Bond), entry.Variant)
               ?? Pick(register, entry.Variant)
               ?? Pick(perspective, entry.Variant)
               ?? Pick(head, entry.Variant)
               ?? (Terms.Value.ContainsKey(head) ? head : null);
    }

    private static string? Pick(string key, int variant)
    {
        var count = 0;
        while (count < MaxVariants && Terms.Value.ContainsKey(key + ".v" + count)) count++;
        return count == 0 ? null : key + ".v" + (variant % count);
    }

    private static string ExtraText(WorldState world, string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return string.Empty;
        // §133: an object id is named by the active catalog, like everything the voice sees.
        if (world.Content.ObjectDefinitions.TryGetValue(extra, out var definition) && !string.IsNullOrEmpty(definition.DisplayName))
            return definition.DisplayName;
        foreach (var prefix in ExtraPrefixes)
            if (Terms.Value.TryGetValue(prefix + extra, out var text)) return GenderedText.Feminine(text);
        return extra;
    }

    private static string Get(string key) => Get(key, GenderSlots.Feminine);

    private static string Get(string key, GenderSlots slots) =>
        Terms.Value.TryGetValue(key, out var text) ? GenderedText.Resolve(text, slots) : key;

    /// <summary>
    /// §58.9: пол того, кто назван в записи. Запись хранит только id имени, поэтому тело ищется
    /// среди живых и мёртвых по нему; не нашли — женская форма, как и было до разметки.
    /// </summary>
    private static bool SubjectMale(WorldState world, string? nameId)
    {
        if (string.IsNullOrEmpty(nameId)) return false;
        foreach (var body in world.Entities.Npcs.Values)
            if (body.DisplayName == nameId) return body.Sex == GarmentSex.Male;
        foreach (var body in world.Entities.Corpses.Values)
            if (body.DisplayName == nameId) return body.Sex == GarmentSex.Male;
        return false;
    }

    private static string Safe(string format, string a, string b, string c)
    {
        try { return string.Format(format, a, b, c); }
        catch (FormatException) { return format; }
    }

    internal static string RegisterSlug(JournalRegister register) => register switch
    {
        JournalRegister.Weary => "weary",
        JournalRegister.Bitter => "bitter",
        JournalRegister.Tender => "tender",
        JournalRegister.Suffering => "hurt",
        JournalRegister.Dying => "dying",
        _ => "plain",
    };

    private static string BondSlug(JournalBond bond) => bond switch
    {
        JournalBond.Beloved => "beloved",
        JournalBond.Friend => "friend",
        JournalBond.Cold => "cold",
        JournalBond.Hated => "hated",
        _ => "neutral",
    };

    private static Dictionary<string, string> LoadTerms()
    {
        using var resource = typeof(McpJournalObservations).Assembly.GetManifestResourceStream("HexLive.Mcp.JournalTerms.json")
            ?? throw new InvalidDataException("Missing I2 journal terms resource");
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(resource)
            ?? throw new InvalidDataException("Invalid I2 journal terms resource");
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, languages) in all)
            if (languages.TryGetValue(Language, out var text) && text.Length > 0) terms[key] = text;
        return terms;
    }
}
