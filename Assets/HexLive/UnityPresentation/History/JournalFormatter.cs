#nullable enable
using System;
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Localization;

namespace HexLive.UnityPresentation.History
{

/// <summary>§136: одна готовая строка дневника.</summary>
public readonly struct JournalText
{
    public JournalText(string time, string body, GameHistoryTone tone)
    {
        Time = time;
        Body = body;
        Tone = tone;
    }

    /// <summary>«День 3, 21:00».</summary>
    public string Time { get; }

    /// <summary>Сама запись — от первого лица.</summary>
    public string Body { get; }

    public GameHistoryTone Tone { get; }
}

/// <summary>
/// §136: собирает фразу дневника из записи.
///
/// <para>
/// ⭐ Главное здесь — ЛЕСТНИЦА КЛЮЧЕЙ. Термин ищется от самого выразительного к
/// самому общему:
/// </para>
/// <code>
/// journal.&lt;Type&gt;.&lt;perspective&gt;.&lt;register&gt;.&lt;bond&gt;.v&lt;n&gt;
/// journal.&lt;Type&gt;.&lt;perspective&gt;.&lt;register&gt;.v&lt;n&gt;
/// journal.&lt;Type&gt;.&lt;perspective&gt;.v&lt;n&gt;
/// journal.&lt;Type&gt;.v&lt;n&gt;
/// journal.&lt;Type&gt;
/// history.&lt;Type&gt;                 ← последний рубеж: ~90 уже написанных терминов
/// </code>
/// <para>
/// Без неё фича не вышла бы: пять регистров × пять отношений × два взгляда на
/// сотню событий — это тысячи строк перевода, и до тех пор дневник не показал
/// бы ничего. С ней художественный текст пишется для тех событий, которые
/// этого стоят, а всё прочее честно говорит словами ленты колонии и
/// дописывается по одному термину за раз, без единой правки кода.
/// </para>
/// </summary>
public static class JournalFormatter
{
    /// <summary>
    /// Сколько вариантов у ключа. Проба идёт до первой дырки, поэтому термины
    /// обязаны нумероваться подряд с v0. Кэш нужен из-за цены: панель
    /// перерисовывает полсотни записей, а <c>Loc.Has</c> — это поиск в таблице
    /// на 2300 терминов.
    /// </summary>
    private static readonly Dictionary<string, int> VariantCounts = new();

    private const int MaxVariants = 8;

    static JournalFormatter()
    {
        // Смена языка меняет состав терминов — кэш обязан протухнуть вместе с
        // ней, иначе русский дневник считал бы варианты по английской колонке.
        Loc.LanguageChanged += VariantCounts.Clear;
    }

    public static JournalText Format(JournalEntrySnapshot entry)
    {
        var time = GameHistoryFormatter.FormatTime(entry.Tick);
        if (entry.Type == "CompanionNarrative")
        {
            return new JournalText(time, entry.Extra ?? string.Empty, GameHistoryTone.Social);
        }

        var body = entry.QuietHours > 0 ? Quiet(entry) : Event(entry);
        return new JournalText(time, body, ToneOf(entry));
    }

    private static string Event(JournalEntrySnapshot entry)
    {
        var who = Loc.NpcName(entry.SubjectNameId);
        var what = ExtraText(entry.Extra);
        var key = BestKey(entry);

        if (key == null)
        {
            // Термина для дневника ещё не написали — говорим словами ленты
            // колонии. Это заметно суше, но это ПРАВДА о том, что случилось,
            // а не выдумка и не голый id события.
            var fallback = "history." + entry.Type;
            if (!Loc.Has(fallback))
            {
                return entry.Type;
            }

            // Термины ленты устроены как «{0} сделала {1}». Автор дневника —
            // всегда первое лицо, поэтому в роли деятеля стоит «я»; а когда
            // сделали ЕЙ, роли меняются местами, иначе выйдет «Я помогла
            // Джоли» ровно там, где помогли ей. Второе лицо в дательном:
            // «Джоли помогла мне».
            var receiving = entry.Perspective == 1 && !string.IsNullOrEmpty(who);
            return receiving
                ? Safe(Loc.Get(fallback), who, Loc.Get("journal.me.dative"), what)
                : Safe(Loc.Get(fallback), Loc.Get("journal.me"), who, what);
        }

        return Safe(Loc.Get(key), who, what, string.Empty);
    }

