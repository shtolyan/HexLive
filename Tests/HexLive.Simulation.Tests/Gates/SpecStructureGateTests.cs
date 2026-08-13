using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Структура спеки: один раздел — один файл <c>Spec/&lt;N&gt;.md</c>.
/// <para>
/// Гейт существует из-за конкретной аварии: в старом одноблочном spec.md (22 827
/// строк) §84 завёлся ДВАЖДЫ — «Юка рубится как дерево» и «Одежда знает, на кого
/// сшита», обе помечены iteration 84. Не по небрежности: чтобы узнать свободный
/// номер, надо было просмотреть двадцать две тысячи строк, и никто их не
/// просматривал. Номер §N при этом — публичный API спеки: на него 3773 ссылки
/// из C#, и перенумерация исключена, поэтому дубль лечится только тем, что его
/// НЕ ДАЮТ создать.
/// </para>
/// <para>
/// Проверяется ровно то, что ломается молча: файл, потерявший свой заголовок;
/// раздел, отсутствующий в оглавлении (ссылка из кода ведёт в пустоту, а
/// оглавление говорит «такого раздела нет»); ссылка §N на несуществующий
/// раздел. Размеры разделов гейт НЕ трогает намеренно — иначе он краснел бы от
/// каждой правки абзаца, а гейт, краснеющий на нормальной работе, обходят.
/// </para>
/// </summary>
public sealed class SpecStructureGateTests
{
    private static string SpecDir => Path.Combine(RepoPaths.Root, "Spec");
    private static string IndexPath => Path.Combine(RepoPaths.Root, "spec.md");

    private static string AllowlistPath => Path.Combine(
        RepoPaths.Root, "Tests", "HexLive.Simulation.Tests", "known_missing_spec_sections.txt");

    /// <summary>`## §135 Заголовок`, `## 21. Заголовок`, `## §93-94 Заголовок`.</summary>
    private static readonly Regex Heading =
        new Regex(@"^## (?:§)?(\d+[A-Z]?(?:-\d+)?)[.\s]+(.*)$", RegexOptions.Compiled);

    /// <summary>Ссылка вида §105 или §54 (из §54.14 берётся верхний уровень).</summary>
    private static readonly Regex Reference = new Regex(@"§(\d+[A-Z]?)", RegexOptions.Compiled);

    /// <summary>Имя файла раздела: 135, 29A, 75A.</summary>
    private static readonly Regex SectionFileName = new Regex(@"^\d+[A-Z]?$", RegexOptions.Compiled);

