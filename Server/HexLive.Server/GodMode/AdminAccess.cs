using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.Server.GodMode;

/// <summary>§161 persistent requests bound to an OS-protected device secret, not a public client id.</summary>
public sealed class AdminAccess
{
    public sealed record Request(string Id, string Client, string SecretHash, DateTimeOffset CreatedUtc,
        string State = "pending", DateTimeOffset? ExpiresUtc = null);
    public sealed class State
    {
        public int Version { get; set; } = 2;
        public Dictionary<string, string> LegacyGrants { get; set; } = new();
        public List<Request> Requests { get; set; } = new();
    }
    public sealed record Status(bool Accepted, string State, string RequestId = "", DateTimeOffset? ExpiresUtc = null);
    public Func<string, string, bool>? CentralAuthorize { get; set; }
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private State _state;
    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    public AdminAccess(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path; _now = now ?? (() => DateTimeOffset.UtcNow);
        if (!File.Exists(path)) { _state = new State(); return; }
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        _state = doc.RootElement.TryGetProperty("Version", out _) ? JsonSerializer.Deserialize<State>(json)!
            : new State { LegacyGrants = JsonSerializer.Deserialize<Dictionary<string, string>>(json)! };
        if (_state == null || _state.Version != 2 || _state.LegacyGrants == null || _state.Requests == null) throw new InvalidDataException("Invalid admin access state");
    }
    private static bool ValidSecret(string token) => token != null && token.Length == 64 && token.All(Uri.IsHexDigit);
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    private string Effective(Request r) => r.State == "approved" && r.ExpiresUtc <= _now() ? "expired" : r.State;
    private Status GetStatus(string client, string token)
    {
        if (CentralAuthorize != null) return CentralAuthorize(client, token) ? new(true, "approved") : new(false, "denied");
        if (!ValidSecret(token)) return new(false, "unrequested");
        var hash = Hash(token);
        if (_state.LegacyGrants.TryGetValue(client, out var legacy) && Equal(legacy, hash)) return new(true, "approved");
        var r = _state.Requests.LastOrDefault(r => r.Client == client && Equal(r.SecretHash, hash));
        return r == null ? new(false, "unrequested") : new(Effective(r) == "approved", Effective(r), r.Id, r.ExpiresUtc);
    }
    public Status Check(string client, string token)
    {
        lock (_gate)
        {
            var status = GetStatus(client, token);
            if (status.RequestId.Length > 0) _seen[status.RequestId] = _now();
            return status;
        }
    }
    public Status RequestAccess(string client, string token)
    {
        if (CentralAuthorize != null) return Check(client, token);
        if (!PlayerCharacterAssignments.TryNormalizePlayerId(client, out var canonical) || !ValidSecret(token)) return new(false, "invalid");
        lock (_gate)
        {
            var status = GetStatus(canonical, token);
            if (status.State is "pending" or "approved") return Check(canonical, token);
            if (_state.Requests.Count(r => r.State == "pending") >= 128 ||
                _state.Requests.Count(r => r.Client == canonical && r.State == "pending") >= 3) return new(false, "queue_full");
            var request = new Request(Guid.NewGuid().ToString("N"), canonical, Hash(token), _now());
            var next = Copy(); next.Requests.Add(request); Store(next);
            _seen[request.Id] = _now(); return new(false, "pending", request.Id);
        }
    }
    public void Decide(string id, bool approve, TimeSpan? duration)
    {
        if (duration.HasValue && duration.Value <= TimeSpan.Zero) throw new ArgumentException("Invalid duration");
        lock (_gate)
        {
            var index = _state.Requests.FindIndex(r => r.Id == id);
            if (index < 0 || _state.Requests[index].State != "pending") throw new ArgumentException("Request no longer pending");
            var next = Copy(); var request = next.Requests[index];
            next.Requests[index] = request with { State = approve ? "approved" : "rejected", ExpiresUtc = approve && duration.HasValue ? _now() + duration.Value : null };
            Store(next);
        }
    }
    public object[] Requests()
    {
        lock (_gate) return _state.Requests.OrderByDescending(r => r.CreatedUtc).Select(r => (object)new
        { id = r.Id, client = r.Client, createdUtc = r.CreatedUtc, state = Effective(r), expiresUtc = r.ExpiresUtc,
            connected = _seen.TryGetValue(r.Id, out var seen) && _now() - seen < TimeSpan.FromSeconds(20) }).ToArray();
    }
    public bool Authorized(string client, string token) { lock (_gate) return GetStatus(client, token).Accepted; }
    public T WithAuthorization<T>(string client, string token, Func<T> action, Func<T> denied)
    { lock (_gate) return GetStatus(client, token).Accepted ? action() : denied(); }
    public string[] Clients { get { lock (_gate) return _state.LegacyGrants.Keys.Concat(_state.Requests.Where(r => Effective(r) == "approved").Select(r => r.Client)).Distinct().OrderBy(x => x).ToArray(); } }
    public void Revoke(string client)
    {
        lock (_gate)
        {
            var next = Copy(); next.LegacyGrants.Remove(client);
            next.Requests = next.Requests.Select(r => r.Client == client && (r.State is "approved" or "pending") ? r with { State = "revoked" } : r).ToList();
            Store(next);
        }
    }
    private State Copy() => new() { LegacyGrants = new(_state.LegacyGrants), Requests = new(_state.Requests) };
    private void Store(State next) { WritePrivate(_path, JsonSerializer.Serialize(next)); _state = next; }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void WritePrivate(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
