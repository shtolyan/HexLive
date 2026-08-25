using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Экспорт не отстал от ассетов.
///
/// <para>
/// Одно и то же число живёт в ТРЁХ местах, и у них разный приоритет в разных
/// рантаймах:
/// </para>
/// <list type="bullet">
/// <item><b>инициализатор поля конфига</b> — что видит Unity, если ключа нет в ассете;</item>
/// <item><b>ассет</b> (<c>*.asset</c>) — что видит Unity, если ключ есть;</item>
/// <item><b>simdata.json</b> — что видит headless-прогон и сервер.</item>
/// </list>
/// <para>
/// ⭐ Расхождение не падает и не логируется. Оно выглядит как «правка не
/// работает»: чинишь код и экспорт, а игра читает третий источник и ведёт себя
/// по-старому. §103 стоил двух кругов ровно на этом — и второй раз потому, что
/// открытая сессия Unity ПЕРЕЭКСПОРТИРОВАЛА simdata поверх ручной правки,
/// молча вернув старые значения.
/// </para>
/// <para>
/// Гейт сравнивает то, что увидит Unity (ассет, иначе инициализатор), с тем,
/// что лежит в экспорте. Несовпадение значит ровно одно: пора переснять
/// <b>HexLive ▸ Export Sim Data</b>. Проверка НАЛИЧИЯ ключей — отдельный гейт
/// (<see cref="BalanceParityGateTests"/>); здесь про ЗНАЧЕНИЯ.
/// </para>
/// </summary>
public sealed class SimDataFreshnessGateTests
{
    private static string BalanceAssets =>
        Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "RuntimeSource", "Balance");

    private static string ConfigSources =>
        Path.Combine(RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Config");

    [Test]
    public void ExportMatchesWhatUnityWouldRead()
    {
        var simdata = SimData.BalanceValues();
        Assert.That(simdata, Is.Not.Empty, "В simdata.json нет секции balance.");

        var drift = new List<string>();
        var compared = 0;

        foreach (var assetPath in Directory.EnumerateFiles(BalanceAssets, "*.asset"))
        {
            var config = ConfigFor(assetPath);
            if (config == null)
            {
                continue; // ассет без распознанного конфига — не наше дело
            }

            var assetValues = UnityAsset.Scalars(assetPath);

            foreach (var field in config.Fields)
            {
                // Что увидит Unity: ассет главнее, инициализатор — запасной.
                var effective = assetValues.TryGetValue(field.Name, out var fromAsset)
                    ? fromAsset
                    : field.Initializer;
                if (effective is null)
                {
                    continue; // не скаляр (строка, ссылка) — не сравниваем
                }

                var key = field.Targets
                    .Select(t => t + "." + field.StaticName)
                    .FirstOrDefault(simdata.ContainsKey);
                if (key == null)
                {
                    continue; // статик не экспортируется — это ловит другой гейт
                }

                compared++;
                if (Math.Abs(simdata[key] - effective.Value) > 1e-4)
                {
                    drift.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: Unity прочитает {1}, а в экспорте {2}   ({3}.{4})",
                        key, effective.Value, simdata[key],
                        Path.GetFileName(assetPath), field.Name));
                }
            }
        }

        Assert.That(compared, Is.GreaterThan(50),
            "Сравнить удалось подозрительно мало полей — сломан разбор, а не данные.");

        Assert.That(drift, Is.Empty,
            "Экспорт разошёлся с тем, что прочитает Unity. Это не падает и не " +
            "логируется — выглядит как «правка не работает»: чинишь код, а игра " +
            "берёт третий источник. Переснять: HexLive ▸ Export Sim Data (JSON):\n  " +
            string.Join("\n  ", drift));
    }

    /// <summary>
    /// То же самое для ТАЙМИНГОВ УДАРА. Отдельно, потому что они не скаляры, а
    /// строки таблицы, и живут в других ассетах — но грабли те же, и §103
    /// наступил именно на них: у кулака в ассете стояли заглушки 0.2 с, а
    /// длительность считается как замах + доигрыш.
    /// </summary>
    [Test]
    public void StrikeTimingsMatchTheGearAssets()
    {
        var gearAssets = Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "RuntimeSource", "Gear");
        if (!Directory.Exists(gearAssets))
        {
            Assert.Ignore("Каталог ассетов снаряжения не найден.");
        }

        var exported = SimData.GearStrikes();
        var drift = new List<string>();
        var compared = 0;

        foreach (var assetPath in Directory.EnumerateFiles(gearAssets, "*.asset"))
        {
            var rows = UnityAsset.StrikeRows(assetPath);
            if (rows.Count == 0)
            {
                continue;
            }

            var gearId = GearIdFor(assetPath);
            if (!exported.TryGetValue(gearId, out var fromExport))
            {
                drift.Add(assetPath + ": в ассете " + rows.Count +
                          " строк удара, а в экспорте у '" + gearId + "' их нет вовсе");
                continue;
            }

            if (fromExport.Count != rows.Count)
            {
                drift.Add(gearId + ": строк удара в ассете " + rows.Count +
                          ", в экспорте " + fromExport.Count);
                continue;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                compared++;
                // Длительность НЕ хранится: ассет держит доигрыш, а длительность
                // это замах + доигрыш (GearConfig). Разъехаться тут особенно
                // легко, потому что имена полей разные.
                var expectedDuration = rows[i].HitDelay + rows[i].Follow;
                if (Math.Abs(fromExport[i].HitDelay - rows[i].HitDelay) > 1e-4 ||
                    Math.Abs(fromExport[i].Duration - expectedDuration) > 1e-4 ||
                    Math.Abs(fromExport[i].Cooldown - rows[i].Cooldown) > 1e-4)
                {
                    drift.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} удар #{1}: ассет даёт замах {2} длительность {3} отдых {4}, " +
                        "экспорт — {5} / {6} / {7}",
                        gearId, i + 1, rows[i].HitDelay, expectedDuration, rows[i].Cooldown,
                        fromExport[i].HitDelay, fromExport[i].Duration, fromExport[i].Cooldown));
                }
            }
        }

        Assert.That(drift, Is.Empty,
            "Тайминги удара в экспорте разошлись с ассетом снаряжения. В игре " +
            "выигрывает АССЕТ, поэтому правка экспорта тут бесполезна — и наоборот, " +
            "открытая Unity переэкспортирует поверх неё. Переснять экспорт:\n  " +
            string.Join("\n  ", drift));

        Assert.That(compared, Is.GreaterThan(0), "Ни одной строки удара не сверено.");
    }

    // ── разбор конфигов ──────────────────────────────────────────────────

    private sealed class ConfigField
    {
        public string Name;
        public string StaticName;
        public double? Initializer;
        public List<string> Targets = new List<string>();
    }

    private sealed class ConfigClass
    {
        public List<ConfigField> Fields = new List<ConfigField>();
    }

    /// <summary>Ассет → его класс конфига, по guid скрипта.</summary>
    private static ConfigClass ConfigFor(string assetPath)
    {
        var text = File.ReadAllText(assetPath);
        var guid = Regex.Match(text, @"m_Script:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32})");
        if (!guid.Success)
        {
            return null;
        }

        foreach (var meta in Directory.EnumerateFiles(ConfigSources, "*.cs.meta"))
        {
            if (!File.ReadAllText(meta).Contains(guid.Groups[1].Value, StringComparison.Ordinal))
            {
                continue;
            }

            return ParseConfig(meta.Substring(0, meta.Length - ".meta".Length));
        }

        return null;
    }

    private static ConfigClass ParseConfig(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        var targets = Regex.Matches(text, @"\[MirrorTarget\(typeof\((\w+)\)\)\]")
            .Select(m => m.Groups[1].Value).ToList();
        if (targets.Count == 0)
        {
            return null;
        }

        var config = new ConfigClass();
        foreach (Match m in Regex.Matches(text,
            @"public\s+(?:int|float|bool)\s+(\w+)\s*=\s*([^;]+);"))
        {
            var line = LineAround(text, m.Index);
            if (line.Contains("[MirrorIgnore]", StringComparison.Ordinal))
            {
                continue;
            }

            var name = m.Groups[1].Value;
            var raw = m.Groups[2].Value.Trim().TrimEnd('f');

            var explicitMap = Regex.Match(line, @"\[MirrorField\(typeof\((\w+)\),\s*""(\w+)""\)\]");
            var field = new ConfigField
            {
                Name = name,
                StaticName = explicitMap.Success
                    ? explicitMap.Groups[2].Value
                    : char.ToUpperInvariant(name[0]) + name.Substring(1),
                Targets = explicitMap.Success
                    ? new List<string> { explicitMap.Groups[1].Value }
                    : targets,
            };

            if (raw == "true")
            {
                field.Initializer = 1;
            }
            else if (raw == "false")
            {
                field.Initializer = 0;
            }
            else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                field.Initializer = v;
            }

            config.Fields.Add(field);
        }

        return config;
    }

    private static string LineAround(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1));
        // Атрибуты могут стоять строкой выше — берём две.
        var prev = start > 0 ? text.LastIndexOf('\n', start - 1) : 0;
        var end = text.IndexOf('\n', index);
        if (end < 0)
        {
            end = text.Length;
        }

        return text.Substring(Math.Max(prev, 0), end - Math.Max(prev, 0));
    }

    private static string GearIdFor(string assetPath)
    {
        var name = Path.GetFileNameWithoutExtension(assetPath);
        return name == "fist" ? string.Empty : "tool." + name;
    }
}

}
