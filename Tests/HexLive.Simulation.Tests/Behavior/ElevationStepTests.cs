using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §21.21B: КАЖДЫЙ переход между тайлами разной высоты обязан быть прыжком.
/// <para>
/// Если перепад пересекается обычным шагом, вид не получает ни клипа, ни дуги —
/// он просто снапает корень на новый уровень ступени (0.55 wu за кадр). Именно
/// это игрок видит как «её телепнуло наверх»: она спрыгнула, развернулась и
/// оказалась обратно на уступе; или «прыгнула в воду без плюха и её выкинуло
/// назад». Симптомы разные, причина одна, поэтому проверка тут одна.
/// </para>
/// </summary>
public sealed class ElevationStepTests
{
    private sealed record Crossing(int Tick, int Npc, string Message, int From, int To, bool Hopped);

    [Test]
    public void EveryElevationCrossingIsAHop()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var crossings = new List<Crossing>();
        var hopLandedThisTick = new HashSet<int>();
        var watermark = 0L;

        for (var tick = 0; tick < 4000; tick++)
        {
            hopLandedThisTick.Clear();
            engine.Step();

            // Порядок событий в тике не гарантирован, поэтому сперва собираем
            // все посадки, и только потом судим переходы.
            var fresh = new List<(string type, int npc, string message)>();
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark)
                {
                    continue;
                }

                watermark = e.Seq;
                if (e.Type is "HopLanded" or "EnteredTile")
                {
                    fresh.Add((e.Type, e.EntityId ?? -1, e.Message));
                }
            }

            foreach (var (type, npc, _) in fresh)
            {
                if (type == "HopLanded")
                {
                    hopLandedThisTick.Add(npc);
                }
            }

            foreach (var (type, npc, message) in fresh)
            {
                if (type != "EnteredTile" ||
                    !TryParseTiles(message, out var from, out var to) ||
                    !world.Tiles.Items.TryGetValue(from, out var fromTile) ||
                    !world.Tiles.Items.TryGetValue(to, out var toTile) ||
                    fromTile.Elevation == toTile.Elevation)
                {
                    continue;
                }

                crossings.Add(new Crossing(world.Tick, npc, message,
                    fromTile.Elevation, toTile.Elevation, hopLandedThisTick.Contains(npc)));
            }
        }

        Assert.That(crossings, Is.Not.Empty,
            "За 4000 тиков никто не сменил уровень — мир не тот или NPC не ходят.");

        var walked = crossings.Where(c => !c.Hopped).ToList();
        var report = string.Join("\n", walked.Take(12).Select(c =>
            $"  t{c.Tick} NPC {c.Npc}: {c.From} -> {c.To} ({c.Message})"));

        Assert.That(walked, Is.Empty,
            $"{walked.Count} из {crossings.Count} переходов между уровнями сделаны ШАГОМ, " +
            "без прыжка — вид на таком переходе просто снапает корень на ступень выше/ниже, " +
            $"и это читается как телепорт:\n{report}");
    }

    // "From=11,-8 To=12,-9"
    private static bool TryParseTiles(string message, out TileCoord from, out TileCoord to)
    {
        from = default;
        to = default;
        return TryParseCoord(message, "From=", out from) && TryParseCoord(message, "To=", out to);
    }

    private static bool TryParseCoord(string message, string prefix, out TileCoord coord)
    {
        coord = default;
        var at = message.IndexOf(prefix, System.StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var body = message.Substring(at + prefix.Length);
        var end = body.IndexOf(' ');
        if (end >= 0)
        {
            body = body.Substring(0, end);
        }

        var parts = body.Split(',');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var q) ||
            !int.TryParse(parts[1], out var r))
        {
            return false;
        }

        coord = new TileCoord(q, r);
        return true;
    }
}

}
