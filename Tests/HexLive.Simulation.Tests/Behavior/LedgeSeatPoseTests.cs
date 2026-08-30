using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

// Bug #332: «сидит на краю гексагона и проваливается внутрь». Каноничность
// джанкшена — правило планировщика («куда сесть»), а не позы: на прототипном
// острове ~3/4 шовных джанкшенов НЕ каноничны, и сидящая на любом из них
// (ручное «присесть», §137-отдых, снос прибытия) теряла весь подъём на уступ.
public sealed class LedgeSeatPoseTests
{
    [Test]
    public void PoseGeometryDoesNotRequireTheCanonicalSeamJunction()
    {
        var world = TestWorld.CreateWorld();

        var nonCanonical = world.Junctions.Items.Values.FirstOrDefault(junction =>
            junction.Tiles.Count > 1 &&
            PlanningSystem.TryGetEdgeSeatGeometry(
                world, junction, false, requireCanonical: false, out _, out _) &&
            !PlanningSystem.TryGetEdgeSeatGeometry(
                world, junction, false, requireCanonical: true, out _, out _));

        Assert.That(nonCanonical, Is.Not.Null,
            "Шов из одного шага почти всегда покрыт несколькими джанкшенами, " +
            "из которых канонический — один; остальные обязаны отдавать " +
            "геометрию позы (requireCanonical:false), иначе сидящая там тонет " +
            "в верхнем гексе (замер на HugeIsland: 13672 из 18306 швов).");

        Assert.That(PlanningSystem.TryGetEdgeSeatGeometry(
                world, nonCanonical, false, requireCanonical: false,
                out var standTile, out _),
            Is.True);
        Assert.That(world.Tiles.Items.ContainsKey(standTile), Is.True,
            "Геометрия позы отдаёт настоящий верхний тайл сиденья.");
    }
}

}
