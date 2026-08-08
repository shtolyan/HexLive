using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ РУЧКИ БАЛАНСА: одно число — один смысл, и его кто-то читает.
///
/// <para>
/// Число баланса живёт в ТРЁХ местах с разным приоритетом, и это стоило двух
/// правок «которые не работали»: инициализатор в <c>*BalanceConfig.cs</c> (его
/// видит Unity, когда ключа нет в ассете), сам <c>.asset</c>, и
/// <c>simdata.json</c> (headless-прогон и сервер). <c>SimDataFreshnessGate</c>
/// сверяет вторую пару. Здесь — две дыры, которые он не видит.
/// </para>
/// <para>
/// ПЕРВАЯ: инициализатор статика <c>Spec*.cs</c> против того, на чём игра
/// РЕАЛЬНО работает. §81 держал 0.55/600/0.55, а игра шла на 0.45/240/0.35,
/// потому что зеркало перезаписывает статик на старте: врал только комментарий
/// рядом с литералом — и голый прогон без simdata, который считал другую игру.
/// </para>
/// <para>
/// Сравнение идёт с <c>simdata.json</c>, а не с инициализатором конфига,
/// потому что приоритет ТРЁХСТУПЕНЧАТЫЙ: Unity читает <c>.asset</c>, если ключ
/// там есть, и только иначе — инициализатор конфига. Сверять со второй
/// ступенью значило бы объявлять расхождением каждую тюнингованную ручку.
/// simdata — это и есть то, что применилось, а совпадение simdata с ассетом
/// сторожит <c>SimDataFreshnessGate</c>. Вместе они замыкают треугольник.
/// </para>
/// <para>
/// ВТОРАЯ: ручка, которую не читает никто. Она объявлена, экспортирована и
/// крутится в инспекторе — выглядит рабочей и молча ничего не делает. Четыре
/// такие нашлись у §81 (такт второго тычка, потолок добычи, множитель урона,
/// порог «не бить умирающую»), и все четыре пережили схемы, которые их
/// использовали.
/// </para>
/// </summary>
public sealed class BalanceKnobHygieneGateTests
{
    private static readonly Regex ConfigField = new Regex(
        @"public\s+(?:int|float|bool)\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex StaticField = new Regex(
        @"public\s+static\s+(?:int|float|bool)\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex StaticDeclaration = new Regex(
        @"public\s+static\s+(?:int|float|bool)\s+(\w+)\s*=", RegexOptions.Compiled);

