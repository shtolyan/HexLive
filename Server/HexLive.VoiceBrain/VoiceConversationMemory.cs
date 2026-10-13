using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.VoiceBrain;

/// <summary>§171.21: the same append-only archive/search as Studio, private to this character and voice owner.</summary>
public sealed class VoiceConversationMemory(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    private readonly object _gate = new();
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);
    public Scope For(SessionTicket ticket)
    {
        var key = AgentMemoryArchive.Id(ticket.CharacterId) + "-" + AgentMemoryArchive.Id(ticket.OwnerUserId);
        lock (_gate)
        {
            if (!_scopes.TryGetValue(key, out var scope))
            {
                if (_scopes.Count >= 64) _scopes.Remove(_scopes.MinBy(p => p.Value.LastUsedUtc).Key);
                scope = new Scope(Path.Combine(directory, key), ticket.WorldId, AgentMemoryArchive.Id(ticket.OwnerUserId));
                _scopes.Add(key, scope);
            }
            scope.LastUsedUtc = DateTimeOffset.UtcNow; return scope;
        }
    }
    public sealed class Scope
    {
        private readonly AgentMemoryArchive _archive;
        private readonly AgentMemorySearch _search;
        private readonly string _episode, _speaker;
        internal DateTimeOffset LastUsedUtc;
        internal Scope(string root, string episode, string speaker)
        { _archive = new(root); _search = new(root); _episode = episode; _speaker = speaker; }
        private bool Allowed(AgentMemoryRecord row) => row.Speaker == _speaker && row.Episode == _episode && row.Source == "server-voice";
        private static string ReceiptId(string sessionId) => AgentMemoryArchive.Id(sessionId + ":provider:import-complete");
        public bool HasCompletedImport(string sessionId)
        {
            _search.Refresh();
            return _search.Read(ReceiptId(sessionId), allowed: row => row.Speaker == _speaker && row.Episode == _episode &&
                row.Source == "server-voice-receipt").SourceIds.Length > 0;
        }
        public bool Record(string turnId, string role, string text, bool incomplete = false)
        {
            if (text.Length == 0) return false;
            if (role is not ("user" or "assistant")) throw new ArgumentOutOfRangeException(nameof(role));
            return _archive.Append(new AgentMemoryRecord {
                Id = AgentMemoryArchive.Id(turnId + "\n" + role + "\n" + text + "\n" + incomplete),
                Kind = role == "user" ? "player" : "speech", Text = text, Group = turnId,
                Episode = _episode, Speaker = _speaker, Source = "server-voice",
                Status = role == "user" ? "received" : "streamed", Incomplete = incomplete,
                OccurredUtc = DateTimeOffset.UtcNow
            });
        }
        public void Import(string sessionId, IReadOnlyList<ConversationUtterance> transcript, bool incomplete)
        {
            for (var i = 0; i < transcript.Count; i++)
            {
                var row = transcript[i];
                Record(AgentMemoryArchive.Id(sessionId + ":provider:" + i), row.Role, row.Text, incomplete || row.Interrupted);
            }
            if (!incomplete) _archive.Append(new AgentMemoryRecord { Id = ReceiptId(sessionId), Kind = "event",
                Text = transcript.Count == 0 ? "Провайдер завершил разговор без доступной расшифровки." : "Расшифровка завершённого разговора сохранена.",
                Episode = _episode, Speaker = _speaker, Source = "server-voice-receipt", Incomplete = transcript.Count == 0 });
        }
        public string Context(string query, string currentTurn)
        {
            if (query.Length > 1000) query = query[..1000];
            _search.Refresh();
            bool Old(AgentMemoryRecord row) => Allowed(row) && row.Group != currentTurn;
            var rows = _search.Search(query, allowed: Old).Hits.Take(4)
                .Concat(_search.Search("", order: "newest", allowed: Old).Hits.Take(6))
                .Select(h => h.Record).Where(r => r.Group != currentTurn).DistinctBy(r => (r.Kind, r.Text));
            var result = new List<object>(); var used = 0;
            foreach (var row in rows)
            {
                var text = row.Text.Length > 600 ? row.Text[..600] : row.Text;
                if (used + text.Length > 6000) break;
                used += text.Length;
                result.Add(new { sourceId = row.Id, role = row.Kind == "player" ? "user" : "assistant",
                    row.OccurredUtc, row.Incomplete, text, excerpt = text.Length != row.Text.Length });
            }
            return JsonSerializer.Serialize(result, Json);
        }
        public string Search(string query, int offset, string? excludeTurn = null)
        {
            _search.Refresh();
            var page = _search.Search(query, offset: Math.Max(0, offset), allowed: row => Allowed(row) && row.Group != excludeTurn);
            return JsonSerializer.Serialize(new { page.Total, page.NextOffset,
                sources = page.Hits.Select(h => new { sourceId = h.Record.Id, h.Record.Kind, h.Record.OccurredUtc,
                    h.Record.Incomplete, snippet = h.Record.Text.Length > 350 ? h.Record.Text[..350] : h.Record.Text }) }, Json);
        }
        public string Read(string sourceId, int offset, string? excludeTurn = null)
        { _search.Refresh(); return JsonSerializer.Serialize(_search.Read(sourceId, Math.Max(0, offset), row => Allowed(row) && row.Group != excludeTurn), Json); }
    }
}
