using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// «Что такое симуляция» — список систем и их порядок.
/// <para>
/// Здесь комментарий наконец становится проверкой. <c>SharkSystem</c> реализован,
/// его состояние сериализуется — и он не зарегистрирован, то есть не шагает
/// никогда. Это записано в <see cref="SimulationSystemRegistry"/> прозой; проза
/// не падает, когда следующая система тихо повторит его судьбу.
/// </para>
/// </summary>
public sealed class SystemRegistryGateTests
{
    /// <summary>
    /// Реализовано, но НЕ шагает — осознанно, с причиной. Добавление строки сюда
    /// должно быть заметным решением, а не следствием забывчивости.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnregistered =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SharkSystem"] =
                "Реализован и world.Sharks сохраняется, но не шагал ни в одном " +
                "прогоне. Включение изменит живое поведение — отдельной задачей, " +
                "не побочным эффектом рефакторинга (SimulationSystemRegistry.cs).",
        };

    /// <summary>
    /// Порядок регистрации = порядок исполнения внутри слоя, и он несущий:
    /// RaidSystem обязан идти ПОСЛЕ MobSystem (тот каждый средний проход чистит
    /// IsFighting), HumanCombatSystem — ПЕРЕД AnimalCombatSystem (у тела один
    /// слот замаха). Перестановка обязана быть осознанной правкой этого списка.
    /// </summary>
    private static readonly string[] ExpectedOrder =
    {
        "PathfindingSystem",
        "MovementSystem",
        "ExecutionSystem",
        "PerceptionSystem",
        "DecisionSystem",
        "PlanningSystem",
        "MobSystem",
        "RaidWaveSystem",
        "CampExpulsionSystem",
        "RaidSystem",
        // §108: между налётом и ударами — по той же причине, по какой налёт
        // идёт после MobSystem: сцепку боя ставит последний, кто её трогает.
        "GroupHuntSystem",
        "HumanCombatSystem",
        "AnimalCombatSystem",
        "PredationSystem",
        "ThreatAlertSystem",
        "RabbitSystem",
        "WeatherSystem",
        "EnvironmentSystem",
        "NeedsDecaySystem",
        "TemperatureSystem",
        "MoistureSystem",
        "FruitProductionSystem",
        "FireSystem",
        "CorpseSystem",
        "MeatSpoilageSystem",
        "DreamSystem",
        "BedSiteSystem",
        "WaterCollectorSystem",
        "HazardSystem",
        "StuckDiagnosticSystem",
    };

    [Test]
    public void EveryImplementedSystemIsRegisteredOrKnowinglyNot()
    {
        var registered = RegisteredTypeNames();

        var implemented = typeof(SimulationSystemRegistry).Assembly
            .GetTypes()
            .Where(t => typeof(ISimulationSystem).IsAssignableFrom(t))
            .Where(t => t.IsClass && !t.IsAbstract)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.That(implemented, Is.Not.Empty, "Рефлексия не нашла ни одной системы.");

        var missing = implemented
            .Where(n => !registered.Contains(n) && !KnownUnregistered.ContainsKey(n))
            .ToList();

        Assert.That(missing, Is.Empty,
            "Система реализована, но не зарегистрирована — значит не шагает НИКОГДА. " +
            "Либо добавь её в SimulationSystemRegistry.RegisterDefaults, либо внеси в " +
            "KnownUnregistered С ПРИЧИНОЙ:\n  " + string.Join("\n  ", missing));

        var staleExceptions = KnownUnregistered.Keys
            .Where(n => registered.Contains(n) || !implemented.Contains(n))
            .ToList();

        Assert.That(staleExceptions, Is.Empty,
            "Исключение в KnownUnregistered протухло: система либо уже " +
            "зарегистрирована, либо удалена. Убери строку:\n  " +
            string.Join("\n  ", staleExceptions));
    }

    [Test]
    public void RegistrationOrderIsPinned()
    {
        var actual = RegisteredTypeNames().ToList();

        Assert.That(actual, Is.EqualTo(ExpectedOrder),
            "Порядок регистрации — это порядок исполнения внутри слоя, и он несущий " +
            "(см. комментарии §72 в SimulationSystemRegistry). Если перестановка " +
            "намеренная — обнови ExpectedOrder и опиши, почему новый порядок верен.");
    }

    private static List<string> RegisteredTypeNames() =>
        TestWorld.CreateEngine().Systems.Select(s => s.GetType().Name).ToList();
}

}
