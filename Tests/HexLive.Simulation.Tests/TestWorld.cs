using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Мир для теста — теми же тремя вызовами, что делает сервер
/// (<c>WorldHost</c>) и что предписывает CLAUDE.md. Своей сборки мира здесь
/// нет намеренно: харнесс, собирающий мир по-своему, меряет не ту игру.
/// </summary>
public static class TestWorld
{
    public static SimulationEngine CreateEngine(
        int seed = 12345, SimulationClock clock = null)
    {
        // §30.17: в тестах трасса включена всегда — половина поведенческих
        // проверок ассертит именно на события, а в игре тот же поток по
        // умолчанию молчит.
        SimTrace.EnableAll();

        var definition = PrototypeWorldDefinitionFactory.Create(seed);
        var world = new WorldStateFactory().Create(definition);

        var settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
        };

        clock ??= new SimulationClock();
        clock.Resume();

        var engine = new SimulationEngine(world, settings, clock);
        SimulationSystemRegistry.RegisterDefaults(engine);

        // Иначе объекты доедут безымянными, а рендер нарисует серые шары —
        // молчаливый отказ, поэтому строим до первого кадра (CLAUDE.md §83).
        DefinitionIdTable.Build(world.Content);

        return engine;
    }

    public static WorldState CreateWorld(int seed = 12345) => CreateEngine(seed).World;

    /// <summary>§120.3 r2: стартовый дом прототипа — конструкторное
    /// plan-здание (чертёж Hut1Hex из реестра §120.8), не FBX-кит hut_1hex.
    /// Тесты, которым нужен «дом мира», обязаны идти сюда, а не искать
    /// конкретный DefinitionId.</summary>
    public static Content.WorldObjectState StartHut(WorldState world) =>
        world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == Content.ContentIds.HutPlan &&
            string.IsNullOrEmpty(obj.BuildProduct));

    /// <summary>Легаси-кит hut_1hex, построенный вручную: путь совместимости
    /// старых сейвов (интегрированные кровати §120.2, авторский шкаф §133,
    /// ремонты якорей). Новые миры такой дом больше не рождают — тесты этих
    /// контрактов обязаны собирать его сами.</summary>
    public static Content.WorldObjectState SpawnLegacyKitHut(WorldState world)
    {
        foreach (var coord in world.Tiles.Items.Keys
                     .OrderBy(c => c.Q).ThenBy(c => c.R))
        {
            if (!Bootstrap.BuildingBootstrap.CanPlaceHut(world, coord)) continue;
            if (Runtime.StructurePlacement.CenterJunction(world, coord) is not { } anchor) continue;
            var hut = Core.WorldObjectMutations.SpawnObject(
                world, Content.ContentIds.Hut1Hex,
                world.Junctions.Items[anchor].Fragment, coord, anchor);
            Runtime.BuildingRules.EnsureHutElements(world, hut, completed: true);
            hut.RotationDegrees = 360f;
            Bootstrap.BuildingBootstrap.CompleteHut(world, hut);
            return hut;
        }

        throw new System.InvalidOperationException(
            "Прототипный остров не дал места под легаси-кит.");
    }
}

}