    /// <summary>
    /// ⭐ РАТЧЕТ мёртвых ручек: объявлены, экспортируются, крутятся в
    /// инспекторе — и не читает их никто. Все пережили схемы, которые их
    /// использовали. Разбирать надо по одной, вместе с тем, что должно было
    /// быть на их месте, поэтому они здесь, а не удалены оптом.
    /// <para>
    /// Список только УМЕНЬШАЕТСЯ. Новая мёртвая ручка — ошибка.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownDead =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Spec49.ProactiveBoil"] = "кипячение впрок так и не завели",
            ["Spec49.BoilThirstCeiling"] = "там же",
            ["Spec82.TerritoryScore"] = "территория чужака не даёт ставку",
            ["SpecDream.BedExclusive"] = "мечта о кровати не проверяет исключительность",
            ["SimBalance.ComaWakeThreshold"] = "§60 будит по своему порогу крови",
            ["SimBalance.TanStrength"] = "загар печётся в CharacterBalance-ассете (§40.7)",
            ["SimBalance.HygieneWashGain"] = "мытьё считает свою прибавку",
        };

    /// <summary>
    /// ⭐ РАТЧЕТ уже разошедшихся дефолтов: два значения по умолчанию для одной
    /// ручки. Сегодня это не видно, потому что <c>.asset</c> перекрывает обе
    /// стороны, — но стоит убрать ключ из ассета, и игра поедет на числе,
    /// которого никто не выбирал. Так уже было с теплом костра: статик подняли
    /// до 18/11, конфиг остался на 8/4, и починка держится только ассетом.
    /// <para>
    /// Список только УМЕНЬШАЕТСЯ. Новое расхождение — ошибка, а не запись сюда.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownDefaultDrift =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EnergyRate"] = "0.005 против 0.007",
            ["BleedRateFactor"] = "0.06 против 0.09",
            ["SleepEnergyBaseBonus"] = "0.026 против 0.01",
            ["FireWarmthRange1"] = "18 против 8 — §67 подняли тепло костра в статике",
            ["FireWarmthRange2"] = "11 против 4 — там же",
            ["WashClothesNeedThreshold"] = "0.45 против 0.2",
            ["Enabled"] = "true против false",
            ["TalkRelationshipGain"] = "0.02 против 0.075",
            ["SleepComfortLeafNight"] = "0.85 против 0.3",
            ["SleepComfortBedNight"] = "1.4 против 1",
            ["AidErrandBidShare"] = "0.9 против 0.85",
            ["GrindSeverChance"] = "0.1 против 0.25",
            ["SpotRadiusTiles"] = "6 против 4",
            ["AttackMaxPack"] = "0 против 1",
            ["MaxDogs"] = "2 против 3",
            ["DogRespawnCheckTicks"] = "7200 против 3600",
        };

    [Test]
    public void StaticInitializersMatchTheirConfigInitializers()
    {
        var configDir = Path.Combine(RepoPaths.Root, "Assets", "HexLive",
            "UnityPresentation", "Config");
        Assert.That(Directory.Exists(configDir), Is.True, "Не найден каталог конфигов: " + configDir);

        var statics = StaticInitializers();
        Assert.That(statics.Count, Is.GreaterThan(50),
            $"Разобрано всего {statics.Count} статиков баланса — похоже, сломан разбор.");

        var mismatches = new List<string>();
        var staleKnown = new HashSet<string>(KnownDefaultDrift.Keys, StringComparer.Ordinal);
        var compared = 0;

        foreach (var file in Directory.EnumerateFiles(configDir, "*BalanceConfig.cs")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ConfigField.Matches(text))
            {
                var pascal = char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value.Substring(1);
                if (!TryNumber(m.Groups[2].Value, out var configValue) ||
                    !statics.TryGetValue(pascal, out var declared))
                {
                    continue;
                }

                compared++;
                // Сошлось хотя бы одно объявление — этот конфиг и зеркалит его.
                if (declared.Any(d => Math.Abs(d.Value - configValue) <= 1e-4))
                {
                    continue;
                }

                if (KnownDefaultDrift.ContainsKey(pascal))
                {
                    staleKnown.Remove(pascal);
                    continue;
                }

                mismatches.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} = {1} против {2} в {3}", pascal,
                    string.Join("/", declared.Select(d => d.Owner + " " + d.Value)),
                    configValue, Path.GetFileName(file)));
            }
        }

        Assert.That(compared, Is.GreaterThan(50),
            $"Сопоставлено всего {compared} пар — похоже, сломано сопоставление имён, " +
            "а не данные.");

        Assert.That(mismatches, Is.Empty,
            "Одна ручка — два разных значения по умолчанию:\n  " +
            string.Join("\n  ", mismatches) +
            "\nВыровняй литерал статика под инициализатор конфига (это то, что " +
            "видит Unity, когда ключа нет в ассете). Пока ассет перекрывает обе " +
            "стороны, расхождение невидимо — и ждёт того дня, когда ключ из ассета " +
            "уберут.");

        Assert.That(staleKnown, Is.Empty,
            "Расхождение исчезло — удали запись из KnownDefaultDrift:\n  " +
            string.Join("\n  ", staleKnown));
    }

    [Test]
    public void EveryBalanceConfigFieldResolvesToExactlyOneDeclaredTarget()
    {
        var configDir = Path.Combine(RepoPaths.Root, "Assets", "HexLive",
            "UnityPresentation", "Config");
        var statics = StaticDeclarations();
        var broken = new List<string>();
        var checkedFields = 0;

        foreach (var file in Directory.EnumerateFiles(configDir, "*BalanceConfig.cs")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var targets = Regex.Matches(text, @"\[MirrorTarget\(typeof\((\w+)\)\)\]")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match field in ConfigField.Matches(text))
            {
                var previousFieldEnd = text.LastIndexOf(';', field.Index);
                var attributesStart = previousFieldEnd < 0 ? 0 : previousFieldEnd + 1;
                var attributes = text.Substring(attributesStart, field.Index - attributesStart);
                if (attributes.Contains("[MirrorIgnore]", StringComparison.Ordinal))
                {
                    continue;
                }

                var fieldName = field.Groups[1].Value;
                var explicitMap = Regex.Match(attributes,
                    @"\[MirrorField\(typeof\((\w+)\),\s*""(\w+)""\)\]");
                var staticName = explicitMap.Success
                    ? explicitMap.Groups[2].Value
                    : char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
                var allowedTargets = explicitMap.Success
                    ? new HashSet<string>(new[] { explicitMap.Groups[1].Value }, StringComparer.Ordinal)
                    : targets;

                var matches = statics.TryGetValue(staticName, out var declarations)
                    ? declarations.Where(allowedTargets.Contains).ToList()
                    : new List<string>();
                checkedFields++;
                if (matches.Count != 1)
                {
                    broken.Add($"{Path.GetFileName(file)}.{fieldName} -> " +
                               $"{string.Join("/", allowedTargets)}.{staticName}: " +
                               $"найдено объявлений {matches.Count}");
                }
            }
        }

        Assert.That(checkedFields, Is.GreaterThan(50),
            "Проверено подозрительно мало полей balance-конфигов.");
        Assert.That(broken, Is.Empty,
            "SimConfigMirror упадёт при загрузке этих полей:\n  " +
            string.Join("\n  ", broken));
    }

    [Test]
    public void EveryBalanceKnobHasAReader()
    {
        var statics = StaticInitializers();
        var sources = SourceScan.SimulationFiles()
            .Where(f => !SourceScan.Relative(f).Contains("/Balance/", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var dead = new List<string>();
        var staleAllowed = new HashSet<string>(KnownDead.Keys, StringComparer.Ordinal);

        foreach (var (name, declarations) in statics)
        {
            foreach (var declared in declarations)
            {
                var key = declared.Owner + "." + name;
                if (sources.Any(s => s.Contains(key, StringComparison.Ordinal)))
                {
                    staleAllowed.Remove(key);
                    continue;
                }

                if (KnownDead.ContainsKey(key))
                {
                    staleAllowed.Remove(key);
                    continue;
                }

                dead.Add(key);
            }
        }

        Assert.That(dead, Is.Empty,
            "Ручка баланса объявлена, экспортируется и крутится в инспекторе — а не " +
            "читает её никто:\n  " + string.Join("\n  ", dead) +
            "\nЛибо дай ей читателя, либо удали (и из *BalanceConfig.cs тоже, и " +
            "переснимай simdata). Ручка, которая молча ничего не делает, хуже " +
            "отсутствующей: на ней теряют время.");

        Assert.That(staleAllowed, Is.Empty,
            "Ручка ожила — удали запись из KnownDead:\n  " +
            string.Join("\n  ", staleAllowed));
    }

    private readonly struct Declared
    {
        public Declared(string owner, double value)
        {
            Owner = owner;
            Value = value;
        }

        public string Owner { get; }

        public double Value { get; }
    }

    /// <summary>
    /// Публичные статики-числа всех классов баланса симуляции: имя поля →
    /// ВСЕ объявления. Список, а не одно значение, потому что имя вроде
    /// <c>Enabled</c> живёт сразу в нескольких Spec-классах, а конфиг знает
    /// только camelCase — по одному имени класс не восстановить.
    /// </summary>
    private static Dictionary<string, List<Declared>> StaticInitializers()
    {
        var found = new Dictionary<string, List<Declared>>(StringComparer.Ordinal);

        foreach (var file in SourceScan.SimulationFiles())
        {
            var relative = SourceScan.Relative(file);
            if (!relative.Contains("/Balance/", StringComparison.Ordinal) &&
                !relative.EndsWith("SimBalance.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var owner = Path.GetFileNameWithoutExtension(file);
            foreach (Match m in StaticField.Matches(File.ReadAllText(file)))
            {
                if (!TryNumber(m.Groups[2].Value, out var value))
                {
                    continue;
                }

                if (!found.TryGetValue(m.Groups[1].Value, out var list))
                {
                    found[m.Groups[1].Value] = list = new List<Declared>();
                }

                list.Add(new Declared(owner, value));
            }
        }

        return found;
    }

    private static Dictionary<string, List<string>> StaticDeclarations()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in SourceScan.SimulationFiles())
        {
            var relative = SourceScan.Relative(file);
            if (!relative.Contains("/Balance/", StringComparison.Ordinal) &&
                !relative.EndsWith("SimBalance.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var owner = Path.GetFileNameWithoutExtension(file);
            foreach (Match match in StaticDeclaration.Matches(File.ReadAllText(file)))
            {
                if (!found.TryGetValue(match.Groups[1].Value, out var owners))
                {
                    found[match.Groups[1].Value] = owners = new List<string>();
                }

                owners.Add(owner);
            }
        }

        return found;
    }

    private static bool TryNumber(string raw, out double value)
    {
        var s = raw.Trim().TrimEnd('f', 'F');
        if (s == "true")
        {
            value = 1;
            return true;
        }

        if (s == "false")
        {
            value = 0;
            return true;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

}
