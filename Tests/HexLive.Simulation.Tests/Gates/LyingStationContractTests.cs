using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// ⭐ КОНТРАКТ §111.13: у лежащего тела ровно ОДИН способ поставить к нему
/// человека — через станцию.
///
/// Пока снап звался «встать у ног», каждое место звало его само, и мест этих
/// шесть. Проверить их поимённо глазами нельзя: они в разных файлах и добавляются
/// по одному вместе с новыми видами помощи. Поэтому правило проверяется текстом:
/// имени прежнего снапа в production-коде больше не существует, а каждый
/// исполнитель сцены у лежащего обязан спрашивать станцию.
/// </summary>
public sealed class LyingStationContractTests
{
    private static string Read(params string[] parts) => File.ReadAllText(
        Path.Combine(RepoPaths.Root, Path.Combine(parts)));

    private static string Sim(params string[] tail)
    {
        var parts = new string[4 + tail.Length];
        parts[0] = "Assets";
        parts[1] = "HexLive";
        parts[2] = "Simulation";
        parts[3] = "Runtime";
        tail.CopyTo(parts, 4);
        return Read(parts);
    }

    [Test]
    public void EveryLyingSceneAsksForAStationInsteadOfTheFeetPoint()
    {
        var stations = Sim("Helpers", "LyingStations.cs");
        var spot = Sim("Helpers", "LyingSpot.cs");
        var aid = Sim("Systems", "Ai", "ExecutionSystem.Social.cs");
        var loot = Sim("Systems", "Ai", "ExecutionSystem.LootHelpless.cs");
        var limb = Sim("Systems", "Ai", "ExecutionSystem.Prosthetics.cs");
        var planner = Sim("Systems", "Ai", "PlanningSystem.Plans.Social.cs");
        var interrupt = Sim("Systems", "Ai", "PlanInterruption.cs");

        Assert.Multiple(() =>
        {
            Assert.That(spot + aid + loot + limb + planner,
                Does.Not.Contain("AlignInteractorAtFeet"),
                "Единая точка у ног отменена §111.13: позу ставит LyingStations.Align(slot).");

            foreach (var scene in new[] { aid, loot, limb })
            {
                Assert.That(scene, Does.Contain("LyingStations.Align("));
                Assert.That(scene, Does.Contain("LyingStations.TryClaim("));
            }

            // Планировщик обязан брать станцию ДО брони узла подхода, иначе
            // двое в одном тике уйдут на одну и ту же точку. Он клеймит
            // КОНКРЕТНЫЙ слот (bug-255: доказать каждую станцию по приоритету,
            // а не первую геометрически пригодную).
            Assert.That(planner, Does.Contain("LyingStations.TryClaimSlot("));

            // Место освобождается в общей воронке срыва плана, а не по месту.
            Assert.That(interrupt, Does.Contain("LyingStations.ReleaseStation("));

            // Смещения выводятся из шага суб-сетки, а не вбиты числами.
            Assert.That(stations, Does.Contain("HexPointLayout.BoundaryRadius"));
            Assert.That(stations, Does.Not.Contain("0.3248"));
            Assert.That(stations, Does.Not.Contain("0.1875"));
        });
    }
}

}
