using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// Каждая ручка баланса обязана быть в экспорте <c>SimData/simdata.json</c>.
/// <para>
/// Ловушка, стоившая нескольких кругов на §102: <c>ApplyBalance</c> МОЛЧА
/// пропускает ключи, которых не знает, а экспорт идёт по
/// <see cref="BalanceReflection.BalanceClasses"/>. Класс, не внесённый в этот
/// список, выглядит рабочим — крутилка в инспекторе двигается, поле есть, — но
/// не экспортируется и не применяется: headless-прогон и сервер тихо живут на
/// код-дефолтах. Ровно об этом предупреждает комментарий у <c>Spec81</c>.
/// </para>
/// <para>
/// Второй смысл теста — Фаза 3 (вынос магических чисел в ручки): вынес поле и
/// забыл переэкспорт → красный тест, а не молчаливый дрейф.
/// </para>
/// </summary>
public sealed class BalanceParityGateTests
{
    [Test]
    public void EveryTunableFieldIsPresentInSimData()
    {
        var exported = ExportedBalanceKeys();
        Assert.That(exported, Is.Not.Empty, "В simdata.json нет секции balance.");

        var missing = BalanceReflection.EnumerateFields()
            .Select(pair => pair.Key)
            .Where(key => !exported.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.That(missing, Is.Empty,
            "Ручка есть в коде, но её нет в SimData/simdata.json — headless-прогон и " +
            "сервер будут молча использовать код-дефолт. Переснять экспорт: Unity, " +
            "меню HexLive ▸ Export Sim Data (JSON):\n  " + string.Join("\n  ", missing));
    }

    [Test]
    public void SimDataHasNoKeysTheCodeNoLongerKnows()
    {
        var known = BalanceReflection.EnumerateFields()
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var stale = ExportedBalanceKeys()
            .Where(key => !known.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.That(stale, Is.Empty,
            "В экспорте есть ключи, которых в коде уже нет — ApplyBalance пропускает " +
            "их молча, так что экспорт просто устарел. Переснять: HexLive ▸ Export " +
            "Sim Data (JSON):\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// Читаем ключи руками, а не через MiniJson: тест обязан ловить расхождение
    /// между кодом и ФАЙЛОМ, а разбор файла тем же кодом закрывал бы глаза на
    /// целый класс расхождений.
    /// </summary>
    private static HashSet<string> ExportedBalanceKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var text = File.ReadAllText(RepoPaths.SimData);

        var section = text.IndexOf("\"balance\"", StringComparison.Ordinal);
        if (section < 0)
        {
            return keys;
        }

        var open = text.IndexOf('{', section);
        if (open < 0)
        {
            return keys;
        }

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                continue;
            }

            if (c != '"' || depth != 1)
            {
                continue;
            }

            var end = text.IndexOf('"', i + 1);
            if (end < 0)
            {
                break;
            }

            var candidate = text.Substring(i + 1, end - i - 1);
            i = end;

            // Ключ — это то, за чем идёт двоеточие; значения-строки пропускаем.
            var after = end + 1;
            while (after < text.Length && char.IsWhiteSpace(text[after]))
            {
                after++;
            }

            if (after < text.Length && text[after] == ':')
            {
                keys.Add(candidate);
            }
        }

        return keys;
    }
}

}
