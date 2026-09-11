using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HexLive.Server.Releases;

/// <summary>§156 persistent immutable Player archives and the atomic latest pointer.</summary>
public sealed class PlayerReleaseStore
{
    private readonly string _root;
    private readonly string _blobs;
    private readonly string _latest;

    public PlayerReleaseStore(string root, string platform = "windows")
    {
        if (platform is not ("windows" or "macos")) throw new ArgumentException("unsupported Player platform", nameof(platform));
        _root = Path.Combine(Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))), platform);
        _blobs = Path.Combine(_root, "blobs");
        _latest = Path.Combine(_root, "latest.json");
        Directory.CreateDirectory(_blobs);
    }

    public string RootPath => _root;

    public JsonDocument? ReadLatest()
    {
        if (!File.Exists(_latest)) return null;
        return JsonDocument.Parse(File.ReadAllBytes(_latest));
    }

    public bool TryGetBlob(string sha256, out string path, out long size)
    {
        path = string.Empty;
        size = 0;
        if (!IsSha256(sha256)) return false;
        var candidate = Path.Combine(_blobs, sha256.ToLowerInvariant());
        if (!File.Exists(candidate)) return false;
        var info = new FileInfo(candidate);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return false;
        path = candidate;
        size = info.Length;
        return true;
    }

    public bool TryGetInstaller(out string path, out long size)
    {
        path = Path.Combine(_root, "HexLiveInstaller.exe");
        size = 0;
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        size = info.Length;
        return true;
    }

    public static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
