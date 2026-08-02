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
        public StrikeRow(double hitDelay, double follow, double cooldown, string clipGuid = null)
        {
            HitDelay = hitDelay;
            Follow = follow;
            Cooldown = cooldown;
            ClipGuid = clipGuid;
        }

        /// <summary>GUID клипа, который вид играет на этот удар (null — не задан).</summary>
        public string ClipGuid { get; }

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
        string clipGuid = null;

        void Flush()
        {
            if (hit.HasValue && follow.HasValue && cooldown.HasValue)
            {
                rows.Add(new StrikeRow(hit.Value, follow.Value, cooldown.Value, clipGuid));
            }

            hit = follow = cooldown = null;
            clipGuid = null;
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

            var clip = Regex.Match(line, @"clip:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})");
            if (clip.Success)
            {
                clipGuid = clip.Groups[1].Value;
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

    /// <summary>GUID'ы клипов из <c>attackClips:</c> — снаряжение без вариантов
    /// удара (нож, топор, копьё) держит их здесь.</summary>
    public static List<string> AttackClipGuids(string assetPath)
    {
        var guids = new List<string>();
        var inList = false;

        foreach (var line in File.ReadAllLines(assetPath))
        {
            if (Regex.IsMatch(line, @"^  attackClips:\s*$"))
            {
                inList = true;
                continue;
            }

            if (!inList)
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^  [a-z]\w*:"))
            {
                break; // следующий ключ верхнего уровня
            }

            var m = Regex.Match(line, @"guid:\s*([0-9a-f]{32})");
            if (m.Success)
            {
                guids.Add(m.Groups[1].Value);
            }
        }

        return guids;
    }

    /// <summary>
    /// ДЛИНА КЛИПА В СЕКУНДАХ по его <c>.meta</c> — то, что вид реально
    /// проиграет, без запуска Unity.
    ///
    /// <para>
    /// Кадры берутся из <c>clipAnimations</c> (<c>lastFrame − firstFrame</c>),
    /// частота — 30: все клипы библиотеки пришли из Mixamo. Формула сверена с
    /// живым Unity: у <c>Punch A_once_to65</c> 65 кадров, и
    /// <c>AnimationClip.length</c> = 2.1667 с ровно.
    /// </para>
    /// <para>
    /// Возвращает 0, если мета не найдена или клипов в ней нет.
    /// </para>
    /// </summary>
    public static double ClipSecondsByGuid(string assetsRoot, string guid)
    {
        const double mixamoFps = 30.0;

        var meta = MetaByGuid(assetsRoot, guid);
        if (meta == null)
        {
            return 0;
        }

        double first = 0, last = 0;
        var seen = false;

        foreach (var line in File.ReadAllLines(meta))
        {
            var f = Regex.Match(line, @"^\s+firstFrame:\s*(-?[\d.]+)");
            if (f.Success)
            {
                first = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var l = Regex.Match(line, @"^\s+lastFrame:\s*(-?[\d.]+)");
            if (l.Success)
            {
                last = double.Parse(l.Groups[1].Value, CultureInfo.InvariantCulture);
                seen = true;
                // Первая нарезка файла и есть та, на которую ссылается ассет:
                // в этой библиотеке один клип на FBX.
                break;
            }
        }

        return seen && last > first ? (last - first) / mixamoFps : 0;
    }

    private static Dictionary<string, string> _metaByGuid;

    /// <summary>Путь к <c>.meta</c> по GUID. Индекс строится один раз на прогон.</summary>
    public static string MetaByGuid(string assetsRoot, string guid)
    {
        if (_metaByGuid == null)
        {
            _metaByGuid = new Dictionary<string, string>();
            foreach (var meta in Directory.EnumerateFiles(assetsRoot, "*.meta",
                         SearchOption.AllDirectories))
            {
                foreach (var line in File.ReadLines(meta))
                {
                    var m = Regex.Match(line, @"^guid:\s*([0-9a-f]{32})");
                    if (m.Success)
                    {
                        _metaByGuid[m.Groups[1].Value] = meta;
                        break;
                    }
                }
            }
        }

        return _metaByGuid.TryGetValue(guid, out var path) ? path : null;
    }
}

}
