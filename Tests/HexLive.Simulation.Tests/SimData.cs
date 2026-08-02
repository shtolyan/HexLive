using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Чтение <c>simdata.json</c> ТЕКСТОМ, мимо кода игры.
/// <para>
/// Намеренно не через <c>SimDataFile</c>: гейт обязан ловить расхождение между
/// кодом и ФАЙЛОМ, а разбор файла тем же кодом закрыл бы глаза на целый класс
/// расхождений (и на то, что часть значений уже применена к статикам).
/// </para>
/// </summary>
public static class SimData
{
    public readonly struct Strike
    {
        public Strike(double hitDelay, double duration, double cooldown)
        {
            HitDelay = hitDelay;
            Duration = duration;
            Cooldown = cooldown;
        }

        public double HitDelay { get; }
        public double Duration { get; }
        public double Cooldown { get; }
    }

    /// <summary>Секция <c>balance</c>: полное имя статика → значение.</summary>
    public static Dictionary<string, double> BalanceValues()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var text = File.ReadAllText(RepoPaths.SimData);

        var section = Section(text, "\"balance\"", '{', '}');
        if (section == null)
        {
            return values;
        }

        foreach (Match m in Regex.Matches(section,
            @"""([A-Za-z_]\w*\.[A-Za-z_]\w*)""\s*:\s*(true|false|-?[\d.]+(?:[eE]-?\d+)?)"))
        {
            var raw = m.Groups[2].Value;
            if (raw == "true")
            {
                values[m.Groups[1].Value] = 1;
            }
            else if (raw == "false")
            {
                values[m.Groups[1].Value] = 0;
            }
            else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                values[m.Groups[1].Value] = v;
            }
        }

        return values;
    }

    /// <summary>Секция <c>gear</c>: id снаряжения → его строки удара.</summary>
    public static Dictionary<string, List<Strike>> GearStrikes()
    {
        var result = new Dictionary<string, List<Strike>>(StringComparer.Ordinal);
        var text = File.ReadAllText(RepoPaths.SimData);

        var gear = Section(text, "\"gear\"", '[', ']');
        if (gear == null)
        {
            return result;
        }

        // Каждая запись снаряжения — объект с "id" и, возможно, "strikes".
        foreach (Match entry in Regex.Matches(gear,
            @"""id""\s*:\s*""([^""]*)""(.*?)(?=""id""\s*:|$)", RegexOptions.Singleline))
        {
            var id = entry.Groups[1].Value;
            // Только содержимое "strikes": [...] — у самой записи снаряжения
            // есть базовые hitDelaySeconds/attackDurationSeconds/cooldownSeconds
            // с ТЕМИ ЖЕ именами, и без этого сужения они считались бы пятой
            // строкой удара.
            var body = Section(entry.Groups[2].Value, "\"strikes\"", '[', ']') ?? string.Empty;
            var strikes = new List<Strike>();

            foreach (Match s in Regex.Matches(body,
                @"""hitDelaySeconds""\s*:\s*(-?[\d.]+)\s*,\s*""attackDurationSeconds""\s*:\s*(-?[\d.]+)\s*,\s*""cooldownSeconds""\s*:\s*(-?[\d.]+)"))
            {
                strikes.Add(new Strike(
                    double.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture),
                    double.Parse(s.Groups[3].Value, CultureInfo.InvariantCulture)));
            }

            if (strikes.Count > 0)
            {
                result[id] = strikes;
            }
        }

        return result;
    }

    /// <summary>Содержимое секции по её ключу, вместе с обрамляющими скобками.</summary>
    private static string Section(string text, string key, char open, char close)
    {
        var at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = text.IndexOf(open, at);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }
}

}
