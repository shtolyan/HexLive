using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Launcher;

public static class LauncherPaths
{
    public const string LatestUrl = "https://vmi3529459.contaboserver.net/api/releases/v1/windows/latest";
    public static string DefaultInstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HexLive");
    public static string CacheRoot => Path.Combine(LocalLow(), "JuicyLove", "HexLive", "HexLiveAssetCache");
    public static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JuicyLove", "HexLive", "launcher", "player-token.txt");

    private static string LocalLow()
    {
        var id = new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) == 0)
        {
            try { return Marshal.PtrToStringUni(pointer)!; }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

public sealed record InstallProgress(string Stage, long CompletedBytes, long TotalBytes, long BytesPerSecond)
{
    public double Fraction => TotalBytes <= 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

public sealed class RemoteRelease
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("playerRelease")] public PlayerRelease PlayerRelease { get; set; } = new();
}

public sealed class PlayerRelease
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("archiveSha256")] public string ArchiveSha256 { get; set; } = "";
    [JsonPropertyName("archiveSize")] public long ArchiveSize { get; set; }
    [JsonPropertyName("archiveFileName")] public string ArchiveFileName { get; set; } = "";
    [JsonPropertyName("executable")] public string Executable { get; set; } = "";
    [JsonPropertyName("runtimeProfile")] public string RuntimeProfile { get; set; } = "";
    [JsonPropertyName("assetApi")] public string AssetApi { get; set; } = "";
    [JsonPropertyName("gameServer")] public string GameServer { get; set; } = "";
}

public sealed class InstallState
{
    public string Version { get; set; } = "";
    public string Executable { get; set; } = "";
    public string GameServer { get; set; } = "";
    public string ArchiveSha256 { get; set; } = "";
}

internal sealed class ContentIndex
{
    public long RegistryRevision { get; set; }
    public bool IsDelta { get; set; }
    public List<ContentRecord> Objects { get; set; } = [];
    public List<ContentKey> PlatformMissing { get; set; } = [];
}
internal sealed class ContentKey { public string Type { get; set; } = ""; public string Id { get; set; } = ""; }
internal sealed class ContentRecord
{
    public string Type { get; set; } = ""; public string Id { get; set; } = "";
    public long Revision { get; set; } public string State { get; set; } = "active";
    public JsonElement Metadata { get; set; } public ContentVariant? Variant { get; set; }
}
internal sealed class ContentVariant
{
    public string Platform { get; set; } = ""; public string RuntimeProfile { get; set; } = "";
    public string Sha256 { get; set; } = ""; public long Size { get; set; }
    public string PayloadType { get; set; } = "assetBundle"; public string EntryAsset { get; set; } = "main";
    public string? IconAsset { get; set; } public List<ContentAttachment> Attachments { get; set; } = [];
}
internal sealed class ContentAttachment { public string Name { get; set; } = ""; public string Sha256 { get; set; } = ""; public long Size { get; set; } }
internal sealed record BlobNeed(string Sha256, long Size);
internal sealed class VerifiedFile { public List<VerifiedStamp> Stamps { get; set; } = []; }
internal sealed class VerifiedStamp { public string Sha256 { get; set; } = ""; public long Size { get; set; } public long MtimeTicks { get; set; } }

