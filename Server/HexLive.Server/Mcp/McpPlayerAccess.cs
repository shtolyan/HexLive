using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.Server.Mcp;

public sealed record AgentPairingTicket(string Id, string Code, string PollSecret, DateTimeOffset ExpiresUtc);
public sealed record AgentPairingResult(string State, string? Credential = null);
public sealed record AgentAccessGrant(string Id, string PlayerId, string DisplayName, string CredentialHash);

/// <summary>§163: only an authenticated /watch owner may call Approve. No model/provider data.</summary>
public sealed class McpPlayerAccess
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, DateTimeOffset Until)> _rates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentAccessGrant> _grants;
    private sealed record Pending(string Name, string Code, string PollHash, DateTimeOffset Until)
    {
        public int Attempts { get; set; }
        public string? Credential { get; set; }
    }
    private sealed record Session(string GrantId, string CredentialHash, string WorldId, DateTimeOffset Until);
    public McpPlayerAccess(string path, Func<DateTimeOffset>? now = null)
    {
        _path = Path.GetFullPath(path); _now = now ?? (() => DateTimeOffset.UtcNow);
        if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024)
            throw new InvalidDataException("InvalidAgentAccessStore");
        var records = File.Exists(_path) ? JsonSerializer.Deserialize<AgentAccessGrant[]>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("InvalidAgentAccessStore") : Array.Empty<AgentAccessGrant>();
        if (records.Length > 1024 || records.Any(g => g == null || !Guid.TryParseExact(g.Id, "N", out _) ||
            !PlayerCharacterAssignments.TryNormalizePlayerId(g.PlayerId, out var normalized) || normalized != g.PlayerId ||
            string.IsNullOrWhiteSpace(g.DisplayName) || g.DisplayName.Length > 48 || g.DisplayName.Any(char.IsControl) ||
            g.CredentialHash == null || g.CredentialHash.Length != 64 || !g.CredentialHash.All(Uri.IsHexDigit)) ||
            records.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != records.Length ||
            records.Select(g => g.CredentialHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Length)
            throw new InvalidDataException("InvalidAgentAccessStore");
        _grants = records.ToDictionary(g => g.Id, StringComparer.Ordinal);
    }
    public AgentPairingTicket Begin(string displayName, string source)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 48 || displayName.Any(char.IsControl) || source.Length > 128)
            throw new ArgumentException("InvalidPairingRequest");
        lock (_gate)
        {
            Prune();
            var now = _now();
            _rates.TryGetValue(source, out var rate);
            if (rate.Count >= 4 || _pending.Count >= 64 || (!_rates.ContainsKey(source) && _rates.Count >= 1024))
                throw new InvalidOperationException("PairingRateLimited");
            _rates[source] = (rate.Count + 1, rate.Count == 0 ? now.AddMinutes(1) : rate.Until);
            var id = Guid.NewGuid().ToString("N");
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var until = now.AddMinutes(2);
            _pending.Add(id, new(displayName.Trim(), code, Hash(secret), until));
            return new(id, code, secret, until);
        }
    }
    public string? DescribePending(string id)
    { lock (_gate) { Prune(); return _pending.TryGetValue(id, out var p) ? p.Name : null; } }
    public bool Approve(string id, string code, string authenticatedPlayerId)
    {
        if (!PlayerCharacterAssignments.TryNormalizePlayerId(authenticatedPlayerId, out var owner)) return false;
        lock (_gate)
        {
            Prune();
            if (!_pending.TryGetValue(id, out var pending) || pending.Credential != null || ++pending.Attempts > 8 ||
                code.Length != 8 || !FixedEquals(pending.Code, code.ToUpperInvariant()) || _grants.Count >= 1024) return false;
            var credential = "hexagent_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var grant = new AgentAccessGrant(Guid.NewGuid().ToString("N"), owner, pending.Name, Hash(credential));
            _grants.Add(grant.Id, grant);
            try { Save(); }
            catch { _grants.Remove(grant.Id); throw; }
            pending.Credential = credential;
            return true;
        }
    }
    public AgentPairingResult Poll(string id, string pollSecret)
    {
        lock (_gate)
        {
            Prune();
            if (pollSecret.Length != 64 || !_pending.TryGetValue(id, out var p) || !FixedEquals(p.PollHash, Hash(pollSecret)))
                return new("Unavailable");
            // A lost HTTP response can be retried until expiry; credentials never enter logs/disk in plaintext.
            return p.Credential == null ? new("Pending") : new("Approved", p.Credential);
        }
    }
    public AgentAccessGrant? Authorize(string credential)
    {
        if (!credential.StartsWith("hexagent_", StringComparison.Ordinal) || credential.Length != 73) return null;
        lock (_gate)
        {
            var hash = Hash(credential);
            return _grants.Values.FirstOrDefault(g => FixedEquals(g.CredentialHash, hash));
        }
    }
    public string CreateSession(string credential, string worldId)
    {
        lock (_gate)
        {
            Prune();
            var grant = Authorize(credential) ?? throw new UnauthorizedAccessException("AgentCredentialRejected");
            if (_sessions.Count >= 1024) throw new InvalidOperationException("AgentSessionLimit");
            var id = Guid.NewGuid().ToString("N");
            _sessions.Add(id, new(grant.Id, grant.CredentialHash, worldId, _now().AddMinutes(5)));
            return id;
        }
    }
    public AgentAccessGrant? AuthorizeSession(string id, string credential, string worldId)
    {
        lock (_gate)
        {
            Prune();
            var grant = Authorize(credential);
            if (grant == null || !_sessions.TryGetValue(id, out var session) || session.GrantId != grant.Id ||
                session.WorldId != worldId || !FixedEquals(session.CredentialHash, grant.CredentialHash)) return null;
            _sessions[id] = session with { Until = _now().AddMinutes(5) };
            return grant;
        }
    }
    public bool Revoke(string id, string authenticatedPlayerId)
    {
        if (!PlayerCharacterAssignments.TryNormalizePlayerId(authenticatedPlayerId, out var owner)) return false;
        lock (_gate)
        {
            if (!_grants.TryGetValue(id, out var grant) || grant.PlayerId != owner) return false;
            _grants.Remove(id);
            try { Save(); }
            catch { _grants.Add(id, grant); throw; }
            foreach (var session in _sessions.Where(s => s.Value.GrantId == id).Select(s => s.Key).ToArray()) _sessions.Remove(session);
            return true;
        }
    }
    public void CloseSession(string id) { lock (_gate) _sessions.Remove(id); }
    private void Prune()
    {
        var now = _now();
        foreach (var id in _pending.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) _pending.Remove(id);
        foreach (var id in _rates.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) _rates.Remove(id);
        foreach (var id in _sessions.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) _sessions.Remove(id);
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_grants.Values));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
