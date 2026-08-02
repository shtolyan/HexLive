using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ ТАЙМЛАЙН ЗАМАХА ЖИВЁТ В ОДНОМ МЕСТЕ.
///
/// <para>
/// Доминирующий класс багов в этой кодовой базе — не сложность и не
/// архитектура, а КОПИЯ, У КОТОРОЙ ОТСТАЛА ОДНА СТРОКА. Она компилируется, она
/// проходит все тесты, и находят её только глазами, через несколько кругов
/// отладки. Список цены:
/// </para>
/// <list type="bullet">
/// <item>§103: <c>MeleeSwing</c> — копия собачьего замаха, потерявшая
/// <c>SwingStartTick</c>. Удары человека против человека не рисовались
/// НИКОГДА; четыре круга отладки.</item>
/// <item>§102: две мерки дистанции, разошедшиеся на 2872 тика молчания.</item>
/// <item>Фаза 2: фильтр кокоса, внесённый в одну копию из пяти — стоил жизни
/// колонистке.</item>
/// </list>
/// <para>
/// §104 r2 свёл таймлайн в единственный <c>MeleeSwing.TryAdvanceSwing</c>, а
/// тайминги — в <c>GearStats.StrikeTimings</c>. Этот линт следит, чтобы копия
/// не завелась снова: ни расчётом таймингов на стороне, ни своим выбором
/// варианта удара, ни ручной раздачей окна анимации.
/// </para>
/// </summary>
public sealed class SwingTimelineGateTests
{
    /// <summary>
    /// Кто вправе трогать поля слота замаха напрямую, и почему:
    /// <list type="bullet">
    /// <item><c>MeleeSwing</c> — сам таймлайн.</item>
    /// <item><c>FightScene</c> — владелец постановочной сцены: разводит удары и
    /// разбирает слот в конце.</item>
    /// <item><c>RaidSystem</c> — расцепление пары (Unpair/BreakOff).</item>
    /// </list>
    /// Всем остальным полагается звать <c>TryAdvanceSwing</c>.
    /// </summary>
    private static readonly HashSet<string> MaySetSwingSlot =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Runtime/Helpers/MeleeSwing.cs",
            "AI/FightScene.cs",
            "Runtime/Systems/Wildlife/RaidSystem.cs",
        };

    /// <summary>
    /// ПЕРЕВОЗЧИКИ: копируют поле из состояния в снапшот, на провод и обратно.
    /// Они не открывают замах и не решают о нём ничего — им положено касаться
    /// каждого поля, это их работа. Полнота их покрытия сторожится отдельно
    /// (WireCoverageGate, SnapshotContractGate).
    /// </summary>
    private static readonly string[] Transport =
    {
        "Debug/WorldSnapshotExporter.cs",
        "Wire/WorldSnapshotCodec.cs",
        "Wire/SnapshotDelta.cs",
        "Wire/SnapshotDeltaReader.cs",
    };

    /// <summary>Поля, из которых состоит «идёт замах».</summary>
    private static readonly string[] SwingSlotFields =
    {
        "StrikeLandsAtTick",
        "AttackAnimUntilTick",
        "SwingStartTick",
        "SwingStrikeIndex",
    };

    [Test]
    public void StrikeTimingsExistInExactlyOnePlace()
    {
        var declarations = Declarations("StrikeTimings");

        Assert.That(declarations.Count, Is.EqualTo(1),
            "Тайминги замаха обязаны считаться в ОДНОМ месте " +
            "(GearStats.StrikeTimings). Найдено объявлений: " + declarations.Count +
            "\n  " + string.Join("\n  ", declarations) +
            "\nЕсли нужен свой расчёт — значит нужен новый ПАРАМЕТР у общего, а " +
            "не вторая копия: разошедшаяся копия и есть §103.");

        Assert.That(declarations[0].RelativePath, Does.EndWith("Content/ItemCatalog.cs"),
            "Расчёт таймингов ушёл из GearStats — верни его туда: это свойство " +
            "снаряжения, и спрашивает его ещё и вид.");
    }

    /// <summary>
    /// Соль 777 выбирает вариант удара. Второе место с той же солью — это вторая
    /// копия выбора, которая разойдётся с первой при первой же правке.
    /// </summary>
    [Test]
    public void StrikeVariantIsPickedInExactlyOnePlace()
    {
        var hits = LinesMatching(line => line.Contains(", 777)", StringComparison.Ordinal));

        Assert.That(hits, Is.Not.Empty,
            "Выбор варианта удара (Hash01 с солью 777) не найден вообще — либо " +
            "соль сменилась, либо линт смотрит не туда.");

        var files = hits.Select(h => h.RelativePath).Distinct().ToList();
        Assert.That(files.Count, Is.EqualTo(1),
            "Вариант удара выбирается больше чем в одном файле:\n  " +
            string.Join("\n  ", hits.Select(h => h.ToString())) +
            "\nЭто ровно та копия, что стоила §103. Зови MeleeSwing.TryAdvanceSwing.");

        Assert.That(files[0], Does.EndWith("Runtime/Helpers/MeleeSwing.cs"),
            "Выбор варианта удара переехал из MeleeSwing в " + files[0] + ".");
    }

    /// <summary>
    /// Поля слота замаха ставит таймлайн, гасит владелец сцены. Любой третий,
    /// раздающий их руками, — это заново написанный таймлайн.
    /// </summary>
    [Test]
    public void OnlySwingOwnersTouchTheSwingSlot()
    {
        var offenders = new List<string>();

        foreach (var hit in LinesMatching(line =>
                     SwingSlotFields.Any(field =>
                         line.Contains(field + " =", StringComparison.Ordinal) ||
                         line.Contains(field + " +=", StringComparison.Ordinal))))
        {
            var allowed = MaySetSwingSlot.Any(owner =>
                hit.RelativePath.EndsWith(owner, StringComparison.Ordinal));
            var carries = Transport.Any(carrier =>
                hit.RelativePath.EndsWith(carrier, StringComparison.Ordinal));

            if (!allowed && !carries)
            {
                offenders.Add(hit.ToString());
            }
        }

        Assert.That(offenders, Is.Empty,
            "Поля слота замаха раздаются мимо MeleeSwing/FightScene:\n  " +
            string.Join("\n  ", offenders) +
            "\nЕсли системе нужен видимый удар — зови MeleeSwing.TryAdvanceSwing " +
            "и примени урон у себя. Собственная раздача этих полей и есть вторая " +
            "копия таймлайна.");
    }

    private static List<SourceHit> Declarations(string methodName)
    {
        // Объявление, а не вызов: за именем идёт '(' и строка начинается с
        // модификатора видимости.
        return LinesMatching(line =>
        {
            var trimmed = line.TrimStart();
            var isDeclaration =
                trimmed.StartsWith("public ", StringComparison.Ordinal) ||
                trimmed.StartsWith("internal ", StringComparison.Ordinal) ||
                trimmed.StartsWith("private ", StringComparison.Ordinal) ||
                trimmed.StartsWith("protected ", StringComparison.Ordinal);
            return isDeclaration && trimmed.Contains(" " + methodName + "(", StringComparison.Ordinal);
        });
    }

    /// <summary>Строки исходников симуляции, подходящие под условие; комментарии мимо.</summary>
    private static List<SourceHit> LinesMatching(Func<string, bool> predicate)
    {
        var hits = new List<SourceHit>();

        foreach (var file in SourceScan.SimulationFiles())
        {
            var relative = SourceScan.Relative(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                if (code.Length > 0 && predicate(code))
                {
                    hits.Add(new SourceHit
                    {
                        RelativePath = relative,
                        Line = i + 1,
                        Value = lines[i].Trim()
                    });
                }
            }
        }

        return hits;
    }

    private static string StripComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line.Substring(0, at);
    }
}

}