public sealed class LauncherService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HttpClient _http;
    public LauncherService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { MaxConnectionsPerServer = 6 })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool NeedsUpdate(InstallState? installed, RemoteRelease release) =>
        installed == null || installed.Version != release.Version ||
        !string.Equals(installed.ArchiveSha256, release.PlayerRelease.ArchiveSha256, StringComparison.OrdinalIgnoreCase);

    public static void RequireProtocol(RemoteRelease release, int required)
    {
        if (required > 0 && release.PlayerRelease.ProtocolVersion != required)
            throw new InvalidDataException("Совместимое обновление ещё не опубликовано. Повторите проверку позже.");
    }

    public async Task<RemoteRelease> GetLatestAsync(CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var value = await _http.GetFromJsonAsync<RemoteRelease>(LauncherPaths.LatestUrl, Json, timeout.Token);
        if (value is null || value.PlayerRelease.ArchiveSha256.Length != 64 || value.PlayerRelease.ArchiveSize <= 0)
            throw new InvalidDataException("Сервер вернул повреждённый Windows release manifest.");
        return value;
    }

    public static InstallState? ReadInstallState(string root)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(root), "install-state.json");
            var state = File.Exists(path) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), Json) : null;
            return state is not null && File.Exists(state.Executable) ? state : null;
        }
        catch { return null; }
    }

    public async Task<InstallState> InstallAsync(RemoteRelease release, string installRoot,
        IProgress<InstallProgress> progress, CancellationToken cancel = default)
    {
        installRoot = Path.GetFullPath(installRoot);
        if (Process.GetProcessesByName("HexLive").Length != 0)
            throw new InvalidOperationException("Закройте HexLive перед обновлением.");
        Directory.CreateDirectory(installRoot);
        var stagingRoot = Path.Combine(installRoot, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var archivePart = Path.Combine(stagingRoot, release.PlayerRelease.ArchiveSha256 + ".zip.part");
        var clock = Stopwatch.StartNew(); long completed = 0;
        var progressLock = new object();
        void Report(string stage, long delta, long total)
        {
            lock (progressLock)
            {
                completed += delta;
                progress.Report(new(stage, completed, Math.Max(total, completed),
                    clock.Elapsed.TotalSeconds < .2 ? 0 : (long)(completed / clock.Elapsed.TotalSeconds)));
            }
        }

        var indexResult = await GetContentIndexAsync(release.PlayerRelease, cancel);
        var needs = indexResult.Index.Objects.Where(x => x.Variant is not null)
            .SelectMany(x => new[] { new BlobNeed(x.Variant!.Sha256, x.Variant.Size) }
                .Concat(x.Variant.Attachments.Select(a => new BlobNeed(a.Sha256, a.Size))))
            .GroupBy(x => x.Sha256, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        Report("Проверяем уже скачанные файлы…", 0, 0);
        var stamps = ReadStamps(Path.Combine(LauncherPaths.CacheRoot, "verified-blobs.json"));
        var total = await RemainingDownloadBytesAsync(archivePart, release.PlayerRelease.ArchiveSha256,
            release.PlayerRelease.ArchiveSize, cancel);
        foreach (var need in needs)
        {
            var target = Path.Combine(LauncherPaths.CacheRoot, "blobs", need.Sha256);
            if (await VerifyCachedAsync(target, need, stamps, cancel)) continue;
            total += await RemainingDownloadBytesAsync(Path.Combine(LauncherPaths.CacheRoot, "partial", need.Sha256 + ".part"),
                need.Sha256, need.Size, cancel);
        }
        Report("Скачиваем обновление", 0, total);
        await DownloadVerifiedAsync(ReleaseBlobUrl(release.PlayerRelease.ArchiveSha256), archivePart,
            release.PlayerRelease.ArchiveSha256, release.PlayerRelease.ArchiveSize,
            delta => Report("Скачиваем Player", delta, total), cancel);

        var versions = Path.Combine(installRoot, "versions"); Directory.CreateDirectory(versions);
        // Keep the currently installed version intact until the new state is committed.
        var finalVersion = Path.Combine(versions, SafeSegment(release.Version) + "-" + Guid.NewGuid().ToString("N"));
        var versionStaging = finalVersion + ".staging";
        if (Directory.Exists(versionStaging)) Directory.Delete(versionStaging, true);
        Directory.CreateDirectory(versionStaging);
        ExtractSafe(archivePart, versionStaging);
        var executable = Path.GetFullPath(Path.Combine(versionStaging,
            release.PlayerRelease.Executable.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnder(versionStaging, executable);
        if (!File.Exists(executable)) throw new InvalidDataException("В Player archive нет HexLive.exe.");
        Directory.Move(versionStaging, finalVersion);

        await WarmCacheAsync(release.PlayerRelease, indexResult, needs, stamps, total, Report, cancel);
        var installedLauncher = Path.Combine(installRoot, "HexLiveLauncher.exe");
        var bundledLauncher = Path.Combine(finalVersion,
            Path.GetDirectoryName(release.PlayerRelease.Executable)!, "HexLiveUpdater.exe");
        EnsureUnder(finalVersion, bundledLauncher);
        PromoteLauncher(bundledLauncher, installedLauncher);
        var state = new InstallState
        {
            Version = release.Version,
            Executable = Path.Combine(finalVersion, release.PlayerRelease.Executable.Replace('/', Path.DirectorySeparatorChar)),
            GameServer = release.PlayerRelease.GameServer,
            ArchiveSha256 = release.PlayerRelease.ArchiveSha256
        };
        WriteAtomic(Path.Combine(installRoot, "install-state.json"), JsonSerializer.Serialize(state, Json));
        InstallShellIntegration(installedLauncher, installRoot);
        progress.Report(new("Готово", completed, completed, (long)(completed / Math.Max(.2, clock.Elapsed.TotalSeconds))));
        return state;
    }

    private async Task<(ContentIndex Index, string? ETag)> GetContentIndexAsync(PlayerRelease release, CancellationToken cancel)
    {
        var url = $"{release.AssetApi.TrimEnd('/')}/index/StandaloneWindows64/{Uri.EscapeDataString(release.RuntimeProfile)}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        var index = await JsonSerializer.DeserializeAsync<ContentIndex>(body, Json, cancel) ?? throw new InvalidDataException("Пустой asset index.");
        ValidateRuntimeContent(index);
        return (index, response.Headers.ETag?.ToString());
    }

    internal static void ValidateRuntimeContent(ContentIndex index)
    {
        // §152.7: shared SFX, voices, music and FMOD banks are bundled in Player.
        // Retired audio records can remain in older registries; they are not downloadable runtime content.
        index.Objects.RemoveAll(o => o.Type == "audio");
        index.PlatformMissing.RemoveAll(o => o.Type == "audio");
        if (index.PlatformMissing.Count != 0)
            throw new InvalidDataException($"Windows-контент неполон: отсутствует {index.PlatformMissing.Count} объектов.");
    }

    private async Task WarmCacheAsync(PlayerRelease release, (ContentIndex Index, string? ETag) result,
        List<BlobNeed> needs, Dictionary<string, VerifiedStamp> stamps, long total, Action<string, long, long> report, CancellationToken cancel)
    {
        var root = LauncherPaths.CacheRoot; var blobs = Path.Combine(root, "blobs");
        var partial = Path.Combine(root, "partial"); Directory.CreateDirectory(blobs); Directory.CreateDirectory(partial);
        var stampsPath = Path.Combine(root, "verified-blobs.json");
        var gate = new SemaphoreSlim(6);
        await Task.WhenAll(needs.Select(async need =>
        {
            await gate.WaitAsync(cancel);
            try
            {
                var target = Path.Combine(blobs, need.Sha256);
                if (TrustStamp(target, need, stamps)) { report("Проверяем кэш", 0, total); return; }
                if (File.Exists(target) && !new FileInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                    new FileInfo(target).Length == need.Size &&
                    string.Equals(await HashAsync(target, cancel), need.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var verified = new FileInfo(target);
                    lock (stamps) stamps[need.Sha256] = new VerifiedStamp
                        { Sha256 = need.Sha256, Size = verified.Length, MtimeTicks = verified.LastWriteTimeUtc.Ticks };
                    report("Проверяем кэш", 0, total);
                    return;
                }
                var part = Path.Combine(partial, need.Sha256 + ".part");
                await DownloadVerifiedAsync($"{release.AssetApi.TrimEnd('/')}/blobs/{need.Sha256}", part,
                    need.Sha256, need.Size, delta => report("Скачиваем контент", delta, total), cancel);
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
                var info = new FileInfo(target);
                lock (stamps) stamps[need.Sha256] = new VerifiedStamp
                    { Sha256 = need.Sha256, Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks };
            }
            finally { gate.Release(); }
        }));
        WriteAtomic(stampsPath, JsonSerializer.Serialize(new VerifiedFile { Stamps = stamps.Values.ToList() }, Json));
        foreach (var record in result.Index.Objects.Where(x => x.Variant is not null))
        {
            var path = Path.Combine(root, "records", record.Type, record.Id + ".json");
            WriteAtomic(path, JsonSerializer.Serialize(new { current = record }, Json));
        }
        WriteAtomic(Path.Combine(root, "registry-state.json"), JsonSerializer.Serialize(new
        {
            endpoint = release.AssetApi.TrimEnd('/'), registryRevision = result.Index.RegistryRevision,
            etag = result.ETag ?? "", knownRecords = result.Index.Objects.OrderBy(x => x.Type + "/" + x.Id).ToList()
        }, Json));
    }

    internal async Task DownloadVerifiedAsync(string url, string part, string sha, long size,
        Action<long> advanced, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        if (File.Exists(part) && new FileInfo(part).Length == size &&
            string.Equals(await HashAsync(part, cancel), sha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (File.Exists(part) && new FileInfo(part).Length == size) File.Delete(part);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (offset == size)
                {
                    if (string.Equals(await HashAsync(part, cancel), sha, StringComparison.OrdinalIgnoreCase)) return;
                    File.Delete(part); offset = 0;
                }
                if (offset > size) { File.Delete(part); offset = 0; }
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                if (offset > 0 && response.StatusCode == HttpStatusCode.OK) { File.Delete(part); offset = 0; }
                response.EnsureSuccessStatusCode();
                if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent ||
                    response.Content.Headers.ContentRange?.From != offset ||
                    response.Content.Headers.ContentRange?.Length != size))
                    throw new InvalidDataException("Сервер вернул неверный диапазон загрузки.");
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using (var destination = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write, FileShare.None, 1024 * 128, true))
                {
                    var buffer = new byte[1024 * 128]; int read;
                    while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                    {
                        if (destination.Length + read > size) throw new InvalidDataException("Размер загрузки превышает манифест.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancel); advanced(read);
                    }
                    await destination.FlushAsync(cancel);
                }
                if (new FileInfo(part).Length != size || !string.Equals(await HashAsync(part, cancel), sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Скачанный файл не прошёл SHA-256 проверку.");
                return;
            }
            catch when (attempt < 3 && !cancel.IsCancellationRequested) { await Task.Delay(500 * attempt, cancel); }
        }
    }

    public void Launch(InstallState state)
    {
        if (!File.Exists(state.Executable)) throw new FileNotFoundException("HexLive.exe не найден.", state.Executable);
        Process.Start(new ProcessStartInfo(state.Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(state.Executable)!, UseShellExecute = false
        });
    }

    internal static void PromoteLauncher(string source, string target)
    {
        // A running Windows executable can be renamed, but cannot be overwritten.
        // Publish the newly bundled updater, never perpetuate the previous launcher.
        var staging = target + ".new-" + Guid.NewGuid().ToString("N");
        var previous = target + ".previous-" + Guid.NewGuid().ToString("N");
        File.Copy(source, staging);
        var movedPrevious = false;
        try
        {
            if (File.Exists(target)) { File.Move(target, previous); movedPrevious = true; }
            try { File.Move(staging, target); }
            catch
            {
                if (movedPrevious) File.Move(previous, target);
                throw;
            }
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
        if (movedPrevious)
        {
            try { File.Delete(previous); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task UninstallAsync(string root)
    {
        RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HexLive.lnk"));
        RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "HexLive.lnk"));
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\HexLive", false);
        if (File.Exists(LauncherPaths.TokenPath)) File.Delete(LauncherPaths.TokenPath);
        var full = Path.GetFullPath(root);
        return Task.Run(() =>
        {
            Thread.Sleep(700);
            foreach (var path in Directory.GetFileSystemEntries(full))
            {
                if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
            }
            if (string.Equals(Path.GetDirectoryName(Environment.ProcessPath), full, StringComparison.OrdinalIgnoreCase))
                MoveFileEx(Environment.ProcessPath!, null, 4);
        });
    }

    internal static void ExtractSafe(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            EnsureUnder(destination, path);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, true);
        }
    }

    internal static void EnsureUnder(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP содержит небезопасный путь.");
    }
    private static string SafeSegment(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string ReleaseBlobUrl(string sha) => $"https://vmi3529459.contaboserver.net/api/releases/v1/windows/blobs/{sha}";
    private static async Task<string> HashAsync(string path, CancellationToken cancel)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, cancel)).ToLowerInvariant(); }
    private static Dictionary<string, VerifiedStamp> ReadStamps(string path)
    {
        try { return (JsonSerializer.Deserialize<VerifiedFile>(File.ReadAllText(path), Json)?.Stamps ?? []).ToDictionary(x => x.Sha256, StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
    private static bool TrustStamp(string path, BlobNeed need, Dictionary<string, VerifiedStamp> stamps)
    {
        VerifiedStamp? stamp;
        lock (stamps) stamps.TryGetValue(need.Sha256, out stamp);
        if (!File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint) || stamp is null) return false;
        var info = new FileInfo(path); return info.Length == need.Size && stamp.Size == info.Length && stamp.MtimeTicks == info.LastWriteTimeUtc.Ticks;
    }

    internal static async Task<bool> VerifyCachedAsync(string path, BlobNeed need,
        Dictionary<string, VerifiedStamp> stamps, CancellationToken cancel)
    {
        if (TrustStamp(path, need, stamps)) return true;
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != need.Size ||
            !string.Equals(await HashAsync(path, cancel), need.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        lock (stamps) stamps[need.Sha256] = new VerifiedStamp
            { Sha256 = need.Sha256, Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks };
        return true;
    }

    internal static async Task<long> RemainingDownloadBytesAsync(string partial, string sha, long size, CancellationToken cancel)
    {
        var info = new FileInfo(partial);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > size) return size;
        if (info.Length < size) return size - info.Length;
        return string.Equals(await HashAsync(partial, cancel), sha, StringComparison.OrdinalIgnoreCase) ? 0 : size;
    }
    private static void WriteAtomic(string path, string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp"; File.WriteAllText(temp, text); File.Move(temp, path, true); }

    private static void InstallShellIntegration(string launcher, string root)
    {
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HexLive.lnk"), launcher);
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "HexLive.lnk"), launcher);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\HexLive");
        key.SetValue("DisplayName", "HexLive"); key.SetValue("DisplayIcon", launcher);
        key.SetValue("InstallLocation", root); key.SetValue("UninstallString", $"\"{launcher}\" --uninstall \"{root}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
    private static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host недоступен.");
        dynamic shell = Activator.CreateInstance(shellType)!; dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target; shortcut.WorkingDirectory = Path.GetDirectoryName(target); shortcut.IconLocation = target; shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
    }
    private static void RemoveShortcut(string path) { if (File.Exists(path)) File.Delete(path); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? replacement, int flags);
}
