using System.Text;
using System.Text.Json;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.26: «пока тебя не было» — the game-time pause since this voice last
/// spoke with her, and what her own diary (§136) holds for that stretch.
/// </summary>
public static class AbsenceView
{
    public const int MaxEntries = 8;
    public const int RecentEntries = 4;

    public static string Build(SessionContact contact, JsonElement describe)
    {
        var sb = new StringBuilder("<since_last_talk>\n");
        var unknown = !contact.First && contact.PauseTicks == null;
        if (contact.First) sb.Append("Это ваш первый разговор с этим голосом.\n");
        else if (unknown) sb.Append("Сколько прошло с прошлого разговора с этим голосом — неизвестно.\n");
        else sb.Append("С прошлого разговора с этим голосом прошло игрового времени: ")
            .Append(Pause(contact.PauseTicks!.Value, contact.DayLengthTicks)).Append(".\n");

        var entries = Entries(describe, contact);
        if (entries.Count > 0)
        {
            sb.Append(contact.First || unknown ? "Недавно в твоём дневнике:\n" : "Пока голоса не было, в твоём дневнике:\n");
            foreach (var (hours, text) in entries) sb.Append("- ").Append(Ago(hours)).Append(": ").Append(text).Append('\n');
        }
        else if (!contact.First && !unknown) sb.Append("За это время в дневнике ничего не записано.\n");

        sb.Append("Это время твоего мира: пока голоса не было, твоя жизнь шла. Заметить паузу можно, если она заметная и к месту; " +
            "обрадоваться, упрекнуть или остаться равнодушной — по твоему отношению к голосу, а не по правилу. " +
            "Можешь упомянуть одно-два события из списка своими словами, если к месту; не пересказывай дневник целиком " +
            "и не выдумывай событий, которых в нём нет. Если в этом звонке ты уже говорила о паузе или поздоровалась — не повторяй.\n");
        return sb.Append("</since_last_talk>\n").ToString();
    }

    public static string Pause(long ticks, int dayLengthTicks)
    {
        var hours = ticks * 24 / Math.Max(1, dayLengthTicks);
        if (hours < 1) return "меньше часа — вы только что говорили";
        var days = hours / 24; hours %= 24;
        return days == 0 ? hours + " ч" : hours == 0 ? days + " дн" : days + " дн " + hours + " ч";
    }

    private static List<(int Hours, string Text)> Entries(JsonElement describe, SessionContact contact)
    {
        if (!describe.TryGetProperty("journal", out var journal) || journal.ValueKind != JsonValueKind.Array) return [];
        var rows = journal.EnumerateArray()
            .Where(e => e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Length > 0)
            .Select(e => (Tick: Long(e, "tick"), Hours: (int)Long(e, "hoursAgo"), Quiet: Long(e, "quietHours") > 0,
                Text: e.GetProperty("text").GetString()!))
            .ToList();
        if (contact.SinceTick is { } since) rows = rows.Where(r => r.Tick > since).ToList();
        else rows = rows.OrderByDescending(r => r.Tick).Take(RecentEntries).ToList();
        // Events before quiet hours, then the story told in order.
        return rows.OrderBy(r => r.Quiet).ThenByDescending(r => r.Tick).Take(MaxEntries)
            .OrderBy(r => r.Tick).Select(r => (r.Hours, r.Text)).ToList();
    }

    private static string Ago(int hours) => hours < 1 ? "только что" : hours < 24 ? hours + " ч назад" : hours / 24 + " дн назад";

    private static long Long(JsonElement row, string key) =>
        row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
