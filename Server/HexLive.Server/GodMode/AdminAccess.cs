using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.Server.GodMode;

/// <summary>Individual, revocable admin grants. A caller-supplied client id is never a credential.</summary>
public sealed class AdminAccess
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, string> _grants;
    private readonly Dictionary<string, (string Client, DateTimeOffset Until)> _codes = new();
    public AdminAccess(string path)
    {
        _path = path;
        _grants = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid admin grants") : new();
    }
    public string Issue(string client)
    {
        if (!PlayerCharacterAssignments.TryNormalizePlayerId(client, out var normalized)) throw new ArgumentException("Invalid client id");
        lock (_gate)
        {
            foreach (var old in _codes.Where(p => p.Value.Until < DateTimeOffset.UtcNow || p.Value.Client == normalized).ToArray()) _codes.Remove(old.Key);
            if (_codes.Count >= 32) throw new InvalidOperationException("Too many pending bindings");
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _codes[Hash(code)] = (normalized, DateTimeOffset.UtcNow.AddMinutes(5));
            return code;
        }
    }
    public string? Exchange(string client, string code)
    {
        lock (_gate)
        {
            var key = Hash(code);
            if (!_codes.TryGetValue(key, out var pending) || pending.Client != client || pending.Until < DateTimeOffset.UtcNow) return null;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var next = new Dictionary<string, string>(_grants) { [client] = Hash(token) };
            Store(next); _grants[client] = next[client]; _codes.Remove(key); return token;
        }
    }
    public bool Authorized(string client, string token)
    {
        lock (_gate) return _grants.TryGetValue(client, out var expected) &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(Hash(token)));
    }
    public T WithAuthorization<T>(string client, string token, Func<T> action, Func<T> denied)
    {
        lock (_gate) return Authorized(client, token) ? action() : denied();
    }
    public string[] Clients { get { lock (_gate) return _grants.Keys.OrderBy(x => x).ToArray(); } }
    public void Revoke(string client)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, string>(_grants); next.Remove(client); Store(next); _grants.Remove(client);
            foreach (var code in _codes.Where(p => p.Value.Client == client).ToArray()) _codes.Remove(code.Key);
        }
    }
    private void Store(Dictionary<string,string> grants) => WritePrivate(_path, JsonSerializer.Serialize(grants));
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