    private static List<string> Order()
    {
        return File.ReadAllLines(Path.Combine(SpecDir, "ORDER.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Все Spec/&lt;N&gt;.md — и разделы, и указатели (§94, §101).</summary>
    private static List<string> NumberedFiles()
    {
        return Directory.EnumerateFiles(SpecDir, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => SectionFileName.IsMatch(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    [Test]
    public void OrderCoversExactlyTheSectionFiles()
    {
        var order = Order();
        Assert.That(order, Is.Unique, "ORDER.txt содержит номер дважды.");

        var onDisk = NumberedFiles().ToHashSet(StringComparer.Ordinal);
        var listed = order.ToHashSet(StringComparer.Ordinal);

        var missingFile = listed.Where(k => !onDisk.Contains(k)).OrderBy(k => k).ToList();
        Assert.That(missingFile, Is.Empty,
            "ORDER.txt называет раздел, которого нет на диске: " + string.Join(", ", missingFile) +
            ". Либо файл удалили руками, либо строку в ORDER.txt дописали без файла.");

        // Файл, которого нет в ORDER.txt, — указатель на сдвоенный заголовок
        // (§93-94, §100-101). Он обязан вести в существующий раздел, иначе
        // правило «§N -> Spec/N.md» имеет молчаливую дыру.
        foreach (var pointer in onDisk.Where(k => !listed.Contains(k)).OrderBy(k => k))
        {
            var text = File.ReadAllText(Path.Combine(SpecDir, pointer + ".md"));
            var target = Regex.Match(text, @"\((\d+[A-Z]?)\.md\)");
            Assert.That(target.Success, Is.True,
                $"Spec/{pointer}.md не в ORDER.txt и не похож на указатель. Раздел " +
                "обязан быть в ORDER.txt, указатель — содержать ссылку вида (93.md).");
            Assert.That(listed, Does.Contain(target.Groups[1].Value),
                $"Spec/{pointer}.md указывает на §{target.Groups[1].Value}, которого нет в ORDER.txt.");
        }
    }

    [Test]
    public void EverySectionFileStartsWithItsOwnHeading()
    {
        foreach (var key in Order())
        {
            var path = Path.Combine(SpecDir, key + ".md");
            var first = File.ReadLines(path).FirstOrDefault() ?? "";
            var m = Heading.Match(first);

            Assert.That(m.Success, Is.True,
                $"Spec/{key}.md начинается не с заголовка раздела, а с: {first}");

            // §93-94 лежит в 93.md: сравнивается номер ДО дефиса.
            var label = m.Groups[1].Value.Split('-')[0];
            Assert.That(label, Is.EqualTo(key),
                $"Spec/{key}.md озаглавлен как §{m.Groups[1].Value}. Имя файла — это адрес, " +
                "по которому его найдут 3773 ссылки из кода; переименовывать заголовок можно, " +
                "менять номер — нет.");
        }
    }

    [Test]
    public void NoFileHoldsTwoSections()
    {
        foreach (var key in Order())
        {
            var path = Path.Combine(SpecDir, key + ".md");
            var extra = File.ReadAllLines(path)
                .Skip(1)
                .Where(l => Regex.IsMatch(l, @"^## §\d"))
                .ToList();

            Assert.That(extra, Is.Empty,
                $"Spec/{key}.md держит второй раздел верхнего уровня:\n  " +
                string.Join("\n  ", extra) +
                "\nИменно так §84 оказался занят двумя темами. Заводи раздел через " +
                "`python3 Tools/spec_new.py \"Название\"` — он выдаст свободный номер.");
        }
    }

    [Test]
    public void IndexListsEverySectionWithItsTitle()
    {
        var index = File.ReadAllText(IndexPath);

        foreach (var key in Order())
        {
            var first = File.ReadLines(Path.Combine(SpecDir, key + ".md")).First();
            var title = Heading.Match(first).Groups[2].Value.Trim().Replace("|", "\\|");
            var row = $"| [§{Heading.Match(first).Groups[1].Value}](Spec/{key}.md) | {title} |";

            Assert.That(index, Does.Contain(row),
                $"В оглавлении нет актуальной строки для §{key}. Перегенерируй: " +
                "`python3 Tools/spec_index.py`. Раздел, которого нет в оглавлении, " +
                "не найдёт никто: спека целиком не читается, читается только оглавление.");
        }

        foreach (Match link in Regex.Matches(index, @"\]\(Spec/([^)]+)\)"))
        {
            var target = Path.Combine(SpecDir, link.Groups[1].Value);
            Assert.That(File.Exists(target), Is.True,
                $"Оглавление ссылается на Spec/{link.Groups[1].Value}, которого нет.");
        }
    }

    [Test]
    public void EveryReferencedSectionExists()
    {
        var known = ReadAllowlist();
        var have = NumberedFiles().ToHashSet(StringComparer.Ordinal);

        var dangling = new SortedDictionary<string, string>(SectionOrder.Instance);
        foreach (var file in SourceFiles())
        {
            foreach (Match m in Reference.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (have.Contains(key) || known.Contains(key)) continue;
                if (!dangling.ContainsKey(key)) dangling[key] = SourceScan.Relative(file);
            }
        }

        Assert.That(dangling, Is.Empty,
            "Ссылка §N из кода ведёт в несуществующий раздел спеки:\n  " +
            string.Join("\n  ", dangling.Select(kv => $"§{kv.Key}   (напр. {kv.Value})")) +
            "\nЛибо опечатка в номере, либо раздел написать забыли. Если это осознанный " +
            "долг — впиши номер в known_missing_spec_sections.txt с причиной.");
    }

    /// <summary>
    /// Ругается, не падая, когда долг закрыли: строка в списке пережила свой раздел.
    /// Падать нельзя — иначе написать недостающий раздел значило бы синхронно
    /// править список в том же коммите.
    /// </summary>
    [Test]
    public void ReportAllowlistEntriesThatAreGone()
    {
        var have = NumberedFiles().ToHashSet(StringComparer.Ordinal);
        var closed = ReadAllowlist().Where(k => have.Contains(k)).OrderBy(k => k).ToList();

        if (closed.Count > 0)
        {
            Assert.Inconclusive(
                "Долг закрыт — эти разделы написаны, убери их из " +
                "known_missing_spec_sections.txt: " + string.Join(", ", closed.Select(k => "§" + k)));
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        foreach (var area in new[]
                 {
                     Path.Combine(RepoPaths.Root, "Assets", "HexLive"),
                     Path.Combine(RepoPaths.Root, "Tests"),
                     Path.Combine(RepoPaths.Root, "Server"),
                 })
        {
            if (!Directory.Exists(area)) continue;
            foreach (var file in Directory.EnumerateFiles(area, "*.cs", SearchOption.AllDirectories))
            {
                // ⭐ `.claude/worktrees/<name>/` — ВТОРОЙ чекаут этого же репозитория
                // со своей копией спеки. Без исключения гейт считал бы чужой checkout
                // и краснел бы от его состояния.
                var rel = SourceScan.Relative(file);
                if (rel.StartsWith(".claude/", StringComparison.Ordinal)) continue;
                if (rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                yield return file;
            }
        }
    }

    private static HashSet<string> ReadAllowlist()
    {
        if (!File.Exists(AllowlistPath)) return new HashSet<string>(StringComparer.Ordinal);
        return File.ReadAllLines(AllowlistPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .Select(l => l.Split(' ')[0].TrimStart('§'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>§29A после §29, §103 после §98 — сортировка по числу, потом по букве.</summary>
    private sealed class SectionOrder : IComparer<string>
    {
        public static readonly SectionOrder Instance = new SectionOrder();

        public int Compare(string a, string b)
        {
            int NumOf(string s) => int.Parse(Regex.Match(s, @"^\d+").Value);
            var byNumber = NumOf(a).CompareTo(NumOf(b));
            return byNumber != 0 ? byNumber : string.CompareOrdinal(a, b);
        }
    }
}

}
