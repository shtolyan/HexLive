using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Server.Mcp;

internal static class McpPerceptionObservations
{
    public static object? Recent(PerceptionObservationBatch? batch)
    {
        if (batch == null) return null;
        var rows = new List<object>();
        foreach (var seen in batch.Observations)
        {
            // Current perception is already provided by describe_colonist.
            // Only add sightings the model has not consumed and no longer sees.
            if (seen.InLatestPerception && !seen.Significant) continue;
            var center = HexSpatialMath.TileToWorld(new TileCoord(seen.TileQ, seen.TileR));
            var row = new Dictionary<string, object?>
            {
                ["kind"] = seen.Kind,
                ["id"] = seen.Id,
                ["lastSeenTile"] = new { q = seen.TileQ, r = seen.TileR },
                ["lastSeenTileCenter"] = new { x = center.X, y = center.Y },
                ["observerTile"] = new { q = seen.ObserverTileQ, r = seen.ObserverTileR },
                ["firstSeenTick"] = seen.FirstSeenTick,
                ["lastSeenTick"] = seen.LastSeenTick,
                ["ageTicks"] = seen.AgeTicks,
                ["significantEvent"] = seen.Significant,
            };
            if (seen.Kind == "npc")
            {
                row["nameId"] = seen.NameId;
                row["hostileWhenSeen"] = seen.Hostile;
                row["unconsciousWhenSeen"] = seen.Unconscious;
                row["dyingWhenSeen"] = seen.Dying;
                row["sufferingWhenSeen"] = seen.Suffering;
                row["aidKindWhenSeen"] = seen.AidKind.ToString();
            }
            else
            {
                row["definitionId"] = seen.DefinitionId;
                if (seen.Kind == "mob")
                {
                    row["healthWhenSeen"] = seen.Health;
                    row["statusWhenSeen"] = seen.MobStatus.ToString();
                    row["targetedMeWhenSeen"] = seen.TargetsMe;
                }
            }
            rows.Add(row);
        }
        return new { epoch = batch.Epoch, watermark = batch.Watermark, reset = batch.Reset,
            gap = batch.Gap, observations = rows };
    }
}
