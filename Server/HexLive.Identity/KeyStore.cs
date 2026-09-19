using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.Identity;

public sealed class KeyStore
{
    public static readonly string[] Permissions = ["game.play", "bugs.create", "bugs.read", "bugs.manage", "server.admin", "keys.manage"];
    public sealed record Account(string Id, string Name, string KeyHash, bool Revoked, DateTimeOffset Created,
        string[]? Permissions = null, long Revision = 1, string SubjectType = "player");
    public sealed record Audit(DateTimeOffset At, string Actor, string AccountId, string Action, string[] Before, string[] After);
    public sealed record State(List<Account> Accounts, List<Audit> Audit);
    public sealed class ConflictException : Exception { public ConflictException() : base("Account changed; reload and retry") {} }
    private readonly string _path;
    private readonly object _gate = new();
    private State _state;
    public KeyStore(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path)) { _state = new([], []); return; }
        var json = File.ReadAllText(_path);
        _state = json.TrimStart().StartsWith("[")
            ? new(JsonSerializer.Deserialize<List<Account>>(json)!, [])
            : JsonSerializer.Deserialize<State>(json) ?? throw new InvalidDataException("Invalid identity store");
        _state = _state with { Accounts = _state.Accounts.Select(a => a with { Permissions = a.Permissions ?? ["game.play"] }).ToList() };
    }
    public Account[] List() { lock (_gate) return _state.Accounts.Select(Copy).ToArray(); }
    public Audit[] History() { lock (_gate) return _state.Audit.ToArray(); }
    private static Account Copy(Account a) => a with { Permissions = a.Permissions?.ToArray() ?? [] };
    private static string[] Validate(string[] values)
    {
        if (values.Any(p => !Permissions.Contains(p))) throw new ArgumentException("Unknown permission");
        return values.Distinct().Order().ToArray();
    }
    public (Account Account, string Key) Issue(string name, string? existingId = null,
        string[]? permissions = null, long? expectedRevision = null, string actor = "operator", string subjectType = "player", string? bootstrapId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || subjectType is not ("player" or "agent")) throw new ArgumentException("Invalid account");
        lock (_gate)
        {
            if (bootstrapId != null && (_state.Accounts.Count != 0 || !Guid.TryParseExact(bootstrapId, "N", out _)))
                throw new ArgumentException("An imported bootstrap identity requires an empty registry and canonical account id");
            var existing = existingId == null ? null : _state.Accounts.SingleOrDefault(a => a.Id == existingId) ?? throw new ArgumentException("Account does not exist");
            if (expectedRevision.HasValue && existing?.Revision != expectedRevision) throw new ConflictException();
            var key = "hexlive_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var account = new Account(existing?.Id ?? bootstrapId ?? Guid.NewGuid().ToString("N"), name.Trim(), Hash(key), false,
                existing?.Created ?? DateTimeOffset.UtcNow, Validate(permissions ?? existing?.Permissions ?? ["game.play"]),
                (existing?.Revision ?? 0) + 1, existing?.SubjectType ?? subjectType);
            Commit(account, actor, existing == null ? "issue" : "rotate");
            return (Copy(account), key);
        }
    }
    public Account? Authenticate(string key) => key.Length == 72 && key.StartsWith("hexlive_", StringComparison.Ordinal) ? AuthenticateHash(Hash(key)) : null;
    public Account? AuthenticateHash(string hash)
    {
        lock (_gate) { var a = _state.Accounts.FirstOrDefault(a => !a.Revoked && a.KeyHash == hash); return a == null ? null : Copy(a); }
    }
    public string? Resolve(string key) => Authenticate(key)?.Id;
    public Account SetPermissions(string id, string[] permissions, long expectedRevision, string actor)
    {
        lock (_gate)
        {
            var a = _state.Accounts.SingleOrDefault(a => a.Id == id) ?? throw new ArgumentException("Unknown account");
            if (a.Revision != expectedRevision) throw new ConflictException();
            var next = a with { Permissions = Validate(permissions), Revision = a.Revision + 1 };
            Commit(next, actor, "permissions"); return Copy(next);
        }
    }
    public bool Revoke(string id, long? expectedRevision = null, string actor = "operator")
    {
        lock (_gate)
        {
            var a = _state.Accounts.SingleOrDefault(a => a.Id == id);
            if (a == null) return false;
            if (expectedRevision.HasValue && a.Revision != expectedRevision) throw new ConflictException();
            Commit(a with { Revoked = true, Revision = a.Revision + 1 }, actor, "revoke"); return true;
        }
    }
    private void Commit(Account account, string actor, string action)
    {
        var before = _state.Accounts.SingleOrDefault(a => a.Id == account.Id);
        var accounts = _state.Accounts.Where(a => a.Id != account.Id).Append(account).ToList();
        bool Owner(Account a) => !a.Revoked && a.Permissions!.Contains("keys.manage");
        if (_state.Accounts.Any(Owner) && !accounts.Any(Owner)) throw new ArgumentException("Cannot remove the last key manager");
        var next = new State(accounts, _state.Audit.Append(new Audit(DateTimeOffset.UtcNow, actor, account.Id, action, before?.Permissions ?? [], account.Revoked ? [] : account.Permissions!)).ToList());
        var temporary = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options)) { JsonSerializer.Serialize(stream, next); stream.Flush(true); }
        File.Move(temporary, _path, true); _state = next;
    }
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
