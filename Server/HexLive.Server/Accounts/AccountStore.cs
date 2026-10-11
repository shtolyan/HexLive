using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace HexLive.Server.Accounts;

public sealed record GameSession(string PlayerId, string SessionToken, DateTimeOffset ExpiresAt);
public sealed record AccountSession(string PlayerId, string Provider, DateTimeOffset ExpiresAt);

/// <summary>§171.9: one host-owned JSON transaction for accounts and hashed sessions.</summary>
public sealed class AccountStore : IPlayerSessionResolver
{
    private const string Prefix = "hexsession_";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private StoreData _data;
    private readonly Dictionary<string, List<CancellationTokenSource>> _connections = new(StringComparer.Ordinal);

    public AccountStore(string path, string projectId, TimeProvider? clock = null)
    {
        _path = Path.GetFullPath(path);
        _clock = clock ?? TimeProvider.System;
        _data = new(1, projectId, Array.Empty<Account>(), Array.Empty<Session>());
        if (!File.Exists(_path)) return;
        try
        {
            Restrict(_path);
            var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path), Json);
            if (data == null || data.Version != 1 || data.ProjectId != projectId || data.Accounts == null || data.Sessions == null ||
                data.Accounts.Any(a => a == null || !PlayerCharacterAssignments.TryNormalizePlayerId(a.AccountId, out var id) || id != a.AccountId ||
                    string.IsNullOrWhiteSpace(a.FirebaseUid) || a.FirebaseUid.Length > 128 || a.Providers == null ||
                    a.Providers.Length == 0 || a.Providers.Any(p => p is not ("google.com" or "apple.com" or "password"))) ||
                data.Accounts.Select(a => a.AccountId).Distinct().Count() != data.Accounts.Length ||
                data.Accounts.Select(a => a.FirebaseUid).Distinct().Count() != data.Accounts.Length ||
                data.Sessions.Any(s => s == null || s.Hash == null || s.Hash.Length != 64 || s.Hash.Any(c => !char.IsAsciiHexDigit(c)) ||
                    !data.Accounts.Any(a => a.AccountId == s.AccountId && a.Providers.Contains(s.Provider))) ||
                data.Sessions.Select(s => s.Hash).Distinct().Count() != data.Sessions.Length)
                throw new InvalidDataException("Invalid account store schema or Firebase project mismatch.");
            _data = data;
        }
        catch (JsonException ex) { throw new InvalidDataException("Cannot read account store safely.", ex); }
    }

    public GameSession CreateSession(FirebaseIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Uid) || identity.Uid.Length > 128 ||
            identity.Provider is not ("google.com" or "apple.com" or "password")) throw new ArgumentException("Invalid verified identity.");
        var token = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var previous = _data.Accounts.SingleOrDefault(a => a.FirebaseUid == identity.Uid);
            var account = previous == null
                ? new Account(Guid.NewGuid().ToString("N"), identity.Uid, new[] { identity.Provider }, now, now)
                : previous with { LastSeen = now, Providers = previous.Providers.Append(identity.Provider).Distinct().Order().ToArray() };
            var expiry = now.AddDays(30);
            var session = new Session(Hash(token), account.AccountId, identity.Provider, now, expiry);
            Save(_data with {
                Accounts = _data.Accounts.Where(a => a.AccountId != account.AccountId).Append(account).ToArray(),
                Sessions = _data.Sessions.Where(s => s.ExpiresAt > now).Append(session).ToArray() });
            return new(account.AccountId, token, expiry);
        }
    }

    public bool TryResolve(string bearer, out string playerId)
    {
        var session = Resolve(bearer);
        playerId = session?.PlayerId ?? "";
        return session != null;
    }

    public AccountSession? Resolve(string? bearer)
    {
        if (!ValidToken(bearer)) return null;
        lock (_gate)
        {
            var session = FindSession(bearer!);
            return session == null ? null : new(session.AccountId, session.Provider, session.ExpiresAt);
        }
    }

    public bool Revoke(string? bearer)
    {
        if (!ValidToken(bearer)) return false;
        lock (_gate)
        {
            var hash = Hash(bearer!);
            if (!_data.Sessions.Any(s => s.Hash == hash)) return false;
            Save(_data with { Sessions = _data.Sessions.Where(s => s.Hash != hash).ToArray() });
            if (_connections.Remove(hash, out var connections))
                foreach (var connection in connections) connection.Cancel();
            return true;
        }
    }

    /// <summary>§171.9: logout/expiry also stops sockets already upgraded with this session.</summary>
    public SessionConnection? Connect(string? bearer)
    {
        if (!ValidToken(bearer)) return null;
        lock (_gate)
        {
            var session = FindSession(bearer!);
            if (session == null) return null;
            var cancel = new CancellationTokenSource(session.ExpiresAt - _clock.GetUtcNow(), _clock);
            if (!_connections.TryGetValue(session.Hash, out var connections))
                _connections[session.Hash] = connections = new();
            connections.Add(cancel);
            return new SessionConnection(session.AccountId, cancel, () => {
                lock (_gate)
                {
                    if (_connections.TryGetValue(session.Hash, out var active))
                    {
                        active.Remove(cancel);
                        if (active.Count == 0) _connections.Remove(session.Hash);
                    }
                    cancel.Dispose();
                }
            });
        }
    }

    private Session? FindSession(string bearer)
    {
        var hash = Hash(bearer);
        return _data.Sessions.FirstOrDefault(s => s.Hash == hash && s.ExpiresAt > _clock.GetUtcNow());
    }

    private static bool ValidToken(string? token) => token != null && token.StartsWith(Prefix, StringComparison.Ordinal) &&
        token.Length == Prefix.Length + 43 && token[Prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void Save(StoreData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, data, Json);
                stream.Flush(flushToDisk: true);
            }
            Restrict(temporary); // fail closed, BEFORE publishing; the temp file is private too
            File.Move(temporary, _path, overwrite: true);
            _data = data; // failed persistence must never issue a memory-only session or revocation
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record StoreData(int Version, string ProjectId, Account[] Accounts, Session[] Sessions);
    private sealed record Account(string AccountId, string FirebaseUid, string[] Providers, DateTimeOffset Created, DateTimeOffset LastSeen);
    private sealed record Session(string Hash, string AccountId, string Provider, DateTimeOffset Created, DateTimeOffset ExpiresAt);
}

public sealed class SessionConnection(string playerId, CancellationTokenSource cancel, Action dispose) : IDisposable
{
    public string PlayerId { get; } = playerId;
    public CancellationToken Lifetime => cancel.Token;
    public void Dispose() => dispose();
}
