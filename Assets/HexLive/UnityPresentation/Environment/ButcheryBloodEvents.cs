using System;
using System.Globalization;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;

namespace HexLive.UnityPresentation.Environment
{
// §56.7: cosmetic event watermark, no replay of old blood on joining/restoring.
internal sealed class ButcheryBloodEvents
{
    private bool _initialized;
    private int _seed, _tick;
    private long _seq;

    internal void Consume(WorldSnapshot snapshot, Action<NpcSnapshot, int> spawn,
        Action<ObjectSnapshot, int> spawnAtPart)
    {
        long newest = 0;
        foreach (var e in snapshot.TraceEvents) newest = Math.Max(newest, e.Seq);
        if (!_initialized || _seed != snapshot.Seed || snapshot.Tick < _tick || newest < _seq)
        {
            _initialized = true;
            _seed = snapshot.Seed;
            _seq = newest;
            _tick = snapshot.Tick;
            return;
        }
        foreach (var e in snapshot.TraceEvents)
        {
            if (e.Seq <= _seq || e.Type != "Butchered" || !e.EntityId.HasValue) continue;
            // Delayed history must not paint blood at a worker's new location.
            if (e.Tick > snapshot.Tick || snapshot.Tick - e.Tick > 8) continue;
            foreach (var npc in snapshot.Npcs)
                if (npc.Id.Value == e.EntityId.Value) { spawn(npc, e.Tick); break; }

            var marker = e.Message.IndexOf(HumanMeatCatalog.ButcheryDropIdsMarker, StringComparison.Ordinal);
            if (marker < 0) continue;
            var dropIds = e.Message.Substring(marker + HumanMeatCatalog.ButcheryDropIdsMarker.Length).Split(',');
            foreach (var id in dropIds)
            {
                if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) continue;
                foreach (var item in snapshot.Objects)
                    if (item.Id.Value == value && HumanMeatCatalog.IsHumanPart(item.DefinitionId) &&
                        !HumanMeatCatalog.IsCooked(item.DefinitionId))
                    { spawnAtPart(item, e.Tick); break; }
            }
        }
        _seq = Math.Max(_seq, newest);
        _tick = snapshot.Tick;
    }
}
}
