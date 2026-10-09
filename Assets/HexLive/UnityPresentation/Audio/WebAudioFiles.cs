#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// §168.6: the Player audio tree (<c>StreamingAssets/HexLive/Sfx/**</c>,
    /// <c>Music</c>) in a browser. There it is a URL, not a folder: nothing can
    /// enumerate it and FMOD cannot open a file by URL. So the web build carries
    /// a manifest written at build time (<c>web-audio-manifest.json</c>), and
    /// each file is fetched on first need into the page's in-memory file system
    /// under <see cref="LocalRoot"/>. From then on <c>FmodSfx</c> and the lipsync
    /// reader see an ordinary file path — the same code as on desktop, only the
    /// root differs.
    /// <para>
    /// Nothing is fetched up front: ~200 MB of voices would never fit the tab.
    /// Each group is fetched when it is first asked for; that first request
    /// stays silent and the next one plays.
    /// </para>
    /// </summary>
    public static class WebAudioFiles
    {
        public const string ManifestName = "web-audio-manifest.json";
        private const int MaxConcurrentDownloads = 6;

        [Serializable]
        private sealed class Manifest
        {
            public string[] files = Array.Empty<string>();
        }

        private static readonly List<string> Entries = new();
        private static readonly HashSet<string> EntrySet = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Present = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Action<bool>>> Pending = new(StringComparer.Ordinal);
        private static readonly Queue<string> Queue = new();
        private static readonly List<Action> ReadyWaiters = new();
        private static int _active;
        private static bool _manifestRequested;

        /// <summary>True once the manifest is known (always false off the web).</summary>
        public static bool Ready { get; private set; }

        /// <summary>Where fetched files live — MEMFS inside the tab.</summary>
        public static string LocalRoot => Path.Combine(Application.temporaryCachePath, "HexLiveAudio");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Entries.Clear();
            EntrySet.Clear();
            Present.Clear();
            Pending.Clear();
            Queue.Clear();
            ReadyWaiters.Clear();
            _active = 0;
            _manifestRequested = false;
            Ready = false;
        }

        /// <summary>Run <paramref name="action"/> once the manifest is loaded
        /// (immediately if it already is). Off the web it never runs.</summary>
        public static void WhenReady(Action action)
        {
            if (Ready)
            {
                action();
                return;
            }

            ReadyWaiters.Add(action);
            RequestManifest();
        }

        /// <summary>Local paths of every manifest file under <paramref name="relativeDirectory"/>
        /// ("Sfx", "Sfx/VoicesLoc/ru", "Music"), downloaded or not.</summary>
        public static List<string> List(string relativeDirectory, bool recursive)
        {
            var prefix = relativeDirectory.TrimEnd('/') + "/";
            var result = new List<string>();
            foreach (var entry in Entries)
            {
                if (!entry.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!recursive && entry.IndexOf('/', prefix.Length) >= 0)
                {
                    continue;
                }
                result.Add(ToLocal(entry));
            }
            return result;
        }

        public static bool IsLocal(string localPath) => Present.Contains(localPath);

        /// <summary>
        /// Make every path local, then call <paramref name="done"/> on the main
        /// thread with true when all of them arrived. Paths not in the manifest
        /// count as failures.
        /// </summary>
        public static void EnsureLocal(IReadOnlyList<string> localPaths, Action<bool> done)
        {
            var remaining = 0;
            var allOk = true;
            foreach (var path in localPaths)
            {
                if (Present.Contains(path))
                {
                    continue;
                }

                remaining++;
                Enqueue(path, ok =>
                {
                    allOk &= ok;
                    if (--remaining == 0)
                    {
                        done(allOk);
                    }
                });
            }

            if (remaining == 0)
            {
                done(allOk);
            }
        }

        private static string ToLocal(string relative) =>
            Path.Combine(LocalRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        private static string ToRelative(string localPath) =>
            localPath.Substring(LocalRoot.Length + 1).Replace(Path.DirectorySeparatorChar, '/');

        private static void Enqueue(string localPath, Action<bool> done)
        {
            if (Pending.TryGetValue(localPath, out var waiters))
            {
                waiters.Add(done);
                return;
            }

            Pending[localPath] = new List<Action<bool>> { done };
            Queue.Enqueue(localPath);
            Pump();
        }

        private static void Finish(string localPath, bool ok)
        {
            if (ok)
            {
                Present.Add(localPath);
            }
            if (Pending.Remove(localPath, out var waiters))
            {
                foreach (var waiter in waiters)
                {
                    waiter(ok);
                }
            }
        }

#if UNITY_WEBGL && !UNITY_EDITOR
#pragma warning disable RS0030 // §168.6: the one sanctioned reader of the StreamingAssets URL
        private static string BaseUrl => Application.streamingAssetsPath + "/HexLive/";
#pragma warning restore RS0030

        private static string UrlOf(string relative)
        {
            var parts = relative.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = Uri.EscapeDataString(parts[i]);
            }
            return BaseUrl + string.Join("/", parts);
        }

        private static void RequestManifest()
        {
            if (_manifestRequested)
            {
                return;
            }

            _manifestRequested = true;
            var request = UnityWebRequest.Get(BaseUrl + ManifestName);
            request.SendWebRequest().completed += _ =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var manifest = JsonUtility.FromJson<Manifest>(request.downloadHandler.text);
                    Entries.AddRange(manifest?.files ?? Array.Empty<string>());
                    EntrySet.UnionWith(Entries);
                    Debug.Log($"[WebAudio] manifest: {Entries.Count} files");
                }
                else
                {
                    Debug.LogWarning($"[WebAudio] no audio manifest ({request.error}) — the build is silent");
                }
                request.Dispose();

                Ready = true;
                var waiters = ReadyWaiters.ToArray();
                ReadyWaiters.Clear();
                foreach (var waiter in waiters)
                {
                    waiter();
                }
            };
        }

        private static void Pump()
        {
            while (_active < MaxConcurrentDownloads && Queue.Count > 0)
            {
                var localPath = Queue.Dequeue();
                var relative = ToRelative(localPath);
                if (!EntrySet.Contains(relative))
                {
                    Finish(localPath, false);
                    continue;
                }

                _active++;
                var request = UnityWebRequest.Get(UrlOf(relative));
                request.SendWebRequest().completed += _ =>
                {
                    _active--;
                    var ok = false;
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                            File.WriteAllBytes(localPath, request.downloadHandler.data);
                            ok = true;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[WebAudio] cannot store {relative}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[WebAudio] {relative}: {request.error}");
                    }
                    request.Dispose();
                    Finish(localPath, ok);
                    Pump();
                };
            }
        }
#else
        private static void RequestManifest()
        {
        }

        private static void Pump()
        {
            // Desktop reads StreamingAssets straight from disk; nothing is queued.
            while (Queue.Count > 0)
            {
                Finish(Queue.Dequeue(), false);
            }
        }
#endif
    }
}
