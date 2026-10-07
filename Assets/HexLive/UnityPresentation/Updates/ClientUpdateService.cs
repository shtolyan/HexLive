using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.Updates;
using UnityEngine;

namespace HexLive.UnityPresentation.Updates
{
    // §166: persistent service; all async entry points are called on Unity's main thread.
    public sealed class ClientUpdateService : MonoBehaviour
    {
        public static ClientUpdateService Instance { get; private set; }
        public static bool BlocksEntry => Instance != null && Instance.Required;
        public bool Required { get; private set; }
        public bool Busy { get; private set; }
        public bool Ready { get; private set; }
        public string FailedRelease { get; private set; }
        public bool Configured => _config != null;
        public ReleaseManifest Release { get; private set; }
        public float Progress { get; private set; }
        public string StatusKey { get; private set; } = "update.check";
        public event Action Changed;
        private UpdateConfig _config;
        private string _envelope, _cache, _package, _install;
        private float _nextCheck;
        private CancellationTokenSource _cancel;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        private string _etag;
        private bool _universal;
        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("ClientUpdates");
            DontDestroyOnLoad(go);
            go.AddComponent<ClientUpdateService>();
            go.AddComponent<ClientUpdatePanel>();
        }
        private void Awake()
        {
            Instance = this;
            _cache = Path.Combine(Application.persistentDataPath, "client-updates");
            Directory.CreateDirectory(_cache);
            _package = Path.Combine(Application.streamingAssetsPath, "HexLiveUpdate");
            try
            {
                var path = Path.Combine(_package, "update-config.json");
                if (!File.Exists(path) || Application.isEditor) { StatusKey = "update.unconfigured"; return; }
                _config = JsonUtility.FromJson<UpdateConfig>(File.ReadAllText(path));
                _universal = _config.architecture == "universal";
                if (_universal)
                {
                    _config.architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                    _config.endpoint = _config.endpoint.Replace("/universal/", "/" + _config.architecture + "/");
                }
                if (!Uri.TryCreate(_config.endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                    throw new InvalidDataException("HTTPS update endpoint required");
                _install = InstallPath();
                var failed = Path.Combine(Path.GetDirectoryName(_install), ".hexlive-updates", "failed-release.txt");
                if (File.Exists(failed)) FailedRelease = File.ReadAllText(failed).Trim();
                if (File.Exists(Path.Combine(_cache, "latest.json"))) Accept(File.ReadAllText(Path.Combine(_cache, "latest.json")));
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[Updates] " + ex.Message); StatusKey = "update.error"; }
        }
        private void Update()
        {
            if (_config != null && !Busy && Time.realtimeSinceStartup >= _nextCheck)
            { _nextCheck = Time.realtimeSinceStartup + 1800; _ = Check(); }
        }
        public async Task Check()
        {
            if (!Configured || Busy) return;
            Busy = true; StatusKey = "update.checking"; Notify();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, _config.endpoint + "/latest");
                if (_etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.NotModified)
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 200000) throw new InvalidDataException("Oversized update metadata");
                    using var body = await response.Content.ReadAsStreamAsync();
                    using var bytes = new MemoryStream();
                    var chunk = new byte[8192]; int read;
                    while ((read = await body.ReadAsync(chunk, 0, chunk.Length, timeout.Token)) > 0)
                    { if (bytes.Length + read > 200000) throw new InvalidDataException("Oversized update metadata"); bytes.Write(chunk, 0, read); }
                    var json = Encoding.UTF8.GetString(bytes.ToArray());
                    Accept(json);
                    Atomic(Path.Combine(_cache, "latest.json"), json);
                    _etag = response.Headers.ETag?.ToString();
                }
                StatusKey = Release == null ? "update.current" : Release.sha256 == FailedRelease ? "update.error" : Required ? "update.required" : "update.available";
            }
            catch (Exception ex) { Error(ex); }
            finally { Busy = false; Notify(); }
        }
        private void Accept(string json)
        {
            var envelope = JsonUtility.FromJson<ReleaseEnvelope>(json);
            var release = JsonUtility.FromJson<ReleaseManifest>(Encoding.UTF8.GetString(ReleaseTrust.Verify(envelope, _config)));
            release.Validate(_config.platform, _config.architecture);
            // A stale cache/feed must not silently remove an already verified minimum.
            Required |= ReleaseManifest.ParseVersion(Application.version) < ReleaseManifest.ParseVersion(release.minimumVersion);
            if (Release != null && (ReleaseManifest.ParseVersion(release.version) < ReleaseManifest.ParseVersion(Release.version) ||
                release.version == Release.version && release.sha256 != Release.sha256)) throw new InvalidDataException("Release downgrade or replacement");
            if (ReleaseManifest.ParseVersion(release.version) <= ReleaseManifest.ParseVersion(Application.version)) return;
            if (Release?.sha256 != release.sha256) Ready = false;
            Release = release; _envelope = json;
        }
        public async Task Download()
        {
            if (Busy || Release == null) return;
            Busy = true; Ready = false; _cancel = new CancellationTokenSource();
            StatusKey = "update.downloading"; Notify();
            var release = Release;
            var path = Path.Combine(_cache, release.sha256 + ".part");
            try
            {
                var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (offset > release.size) { File.Delete(path); offset = 0; }
                var free = new DriveInfo(Path.GetPathRoot(_cache)).AvailableFreeSpace;
                if (free < release.size - offset + 64L * 1024 * 1024) throw new IOException("Not enough disk space");
                if (offset < release.size)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _config.endpoint + "/blobs/" + release.sha256);
                    if (offset > 0) { request.Headers.Range = new RangeHeaderValue(offset, null); request.Headers.TryAddWithoutValidation("If-Range", "\"" + release.sha256 + "\""); }
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancel.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range == null || range.From != offset || range.Length != release.size) throw new InvalidDataException("Invalid download range");
                    }
                    else offset = 0;
                    using var output = new FileStream(path, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                    using var input = await response.Content.ReadAsStreamAsync();
                    var buffer = new byte[128 * 1024]; int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, _cancel.Token)) > 0)
                    {
                        offset += read;
                        if (offset > release.size) throw new InvalidDataException("Oversized archive");
                        await output.WriteAsync(buffer, 0, read, _cancel.Token);
                        Progress = (float)((double)offset / release.size); Notify();
                    }
                    output.Flush(true);
                }
                try { await Task.Run(() => ReleaseTrust.VerifyArchive(path, release), _cancel.Token); }
                catch (InvalidDataException) { File.Delete(path); throw; }
                Ready = true; Progress = 1; StatusKey = "update.ready";
            }
            catch (OperationCanceledException) { StatusKey = "update.cancelled"; }
            catch (Exception ex) { Error(ex); }
            finally { Busy = false; _cancel.Dispose(); _cancel = null; Notify(); }
        }
        public void Cancel() => _cancel?.Cancel();
        public async Task Install()
        {
            if (Busy || !Ready || Release == null) return;
            Busy = true; StatusKey = "update.preparing"; Notify();
            Process helper = null;
            try
            {
                // Verify writability before closing the game; installation never requests elevation.
                var probe = Path.Combine(Path.GetDirectoryName(_install), ".hexlive-write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, ""); File.Delete(probe);
                var temp = Path.Combine(_cache, "install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
                var name = _config.platform == "windows" ? "HexLive.Updater.exe" : "HexLive.Updater";
                var executable = Path.Combine(temp, name); File.Copy(Path.Combine(_package, name + (_universal ? "-" + _config.architecture : "")), executable);
                if (_config.platform == "macos")
                {
                    var chmod = Process.Start(new ProcessStartInfo("/bin/chmod", "+x " + Quote(executable)) { UseShellExecute = false });
                    chmod.WaitForExit(); if (chmod.ExitCode != 0) throw new IOException("Cannot prepare updater");
                }
                var current = Process.GetCurrentProcess();
                var request = new Request {
                    envelope = _envelope, archive = Path.Combine(_cache, Release.sha256 + ".part"), installPath = _install,
                    parentPid = current.Id, parentStartTicks = current.StartTime.ToUniversalTime().Ticks,
                    arguments = RestartArguments() };
                var requestPath = Path.Combine(temp, "request.json"); Atomic(requestPath, JsonUtility.ToJson(request));
                if (_config.platform == "macos")
                {
                    using var protect = Process.Start(new ProcessStartInfo("/bin/chmod", "600 " + Quote(requestPath)) { UseShellExecute = false });
                    protect.WaitForExit(); if (protect.ExitCode != 0) throw new IOException("Cannot protect restart arguments");
                }
                helper = Process.Start(new ProcessStartInfo(executable, Quote(requestPath)) { UseShellExecute = false, CreateNoWindow = true });
                var until = DateTime.UtcNow.AddMinutes(5);
                while (!File.Exists(requestPath + ".ready"))
                {
                    if (helper == null || helper.HasExited || DateTime.UtcNow > until) throw new IOException("Updater preparation failed; see updater.log");
                    await Task.Delay(200);
                }
                var runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
                if (runner != null) runner.WriteSaveNow();
                PlayerPrefs.Save(); Application.Quit();
            }
            catch (Exception ex)
            {
                if (helper != null && !helper.HasExited) helper.Kill();
                Error(ex); Busy = false; Notify();
            }
        }
        [Serializable] private sealed class Request
        {
            public string envelope, archive, installPath;
            public int parentPid; public long parentStartTicks; public string[] arguments;
        }
        private static string Quote(string value) => "\"" +
            System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"") + "\"";

        private static string[] RestartArguments()
        {
            var result = new System.Collections.Generic.List<string>(); var args = System.Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
            { if (args[i] == "-hexlive-update-ack" || args[i] == "-hexlive-install-path") { i++; continue; } result.Add(args[i]); }
            return result.ToArray();
        }
        private static string InstallPath()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (var i = 1; i + 1 < args.Length; i++) if (args[i] == "-hexlive-install-path") return Path.GetFullPath(args[i + 1]);
            var data = new DirectoryInfo(Application.dataPath);
            var app = Application.platform == RuntimePlatform.OSXPlayer ? data.Parent.FullName : data.Parent.FullName;
            // Existing scripted macOS distribution: Releases/vN/HexLive.app -> sibling latest.
            if (Application.platform == RuntimePlatform.OSXPlayer && data.Parent.Parent.Parent?.Name == "Releases")
                return Path.Combine(data.Parent.Parent.Parent.Parent.FullName, "HexLive.app");
            var managed = new DirectoryInfo(app).Parent?.Parent;
            if (managed != null && managed.Name == ".hexlive-updates")
                return Path.Combine(managed.Parent.FullName, Application.platform == RuntimePlatform.OSXPlayer ? "HexLive.app" : "HexLive");
            return app;
        }
        public static void AcknowledgeMenu()
        {
            if (Application.isEditor) return;
            var args = System.Environment.GetCommandLineArgs();
            for (var i = 1; i + 1 < args.Length; i++)
                if (args[i] == "-hexlive-update-ack")
                {
                    var path = Path.GetFullPath(args[i + 1]);
                    if (Path.GetFileName(path).StartsWith("ack-") && new DirectoryInfo(Path.GetDirectoryName(path)).Name == ".hexlive-updates")
                        File.WriteAllText(path, Application.version);
                }
        }
        private static void Atomic(string path, string text)
        {
            File.WriteAllText(path + ".tmp", text);
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
        }
        private void Notify() => Changed?.Invoke();
        private void Error(Exception ex) { UnityEngine.Debug.LogWarning("[Updates] " + ex.Message); StatusKey = ex is UnauthorizedAccessException ? "update.permissions" : "update.error"; }
        private void OnDestroy() { _cancel?.Cancel(); _http.Dispose(); if (Instance == this) Instance = null; }
    }
}