    private static string Quiet(JournalEntrySnapshot entry)
    {
        var chores = ChoreList(entry);
        var hours = entry.QuietHours;

        // «Три часа прошли тихо» вместо трёх одинаковых строк подряд — склейка
        // случилась в симуляции, здесь она только называется вслух.
        var head = hours > 1
            ? Safe(Loc.Get(Pick("journal.quiet.span", entry.Variant)),
                hours.ToString(), string.Empty, string.Empty)
            : Loc.Get(Pick("journal.quiet", entry.Variant));

        return chores.Length == 0
            ? head
            : head + " " + Safe(Loc.Get(Pick("journal.quiet.chores", entry.Variant)),
                chores, string.Empty, string.Empty);
    }

    private static string ChoreList(JournalEntrySnapshot entry)
    {
        var parts = new List<string>(3);
        AddChore(parts, entry.Chore0);
        AddChore(parts, entry.Chore1);
        AddChore(parts, entry.Chore2);
        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    private static void AddChore(List<string> into, string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        // Бытовое дело без своей короткой формы просто молчит: «рубила дрова,
        // TreeChopped» читалось бы как поломка, а не как список дел.
        var key = "journal.chore." + type;
        if (Loc.Has(key))
        {
            into.Add(Loc.Get(key));
        }
    }

    /// <summary>Самый выразительный из написанных ключей, или null.</summary>
    private static string? BestKey(JournalEntrySnapshot entry)
    {
        if (string.IsNullOrEmpty(entry.Type))
        {
            return null;
        }

        var head = "journal." + entry.Type;
        var perspective = entry.Perspective == 1 ? ".got" : ".did";
        var register = "." + RegisterSlug(entry.Register);
        var bond = "." + BondSlug(entry.Bond);

        var withPerspective = head + perspective;

        return Pick(withPerspective + register + bond, entry.Variant)
               ?? Pick(withPerspective + register, entry.Variant)
               ?? Pick(withPerspective, entry.Variant)
               ?? Pick(head, entry.Variant)
               ?? (Loc.Has(head) ? head : null);
    }

    /// <summary>
    /// Ключ конкретного варианта (<c>…​.v2</c>), или null, если у этого ключа
    /// вариантов не написано.
    /// </summary>
    private static string? Pick(string key, int variant)
    {
        var count = VariantsOf(key);
        return count == 0 ? null : key + ".v" + (variant % count);
    }

    private static int VariantsOf(string key)
    {
        if (VariantCounts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var count = 0;
        while (count < MaxVariants && Loc.Has(key + ".v" + count))
        {
            count++;
        }

        VariantCounts[key] = count;
        return count;
    }

    /// <summary>
    /// Уточнение — id предмета, конечности или причины. У него обычно есть свой
    /// термин где-то ещё (снаряжение, зоны тела, причины смерти); сырой id
    /// показываем только если термина нет вовсе — как и всюду, чтобы дырка в
    /// переводе была видна, а не замаскирована.
    /// </summary>
    private static string ExtraText(string extra)
    {
        if (string.IsNullOrEmpty(extra))
        {
            return string.Empty;
        }

        foreach (var prefix in ExtraPrefixes)
        {
            var key = prefix + extra;
            if (Loc.Has(key))
            {
                return Loc.Get(key);
            }
        }

        return extra;
    }

    private static readonly string[] ExtraPrefixes =
    {
        "journal.extra.",
        "item.",
        "part.",
        "history.aid.",
        "history.topic.",
        "history.cause."
    };

    /// <summary>
    /// Подстановка без падения. Термин дневника пишет живой человек, и рано или
    /// поздно кто-то поставит {3} там, где аргументов три: <c>string.Format</c>
    /// на этом бросает исключение и убивает всю панель. Лучше показать шаблон.
    /// </summary>
    private static string Safe(string format, string a, string b, string c)
    {
        try
        {
            return string.Format(format, a, b, c);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static string RegisterSlug(int register) => register switch
    {
        1 => "weary",
        2 => "bitter",
        3 => "tender",
        4 => "hurt",
        5 => "dying",
        _ => "plain"
    };

    private static string BondSlug(int bond) => bond switch
    {
        1 => "beloved",
        2 => "friend",
        3 => "cold",
        4 => "hated",
        _ => "neutral"
    };

    /// <summary>
    /// Цвет чернил. Тон берётся у ленты колонии — одно и то же событие не может
    /// быть тревожным в одном окне и будничным в соседнем; дневник добавляет
    /// только одно своё правило: регистр умирающей перекрывает всё.
    /// </summary>
    private static GameHistoryTone ToneOf(JournalEntrySnapshot entry)
    {
        if (entry.Register == 5)
        {
            return GameHistoryTone.Danger;
        }

        return string.IsNullOrEmpty(entry.Type)
            ? GameHistoryTone.Neutral
            : GameHistoryFormatter.ToneOf(entry.Type);
    }
}

}
