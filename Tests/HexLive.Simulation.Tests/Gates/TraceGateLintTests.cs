using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ §30.17: ДИАГНОСТИКА МОЛЧИТ ПО УМОЛЧАНИЮ — и это держится не дисциплиной.
/// <para>
/// Ловушка, ради которой существует этот линт: аргумент вычисляется ДО вызова,
/// поэтому проверка ВНУТРИ <c>Trace.Debug</c> не спасает — интерполяция и
/// <c>string.Join</c> уже отработали, и гейт экономит только объект события
/// (примерно седьмую часть цены). Спасает ровно одно: <c>if (SimTrace.…)</c>
/// НА МЕСТЕ ВЫЗОВА. Забыть его нельзя увидеть глазами в диффе на 400 точек,
/// поэтому проверяет машина.
/// </para>
/// <para>
/// Второе правило: диагностический тип обязан идти через <c>Trace.Debug</c>, а
/// не <c>Trace.Emit</c>. Иначе он снова начнёт писаться в обычной игре — молча,
/// потому что тесты этого не видят.
/// </para>
/// </summary>
public sealed class TraceGateLintTests
{
    // Хроника: пишется всегда. Белый список читается из самого GameEventTypes,
    // чтобы список жил в ОДНОМ месте; сюда добавлены адресные факты и причины,
    // которые не в списке, но обязаны звучать всегда.
    private static readonly string[] DeathCauses =
    {
        "BledOut", "DogFight", "Drowned", "Heatstroke", "Hypothermia", "LimbSevered",
        "PreyFoughtBack", "Preyed", "RaidFoughtBack", "RaidStruck",
        "StarvedToDeath", "Sunburn", "VitalPartDestroyed",
    };

    private static readonly string[] OrderReplies =
    {
        "ManualOrderRejected", "GroupOrderResult",
        // §121.7: возврат под ИИ по таймауту обязан звучать всегда — молчаливое
        // «она вдруг зажила своей жизнью» игрок читает как поломку.
        "ManualControlExpired",
        // §121.5: снос ПРИНЯТОГО приказа (бой, провал пути) — тоже ответ игроку.
        "ManualOrderInterrupted",
        // §160: результат для контроллера сохраняется после очистки статуса плана.
        "ManualOrderFinished",
        // §153.4: addressed consent/transfer result, visible to the requester
        // through MCP even when diagnostic tracing is disabled.
        "ItemRequestResult",
        // §160.6: personal observations wake the witnessing controller even
        // with diagnostics off; they never join the public GameEventTypes feed.
        "AgentObservedIntruder", "AgentObservedTheft", "AgentObservedLoot", "AgentObservedDeath",
    };

    [Test]
    public void EveryDiagnosticEmitIsGatedAtTheCallSite()
    {
        var ungated = new List<string>();

        foreach (var file in SourceScan.SimulationFiles())
        {
            var relative = SourceScan.Relative(file);
            if (relative.EndsWith("Diagnostics/SimTrace.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("Trace.Debug"))
                {
                    continue;
                }

                // Гейт стоит либо на этой же строке, либо выше по блоку: между
                // ним и вызовом обычно есть открывающая скобка, комментарий, а
                // иногда и цикл (гейт снаружи цикла — это и есть правильная
                // форма: не платить даже за обход).
                var gated = false;
                for (var back = 0; back <= 10 && i - back >= 0; back++)
                {
                    var probe = lines[i - back];
                    if (probe.Contains("SimTrace.Enabled") ||
                        probe.Contains("SimTrace.Perception") ||
                        probe.Contains("SimTrace.Scores"))
                    {
                        gated = true;
                        break;
                    }
                }

                if (!gated)
                {
                    ungated.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.That(ungated, Is.Empty,
            "Диагностическая трасса без гейта на месте вызова — строка соберётся " +
            "даже при выключенной трассе:\n  " + string.Join("\n  ", ungated) +
            "\nОберни в if (SimTrace.Enabled) { … } (или в подканал).");
    }

    [Test]
    public void ChronicleGoesThroughEmit_DiagnosticsThroughDebug()
    {
        var whitelist = Whitelist();
        var always = new HashSet<string>(whitelist, StringComparer.Ordinal);
        foreach (var name in DeathCauses.Concat(OrderReplies))
        {
            always.Add(name);
        }

        var wrongTier = new List<string>();

        // Один вызов может нести ДВА литерала — тернарник
        // `npc.Health <= 0f ? "StarvedToDeath" : "StarvationDamage"`. Если хоть
        // один из них хроника, весь вызов обязан остаться Trace.Emit: молчание
        // ради второго имени стоило бы причины смерти.
        var chronicleCalls = new HashSet<string>(
            SourceScan.EmittedEventTypes()
                .Where(h => always.Contains(h.Value))
                .Select(h => h.RelativePath + ":" + h.Line),
            StringComparer.Ordinal);

        foreach (var hit in SourceScan.EmittedEventTypes())
        {
            if (chronicleCalls.Contains(hit.RelativePath + ":" + hit.Line))
            {
                continue;
            }

            var text = File.ReadAllText(Path.Combine(RepoPaths.Root, hit.RelativePath));
            var lines = text.Split('\n');
            if (hit.Line - 1 >= lines.Length)
            {
                continue;
            }

            // Ищем, каким методом эмитится ЭТОТ литерал: смотрим строку хита и
            // три выше (вызов часто перенесён).
            var method = string.Empty;
            for (var back = 0; back <= 3 && hit.Line - 1 - back >= 0; back++)
            {
                var probe = lines[hit.Line - 1 - back];
                if (probe.Contains("Trace.Debug"))
                {
                    method = "Debug";
                    break;
                }

                if (probe.Contains("Trace.Emit"))
                {
                    method = "Emit";
                    break;
                }
            }

            if (method == "Emit" && !always.Contains(hit.Value))
            {
                wrongTier.Add(
                    $"{hit.RelativePath}:{hit.Line}  \"{hit.Value}\" — диагностика через Trace.Emit");
            }
        }

        Assert.That(wrongTier, Is.Empty,
            "Событие не из хроники обязано идти через Trace.Debug под гейтом, " +
            "иначе оно снова зазвучит в обычной игре:\n  " +
            string.Join("\n  ", wrongTier));
    }

    private static IEnumerable<string> Whitelist()
    {
        var path = Path.Combine(RepoPaths.Root, "Assets", "HexLive", "Simulation",
            "Runtime", "GameEventTypes.cs");
        var text = File.ReadAllText(path);
        return Regex.Matches(text, "\"([A-Za-z][A-Za-z0-9_]*)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct();
    }
}

}
