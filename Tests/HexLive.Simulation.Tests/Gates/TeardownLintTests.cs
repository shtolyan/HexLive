using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Храповик на самописный демонтаж состояния.
/// <para>
/// <c>PlanInterruption.Abort</c> — канонический разбор: он освобождает клеймы,
/// занятость объекта, раскладку крафта, приглашение к разговору, брони ВСЕХ
/// оставшихся шагов и кладёт на землю вещь, которую NPC несла. Прямое
/// присваивание <c>Execution.Status = None</c> не делает ничего из этого.
/// </para>
/// <para>
/// Существующие места — НЕ список багов, и это важно сказать честно: почти все
/// они завершают работу успешно (<c>Plan.Status = Completed</c>), а Abort ставит
/// <c>Invalid</c> и эмитит <c>GoalInterrupted</c>. Подменять их Abort'ом было бы
/// ошибкой. Задача храповика скромнее: чтобы автор ДЕВЯТНАДЦАТОГО места
/// остановился и спросил себя, не должен ли этот путь идти через
/// <c>PlanInterruption</c> — потому что забытое освобождение не падает, а
/// протекает.
/// </para>
/// </summary>
public sealed class TeardownLintTests
{
    private static string AllowlistPath =>
        Path.Combine(RepoPaths.Root, "Tests", "HexLive.Simulation.Tests", "known_teardown_sites.txt");

    private const string Marker = "Execution.Status = ExecutionStatus.None";

    [Test]
    public void NoNewHandRolledTeardown()
    {
        var current = CurrentSites();

        if (Environment.GetEnvironmentVariable("HEXLIVE_UPDATE_ALLOWLIST") == "1")
        {
            WriteAllowlist(current.Keys);
            Assert.Inconclusive("Храповик перезаписан по HEXLIVE_UPDATE_ALLOWLIST=1.");
            return;
        }

        var allowed = ReadAllowlist();

        var added = current.Keys
            .Where(key => !allowed.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.That(added, Is.Empty,
            "Новый самописный сброс выполнения. Проверь, не должен ли этот путь " +
            "идти через PlanInterruption.Abort — он освобождает клеймы, брони " +
            "оставшихся шагов, раскладку крафта и несомую вещь, а прямое " +
            "присваивание не освобождает ничего. Если путь и правда завершает " +
            "работу успешно (Plan.Status = Completed), впиши строку в " +
            "known_teardown_sites.txt:\n  " + string.Join("\n  ", added));
    }

    [Test]
    public void ReportAllowlistEntriesThatAreGone()
    {
        var allowed = ReadAllowlist();
        var current = CurrentSites();

        var gone = allowed.Where(key => !current.ContainsKey(key)).OrderBy(k => k).ToList();

        TestContext.Out.WriteLine(gone.Count == 0
            ? "Самописных сбросов: " + allowed.Count + " (все на месте)."
            : "Ушли — вычеркни из known_teardown_sites.txt:\n  " + string.Join("\n  ", gone));

        Assert.Pass();
    }

    /// <summary>Ключ «файл\tN-е вхождение» — номер строки не годится, он ползёт
    /// от любой правки выше по файлу.</summary>
    private static Dictionary<string, int> CurrentSites()
    {
        var sites = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in SourceScan.SimulationFiles())
        {
            var relative = SourceScan.Relative(file);
            if (relative.EndsWith("PlanInterruption.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var seen = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(Marker, StringComparison.Ordinal) ||
                    lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                seen++;
                sites[relative + "\t#" + seen] = i + 1;
            }
        }

        return sites;
    }

    private static void WriteAllowlist(IEnumerable<string> keys)
    {
        var header = new[]
        {
            "# Места, где выполнение сбрасывается вручную, мимо PlanInterruption.Abort.",
            "#",
            "# Это НЕ список багов: почти все они завершают работу успешно, а Abort",
            "# ставит Plan.Status = Invalid и эмитит GoalInterrupted — подменять их",
            "# было бы ошибкой. Список нужен, чтобы НОВОЕ такое место было заметно:",
            "# забытое освобождение не падает, оно протекает.",
            "#",
            "# Ключ: <файл>\\t#<номер вхождения в файле>. Номер строки не годится —",
            "# он ползёт от любой правки выше.",
            "#",
            "# Перегенерация: HEXLIVE_UPDATE_ALLOWLIST=1 dotnet test --filter Teardown",
            "",
        };

        File.WriteAllLines(AllowlistPath,
            header.Concat(keys.OrderBy(k => k, StringComparer.Ordinal)));
    }

    private static HashSet<string> ReadAllowlist()
    {
        Assert.That(File.Exists(AllowlistPath), Is.True,
            "Нет файла храповика " + AllowlistPath);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(AllowlistPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                allowed.Add(trimmed);
            }
        }

        return allowed;
    }
}

}
