using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// §156: у каждой системы объявлено отношение к спящим чанкам, и объявленное
/// сходится с написанным.
/// <para>
/// Дыра, ради которой гейт существует: «забыл профильтровать» выглядит как
/// работающий код. Система с политикой <c>PerChunk</c> без вопроса
/// <c>ChunkMath.IsAwake</c> продолжает обходить весь остров — то есть ровно ту
/// цену, ради снятия которой §156 и написан, — и никто этого не замечает, пока
/// мир не вырастет.
/// </para>
/// </summary>
public sealed class SystemChunkPolicyGateTests
{
    /// <summary>
    /// Прибитая карта: новая система обязана выбрать политику так же осознанно,
    /// как выбирает место в реестре. Строка здесь — решение, а не формальность.
    /// </summary>
    private static readonly Dictionary<string, ChunkPolicy> Expected =
        new Dictionary<string, ChunkPolicy>(StringComparer.Ordinal)
        {
            // Обходят объекты или тайлы мира — цена росла с площадью.
            ["BedSiteSystem"] = ChunkPolicy.PerChunk,
            ["CorpseSystem"] = ChunkPolicy.PerChunk,
            ["EnvironmentSystem"] = ChunkPolicy.PerChunk,
            ["FireSystem"] = ChunkPolicy.PerChunk,
            ["FruitProductionSystem"] = ChunkPolicy.PerChunk,
            ["MeatSpoilageSystem"] = ChunkPolicy.PerChunk,
            ["MoistureSystem"] = ChunkPolicy.PerChunk,
            ["WaterCollectorSystem"] = ChunkPolicy.PerChunk,

            // Мир целиком или чистая формула от тика.
            ["ColonyArrivalSystem"] = ChunkPolicy.Global,
            // Объекты обходит, но это колониальная ПЕРЕПИСЬ, а не часы на
            // объекте: пропущенная кровать вернула бы мечту о кровати той, у
            // кого кровать есть. См. §156.8.
            ["DreamSystem"] = ChunkPolicy.Global,
            ["JournalSystem"] = ChunkPolicy.Global,
            ["RaidWaveSystem"] = ChunkPolicy.Global,
            ["ShipwreckSurvivorSystem"] = ChunkPolicy.Global,
            ["WeatherSystem"] = ChunkPolicy.Global,

            // Работают от живых NPC — те сами будят чанки вокруг себя.
            ["AnimalCombatSystem"] = ChunkPolicy.NpcDriven,
            ["CampExpulsionSystem"] = ChunkPolicy.NpcDriven,
            ["DecisionSystem"] = ChunkPolicy.NpcDriven,
            ["ExecutionSystem"] = ChunkPolicy.NpcDriven,
            ["GroupHuntSystem"] = ChunkPolicy.NpcDriven,
            ["HazardSystem"] = ChunkPolicy.NpcDriven,
            ["HumanCombatSystem"] = ChunkPolicy.NpcDriven,
            ["LlmControlSystem"] = ChunkPolicy.NpcDriven,
            ["LoopDiagnosticSystem"] = ChunkPolicy.NpcDriven,
            ["ManualOrderSystem"] = ChunkPolicy.NpcDriven,
            ["MobSystem"] = ChunkPolicy.NpcDriven,
            ["MovementSystem"] = ChunkPolicy.NpcDriven,
            ["NeedsDecaySystem"] = ChunkPolicy.NpcDriven,
            ["PathfindingSystem"] = ChunkPolicy.NpcDriven,
            ["PerceptionSystem"] = ChunkPolicy.NpcDriven,
            ["PlanningSystem"] = ChunkPolicy.NpcDriven,
            ["PredationSystem"] = ChunkPolicy.NpcDriven,
            ["ProstheticAidSystem"] = ChunkPolicy.NpcDriven,
            ["RabbitSystem"] = ChunkPolicy.NpcDriven,
            ["RaidSystem"] = ChunkPolicy.NpcDriven,
            ["RescueSystem"] = ChunkPolicy.NpcDriven,
            ["SleepPlanConsistencySystem"] = ChunkPolicy.NpcDriven,
            ["StuckDiagnosticSystem"] = ChunkPolicy.NpcDriven,
            ["TemperatureSystem"] = ChunkPolicy.NpcDriven,
            ["ThreatAlertSystem"] = ChunkPolicy.NpcDriven,
        };

    [Test]
    public void EverySystemDeclaresThePolicyItWasGiven()
    {
        var actual = TestWorld.CreateEngine().Systems
            .ToDictionary(s => s.GetType().Name, s => s.ChunkPolicy, StringComparer.Ordinal);

        var drift = new List<string>();
        foreach (var pair in actual)
        {
            if (!Expected.TryGetValue(pair.Key, out var expected))
            {
                drift.Add($"{pair.Key}: новая система без строки в карте — " +
                          $"выберите политику осознанно (объявлена {pair.Value})");
            }
            else if (expected != pair.Value)
            {
                drift.Add($"{pair.Key}: объявлено {pair.Value}, карта ждёт {expected}");
            }
        }

        foreach (var name in Expected.Keys.Where(k => !actual.ContainsKey(k)))
        {
            drift.Add($"{name}: в карте есть, а среди зарегистрированных нет — " +
                      "протухшая строка");
        }

        Assert.That(drift, Is.Empty, string.Join("\n  ", drift));
    }

    /// <summary>
    /// ⭐ Объявление против написанного. PerChunk обязана спрашивать
    /// <c>IsAwake</c> — иначе политика есть, а фильтра нет, и система молча
    /// продолжает стоить площади острова. Обратное тоже ловим: <c>IsAwake</c> в
    /// системе, объявленной Global или NpcDriven, значит, что кто-то
    /// профильтровал не то.
    /// </summary>
    [Test]
    public void DeclaredPolicyMatchesWhatTheSourceActuallyDoes()
    {
        var byName = SourceScan.SimulationFiles()
            .ToLookup(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

        var drift = new List<string>();
        foreach (var pair in Expected)
        {
            var files = byName[pair.Key].ToList();
            if (files.Count == 0)
            {
                drift.Add($"{pair.Key}: файла системы не нашлось");
                continue;
            }

            // Два законных свидетельства §156: обход по бодрым чанкам
            // (CollectTickable — так дешевле всего) или вопрос про конкретный
            // тайл (IsAwake — когда обходят не объекты, а тайлы, как тени).
            var asks = files.Any(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("ChunkMath.IsAwake", StringComparison.Ordinal) ||
                       text.Contains("ChunkMath.CollectTickable", StringComparison.Ordinal);
            });

            if (pair.Value == ChunkPolicy.PerChunk && !asks)
            {
                drift.Add($"{pair.Key}: объявлена PerChunk, но §156 не спрашивает " +
                          "(ни CollectTickable, ни IsAwake) — фильтра нет, цена " +
                          "осталась площадью острова");
            }
            else if (pair.Value != ChunkPolicy.PerChunk && asks)
            {
                drift.Add($"{pair.Key}: объявлена {pair.Value}, но спрашивает §156 — " +
                          "либо политика неверна, либо профильтровано лишнее");
            }
        }

        Assert.That(drift, Is.Empty, string.Join("\n  ", drift));
    }
}

}
