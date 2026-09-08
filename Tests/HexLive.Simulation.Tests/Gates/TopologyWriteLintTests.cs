using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §158.2: проходимость узла пишется ТОЛЬКО через <c>WorldTopology</c>.
/// Прямое <c>junction.Blocked = …</c> / <c>junction.Door = …</c> /
/// <c>TopologyVersion++</c> в симуляции не попадает в журнал топологии, и
/// инкрементальная связность молча остаётся прошлой — ровно тот класс
/// ошибок, который не поймать ни ревью, ни игрой (мир просто чуть врёт про
/// достижимость). Гейт ловит его компилятором тестов.
/// <para>
/// Исключения: worldgen (<c>WorldStateFactory</c> пишет флаги до появления
/// любого потребителя), загрузка сейва (флаги переписываются пачкой и
/// следом зовётся <c>InvalidateAll</c>) и записи снапшотов/провода, где
/// <c>Blocked</c> — поле записи, а не узла.
/// </para>
/// </summary>
public sealed class TopologyWriteLintTests
{
    private static readonly string[] AllowedFiles =
    {
        "Core/WorldTopology.cs",
        "Bootstrap/WorldStateFactory.cs",
        "Persistence/WorldSaveSerializer.cs",
        "Wire/WorldSnapshotCodec.cs",
        "Debug/WorldSnapshotExporter.cs",
    };

    private static readonly Regex RawWrite = new(
        @"\.(Blocked|Door)\s*=(?![=>])|(?<![A-Za-z0-9_])TopologyVersion\s*(\+\+|\+=|=(?!=))",
        RegexOptions.Compiled);

    [Test]
    public void EveryTopologyWriteGoesThroughTheJournal()
    {
        var root = Path.Combine(RepoPaths.Root, "Assets", "HexLive", "Simulation");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (AllowedFiles.Contains(relative))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var code = line.Trim();
                if (code.StartsWith("//", StringComparison.Ordinal) || !RawWrite.IsMatch(line))
                {
                    continue;
                }

                // Свойство записи снапшота/сериализатора, а не узла: ловим
                // только записи в Junction. Сам сеттер Junction.Blocked живёт в
                // SpatialModel и обязан остаться (через него идёт WorldTopology).
                if (relative == "Spatial/SpatialModel.cs" && code.StartsWith("_blocked", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{relative}:{i + 1}: {code}");
            }
        }

        Assert.That(offenders, Is.Empty,
            "Прямая запись топологии мимо журнала (§158.2). Замени на " +
            "WorldTopology.SetBlocked / SetDoor / InvalidateAll:\n  " +
            string.Join("\n  ", offenders));
    }
}

}
