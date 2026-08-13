using System;

namespace HexLive.Simulation.Runtime.Journal
{

/// <summary>
/// Spec §136: вытащить из <c>Message</c> хроники id человека и уточняющий
/// токен.
///
/// <para>
/// ⚠️ Разбор сообщения — это ЧТЕНИЕ, и только оно. Правило §30.17 «фильтровать
/// по типу, а Message не трогать» здесь не нарушено: строка не переписывается,
/// из неё лишь достают то, что эмитент туда положил.
/// </para>
/// <para>
/// Формат неоднороден намеренно — сообщения писались под разные системы. Три
/// формы, которые реально встречаются:
/// <c>NPC12-&gt;NPC7</c>, <c>Victim=NPC7</c> и <c>Victim=7</c> (голым числом, у
/// <c>Preyed</c>). Все три обязаны читаться, иначе часть дневников молча
/// осталась бы без имён.
/// </para>
/// </summary>
public static class JournalMessageParse
{
    /// <summary>Первый <c>NPC&lt;цифры&gt;</c> в сообщении, или null.</summary>
    public static int? FirstNpc(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var idx = message.IndexOf("NPC", StringComparison.Ordinal);
        while (idx >= 0)
        {
            if (TryDigits(message, idx + 3, out var value))
            {
                return value;
            }

            idx = message.IndexOf("NPC", idx + 3, StringComparison.Ordinal);
        }

        return null;
    }

    /// <summary>Цель стрелки <c>-&gt;NPC&lt;цифры&gt;</c>, или null.</summary>
    public static int? ArrowTarget(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var arrow = message.IndexOf("->NPC", StringComparison.Ordinal);
        return arrow < 0 ? null : TryDigits(message, arrow + 5, out var value) ? value : (int?)null;
    }

    /// <summary>
    /// Значение именованного токена как id: понимает и <c>Victim=NPC7</c>, и
    /// <c>Victim=7</c>.
    /// </summary>
    public static int? TokenNpc(string message, string token)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        var start = message.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += token.Length;
        if (string.CompareOrdinal(message, start, "NPC", 0, 3) == 0)
        {
            start += 3;
        }

        return TryDigits(message, start, out var value) ? value : (int?)null;
    }

    /// <summary>
    /// Текстовое значение токена. Префикс, оканчивающийся на <c>[</c>,
    /// читается до <c>]</c> (так устроен <c>Cause=[…]</c>), остальные — до
    /// пробела.
    /// </summary>
    public static string TokenText(string message, string token)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        var start = message.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += token.Length;
        var bracketed = token[token.Length - 1] == '[';
        var end = bracketed
            ? message.IndexOf(']', start)
            : message.IndexOf(' ', start);
        if (end < 0)
        {
            end = message.Length;
        }

        var text = message.Substring(start, Math.Max(0, end - start)).Trim();

        // У причины смерти форма "Тип: подробности" — в дневник идёт только тип,
        // подробности это отладка.
        var colon = text.IndexOf(':');
        return colon > 0 ? text.Substring(0, colon) : text;
    }

    /// <summary>
    /// Первое слово сообщения. У трети событий id предмета или конечности
    /// стоит именно там (<c>"{zone} damage=…"</c>, <c>"{part} severed …"</c>).
    /// </summary>
    public static string LeadingWord(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var end = message.IndexOf(' ');
        if (end < 0)
        {
            end = message.Length;
        }

        var word = message.Substring(0, end).Trim();

        // Отсечь то, что словом не является: "NPC12", "Given" уже разобраны
        // токенами, а число само по себе игроку ничего не говорит.
        return word.Length == 0 || IsAllDigits(word) ? string.Empty : word;
    }

    private static bool TryDigits(string message, int start, out int value)
    {
        value = 0;
        var i = start;
        while (i < message.Length && message[i] >= '0' && message[i] <= '9')
        {
            value = value * 10 + (message[i] - '0');
            i++;
        }

        return i > start;
    }

    private static bool IsAllDigits(string text)
    {
        foreach (var c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}

}
