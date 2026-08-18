using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// #179: стартовый дом обязан быть обходим со всех шести сторон, а его дверь —
/// достижима без прыжка. До правила спавн терпел до трёх ГОРНЫХ соседей
/// (|Δвысоты| >= 2 запирает весь общий обод, а портал двери принудительно
/// разлочивается и «открывался» в запертый карман) — 26 из 60 сидов ставили
/// дом с запертой стороной.
/// </summary>
public sealed class HutSpawnApproachGateTests
{
    // Немного сидов, но разных: полный мир строится сотни миллисекунд, а
    // замер правила уже сделан на 60 сидах (0 нарушений, 0 миров без дома).
    [TestCase(12345)]
    [TestCase(7920)]     // seed#1 замера
    [TestCase(245489)]   // seed#31 замера — на старом правиле сторона заперта
    [TestCase(277195)]   // seed#35 замера — на старом правиле сторона заперта
    public void StartHutIsApproachableFromAllSidesAndDoorIsReachable(int seed)
    {
        var world = TestWorld.CreateWorld(seed);
        var hut = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan &&
            string.IsNullOrEmpty(obj.BuildProduct));
        var tile = world.Tiles.Items[hut.Tile];

        foreach (var direction in HexDirection.All)
        {
            var coord = new TileCoord(
                hut.Tile.Q + direction.DQ, hut.Tile.R + direction.DR);
            Assert.That(world.Tiles.Items.TryGetValue(coord, out var neighbor),
                Is.True, $"seed {seed}: у дома {hut.Tile} нет соседа {coord}.");
            Assert.That(neighbor.Flags.HasFlag(TileFlags.Walkable) &&
                !neighbor.Flags.HasFlag(TileFlags.Water) &&
                !neighbor.Flags.HasFlag(TileFlags.Blocked),
                Is.True, $"seed {seed}: сосед {coord} дома {hut.Tile} непроходим.");
            Assert.That(Math.Abs(neighbor.Elevation - tile.Elevation), Is.LessThanOrEqualTo(1),
                $"seed {seed}: сосед {coord} дома {hut.Tile} — обрыв " +
                $"(Δ={neighbor.Elevation - tile.Elevation}), общий обод заперт.");
        }

        // Дверь достижима от очага лагеря БЕЗ прыжка — тем же строгим порогом,
        // которым ходят калеки (§50): свою дверь обязана открыть даже одноногая.
        var home = world.FactionHomes[Faction.Colony];
        var homeCenter = StructurePlacement.CenterJunction(world, home);
        Assert.That(homeCenter, Is.Not.Null);
        JunctionId? portal = null;
        foreach (var piece in BuildingRules.ArchitectureObjects(world, hut))
        {
            foreach (var id in piece.Junctions)
            {
                if (world.Junctions.Items.TryGetValue(id, out var junction) &&
                    junction.Door)
                {
                    portal = id;
                    break;
                }
            }

            if (portal is not null) break;
        }

        Assert.That(portal, Is.Not.Null, $"seed {seed}: у дома {hut.Tile} нет портала двери.");
        Assert.That(
            Connectivity.ReachableBeside(world, homeCenter.Value, portal.Value),
            Is.True,
            $"seed {seed}: к двери дома {hut.Tile} нельзя подойти от лагеря {home}.");
    }
}

}
