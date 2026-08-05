using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Храповик на захардкоженные id контента в системах.
/// <para>
/// Сегодня их 215 строк на 40 разных id, и это не опечатки, а правила контента,
/// живущие в коде: <c>needsHammer = BuildProduct is not ("campfire.spot" or
/// "bed.leaf" or …)</c>. Убирать их — Фаза 3; задача ЭТОГО теста скромнее и
/// важнее: не дать появиться новым, пока идёт уборка.
/// </para>
/// <para>
/// Падает на ДОБАВЛЕНИЕ, ругается (не падая) на удаление — чтобы уборка не
/// требовала синхронной правки списка в том же коммите, но и не забывалась.
/// Ключ — пара (файл, id), а не строка: сдвиг номеров строк не должен красить
/// тест.
/// </para>
/// </summary>
public sealed class HardcodedIdLintTests
{
    private static string AllowlistPath =>
        Path.Combine(RepoPaths.Root, "Tests", "HexLive.Simulation.Tests", "known_hardcoded_ids.txt");

    [Test]
    public void NoNewHardcodedContentIds()
    {
        if (Environment.GetEnvironmentVariable("HEXLIVE_UPDATE_ALLOWLIST") == "1")
        {
            WriteAllowlist(CurrentKeys().Keys);
            Assert.Inconclusive(
                "Храповик перезаписан по HEXLIVE_UPDATE_ALLOWLIST=1. Прочитай diff " +
                "файла глазами: он фиксирует ДОЛГ, а не разрешение.");
            return;
        }

        var allowed = ReadAllowlist();
        var current = CurrentKeys();

        var added = current.Keys
            .Where(key => !allowed.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.That(added, Is.Empty,
            "Новый захардкоженный id контента в системе. Так правила контента " +
            "переезжают в код и расходятся с каталогом. Варианты: тег на " +
            "ObjectDefinition (если веток по id две и больше — это категория), либо " +
            "константа в Content/ItemIds.cs / ObjectIds.cs. Если добавление " +
            "осознанное и временное — впиши строку в known_hardcoded_ids.txt:\n  " +
            string.Join("\n  ", added.Select(k => k + "   (" + current[k] + ")")));
    }

    [Test]
    public void ReportAllowlistEntriesThatAreGone()
    {
        var allowed = ReadAllowlist();
        var current = CurrentKeys();

        var gone = allowed
            .Where(key => !current.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        if (gone.Count == 0)
        {
            TestContext.Out.WriteLine("Храповик в тонусе: лишних строк нет, осталось " +
                                      allowed.Count + " (см. Фазу 3).");
            Assert.Pass();
            return;
        }

        TestContext.Out.WriteLine(
            "Эти id из системы ушли — вычеркни их из known_hardcoded_ids.txt, чтобы " +
            "храповик затянулся (" + gone.Count + " из " + allowed.Count + "):\n  " +
            string.Join("\n  ", gone));
        Assert.Pass();
    }

    /// <summary>Ключ «файл\tid» → пример строки, где он встретился.</summary>
    private static Dictionary<string, int> CurrentKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var hit in SourceScan.ContentIdLiterals())
        {
            var key = hit.RelativePath + "\t" + hit.Value;
            if (!keys.ContainsKey(key))
            {
                keys[key] = hit.Line;
            }
        }

        return keys;
    }

    private static void WriteAllowlist(IEnumerable<string> keys)
    {
        var header = new[]
        {
            "# Захардкоженные id контента в Runtime/Systems — СПИСОК ДОЛГА, не разрешения.",
            "#",
            "# Формат: <путь от корня репо>\\t<id>. Ключ без номера строки: сдвиг строк",
            "# не должен красить тест.",
            "#",
            "# Тест падает на ДОБАВЛЕНИЕ новой строки и печатает (не падая) те, что ушли.",
            "# Фаза 3 роадмапа ведёт этот файл к нулю: ветка по двум и более id — это",
            "# категория, ей место тегом на ObjectDefinition; одиночная опознавалка —",
            "# константа в Content/ItemIds.cs / ObjectIds.cs.",
            "#",
            "# Перегенерация: HEXLIVE_UPDATE_ALLOWLIST=1 dotnet test --filter HardcodedId",
            "",
        };

        File.WriteAllLines(AllowlistPath,
            header.Concat(keys.OrderBy(k => k, StringComparer.Ordinal)));
    }

    private static HashSet<string> ReadAllowlist()
    {
        Assert.That(File.Exists(AllowlistPath), Is.True,
            "Нет файла храповика " + AllowlistPath + " — без него тест покрасил бы " +
            "все 215 существующих строк.");

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(AllowlistPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            allowed.Add(trimmed);
        }

        return allowed;
    }
}

}
