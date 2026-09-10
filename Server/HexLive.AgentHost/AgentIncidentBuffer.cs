using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HexLive.AgentHost.Tests")]

namespace HexLive.AgentHost;

/// <summary>Personally observed facts awaiting a durable model decision.
/// Polling advances transport cursors independently of decision consumption.</summary>
internal sealed class AgentIncidentBuffer
{
    internal const int Capacity = 64;
    private readonly List<JsonElement> _pending = new();
    private long _lastSeen;
    private long _dropped;

    internal bool HasPending => _pending.Count != 0 || _dropped != 0;

    internal void Reset()
    {
        _pending.Clear();
        _lastSeen = 0;
        _dropped = 0;
    }

    internal void Observe(JsonElement response)
    {
        if (!response.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return;
        foreach (var item in events.EnumerateArray())
        {
            if (!item.TryGetProperty("seq", out var seqValue) || !seqValue.TryGetInt64(out var seq) ||
                seq <= _lastSeen || !item.TryGetProperty("type", out var type)) continue;
            var name = type.GetString();
            if (name != "AgentObservedIntruder" && name != "AgentObservedTheft" && name != "AgentObservedLoot") continue;
            _lastSeen = seq;
            if (_pending.Count == Capacity)
            {
                _pending.RemoveAt(0);
                _dropped++;
            }
            _pending.Add(item.Clone());
        }
    }

    internal JsonElement Snapshot() => JsonSerializer.SerializeToElement(new
    {
        watermark = _lastSeen,
        dropped = _dropped,
        events = _pending.ToArray(),
    });

    internal void Consume(JsonElement snapshot)
    {
        var watermark = snapshot.GetProperty("watermark").GetInt64();
        _pending.RemoveAll(item => item.GetProperty("seq").GetInt64() <= watermark);
        _dropped = Math.Max(0, _dropped - snapshot.GetProperty("dropped").GetInt64());
    }
}
