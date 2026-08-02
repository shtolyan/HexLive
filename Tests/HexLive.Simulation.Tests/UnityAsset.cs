using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Минимальное чтение Unity-ассетов как ТЕКСТА.
/// <para>
/// Не парсер YAML: нужны только скаляры верхнего уровня и строки таблицы
/// ударов. Полноценный разбор потянул бы зависимость, а вопрос простой — «какое
/// число увидит Unity».
/// </para>
/// </summary>
public static class UnityAsset
{
    /// <summary>Скаляры верхнего уровня: <c>  имяПоля: число</c>.</summary>
    public static Dictionary<string, double> Scalars(string assetPath)
    {
        var values = new Dictionary<string, double>();
        foreach (var line in File.ReadAllLines(assetPath))
        {
            var m = Regex.Match(line, @"^  ([a-z]\w*):\s*(-?[\d.]+(?:[eE]-?\d+)?)\s*$");
            if (m.Success &&
                double.TryParse(m.Groups[2].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v))
            {
                values[m.Groups[1].Value] = v;
            }
        }

        return values;
    }

    public readonly struct StrikeRow
    {
        public StrikeRow(double hitDelay, double follow, double cooldown)
        {
            HitDelay = hitDelay;
            Follow = follow;
            Cooldown = cooldown;
        }

        public double HitDelay { get; }

        /// <summary>Доигрыш ПОСЛЕ попадания. Длительность клипа = замах + доигрыш
        /// (см. GearConfig) — в ассете её нет, и это отдельная возможность
        /// разъехаться.</summary>
        public double Follow { get; }

        public double Cooldown { get; }
    }

    /// <summary>Строки таблицы <c>strikes:</c> у ассета снаряжения.</summary>
    public static List<StrikeRow> StrikeRows(string assetPath)
    {
        var rows = new List<StrikeRow>();
        var lines = File.ReadAllLines(assetPath);

        var inStrikes = false;
        double? hit = null, follow = null, cooldown = null;

        void Flush()
        {
            if (hit.HasValue && follow.HasValue && cooldown.HasValue)
            {
                rows.Add(new StrikeRow(hit.Value, follow.Value, cooldown.Value));
            }

            hit = follow = cooldown = null;
        }

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^  strikes:\s*$"))
            {
                inStrikes = true;
                continue;
            }

            if (!inStrikes)
            {
                continue;
            }

            // Следующий ключ верхнего уровня закрывает таблицу.
            if (Regex.IsMatch(line, @"^  [a-z]\w*:"))
            {
                Flush();
                inStrikes = false;
                continue;
            }

            if (line.TrimStart().StartsWith("- ", System.StringComparison.Ordinal))
            {
                Flush();
            }

            var m = Regex.Match(line, @"(hitDelaySeconds|followSeconds|cooldownSeconds):\s*(-?[\d.]+)");
            if (!m.Success ||
                !double.TryParse(m.Groups[2].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v))
            {
                continue;
            }

            switch (m.Groups[1].Value)
            {
                case "hitDelaySeconds": hit = v; break;
                case "followSeconds": follow = v; break;
                case "cooldownSeconds": cooldown = v; break;
            }
        }

        Flush();
        return rows;
    }
}

}
