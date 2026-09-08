using System;
using System.Collections.Generic;
using System.Linq;

namespace HexLive.Server.Mcp;

// The shared game token authenticates the connection; the same client ID as /watch
// selects its assignments. Session IDs are server-issued, never supplied by callers.
internal sealed class PlayerTokenSessions
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, (string Player, string World, DateTimeOffset Until)> _sessions = new();
    public PlayerTokenSessions(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);
    public string Create(string player, string world)
    {
        lock (_gate)
        {
            Prune();
            if (_sessions.Count >= 1024) throw new InvalidOperationException("PlayerSessionLimit");
            var id = Guid.NewGuid().ToString("N");
            _sessions[id] = (player, world, _now().AddMinutes(5));
            return id;
        }
    }
    public bool Validate(string id, string player, string world)
    {
        lock (_gate)
        {
            Prune();
            if (!_sessions.TryGetValue(id, out var s) || s.Player != player || s.World != world) return false;
            _sessions[id] = (player, world, _now().AddMinutes(5));
            return true;
        }
    }
    public void Close(string id) { lock (_gate) _sessions.Remove(id); }
    private void Prune()
    { foreach (var id in _sessions.Where(s => s.Value.Until <= _now()).Select(s => s.Key).ToArray()) _sessions.Remove(id); }
}
