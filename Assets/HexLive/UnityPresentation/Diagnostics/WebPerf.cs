using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using Debug = UnityEngine.Debug;

namespace HexLive.UnityPresentation.Diagnostics
{
    /// <summary>
    /// §168.11: a frame profiler that works in a RELEASE web player, where the
    /// Unity Profiler cannot attach and profiler markers are stripped. Off by
    /// default; on with <c>?perf=1</c> in the page URL (or <c>-hexlive-perf</c>
    /// on desktop). Two layers:
    /// <list type="bullet">
    /// <item>every PlayerLoop subsystem is bracketed with a timestamp, so the
    /// frame splits into scripts / animation / skinning / rendering / UI;</item>
    /// <item>the heaviest components wrap their Update/LateUpdate in
    /// <see cref="Begin"/>/<see cref="End"/> — the split INSIDE "scripts".</item>
    /// </list>
    /// Shows a table on screen and logs a summary every 5 s. Frame interval minus
    /// the PlayerLoop is time spent outside Unity (browser, GPU wait).
    /// </summary>
    public sealed class WebPerf : MonoBehaviour
    {
        public const int HexWorldUpdate = 0;
        public const int HexWorldLateUpdate = 1;
        public const int ActorLateUpdate = 2;
        public const int FaceLateUpdate = 3;
        public const int CharacterPanelUpdate = 4;
        public const int TacticalMapUpdate = 5;
        public const int SoundLateUpdate = 6;
        public const int RunnerUpdate = 7;
        public const int RunnerLateUpdate = 8;
        public const int PortraitLateUpdate = 9;
        public const int CameraLateUpdate = 10;

        private static readonly string[] SectionNames =
        {
            "HexWorld.Update", "HexWorld.LateUpdate", "NpcActorView.LateUpdate (all)",
            "NpcFaceAnimator.LateUpdate (all)", "CharacterPanel.Update", "TacticalMap.Update",
            "Sound.LateUpdate", "Runner.Update (net+events)", "Runner.LateUpdate",
            "PortraitStage.LateUpdate", "RtsCamera.LateUpdate",
        };

        public static bool Enabled { get; private set; }

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly long[] SectionTicks = new long[SectionNames.Length];
        private static readonly int[] SectionCalls = new int[SectionNames.Length];

        private static readonly List<string> LoopNames = new();
        private static long[] _loopTicks = System.Array.Empty<long>();
        private static long _lastMark;
        private static int _lastIndex = -1;

        private readonly Dictionary<string, double> _windowMs = new();
        private readonly Dictionary<string, double> _windowMax = new();
        private ProfilerRecorder _drawCalls, _batches, _setPass, _triangles;
        private double _frameMsSum, _frameMsMax, _loopMsSum;
        private int _frames;
        private float _windowStart, _lastLog;
        private string _text = "WebPerf: collecting…";
        private GUIStyle _style;

        public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

        public static void End(int section, long start)
        {
            if (start == 0) return;
            SectionTicks[section] += Stopwatch.GetTimestamp() - start;
            SectionCalls[section]++;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var url = Application.absoluteURL ?? string.Empty;
            var requested = url.Contains("perf=1") ||
                            System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-hexlive-perf") >= 0;
            if (!requested || Enabled) return;
            Enabled = true;
            InstrumentPlayerLoop();
            var go = new GameObject("WebPerf");
            DontDestroyOnLoad(go);
            go.AddComponent<WebPerf>();
            Debug.Log("[WebPerf] enabled");
        }

        private static void InstrumentPlayerLoop()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var phases = root.subSystemList;
            for (var p = 0; p < phases.Length; p++)
            {
                var subs = phases[p].subSystemList;
                if (subs == null) continue;
                var wrapped = new List<PlayerLoopSystem>(subs.Length * 2 + 1);
                foreach (var sub in subs)
                {
                    wrapped.Add(MarkSystem(phases[p].type.Name + "." + sub.type.Name));
                    wrapped.Add(sub);
                }
                // Closes the last subsystem of the phase; the gap until the next
                // phase's first mark is Unity's own glue, charged to "(between)".
                wrapped.Add(MarkSystem(phases[p].type.Name + ".(between)"));
                phases[p].subSystemList = wrapped.ToArray();
            }
            root.subSystemList = phases;
            _loopTicks = new long[LoopNames.Count];
            PlayerLoop.SetPlayerLoop(root);
        }

        private static PlayerLoopSystem MarkSystem(string followingName)
        {
            var index = LoopNames.Count;
            LoopNames.Add(followingName);
            return new PlayerLoopSystem
            {
                type = typeof(WebPerf),
                updateDelegate = () =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (_lastIndex >= 0 && _lastIndex < _loopTicks.Length)
                    {
                        _loopTicks[_lastIndex] += now - _lastMark;
                    }
                    _lastIndex = index;
                    _lastMark = now;
                },
            };
        }

        private void OnEnable()
        {
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _windowStart = Time.realtimeSinceStartup;
        }

        private void OnDisable()
        {
            _drawCalls.Dispose();
            _batches.Dispose();
            _setPass.Dispose();
            _triangles.Dispose();
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime * 1000.0;
            _frameMsSum += dt;
            if (dt > _frameMsMax) _frameMsMax = dt;
            _frames++;

            // Harvest last frame's PlayerLoop buckets (the current frame is in flight).
            double loopMs = 0;
            for (var i = 0; i < _loopTicks.Length; i++)
            {
                if (_loopTicks[i] == 0) continue;
                var ms = _loopTicks[i] * TicksToMs;
                _loopTicks[i] = 0;
                loopMs += ms;
                Add(LoopNames[i], ms);
            }
            _loopMsSum += loopMs;
            for (var s = 0; s < SectionTicks.Length; s++)
            {
                if (SectionTicks[s] == 0) continue;
                Add("» " + SectionNames[s], SectionTicks[s] * TicksToMs);
                SectionTicks[s] = 0;
                SectionCalls[s] = 0;
            }

            var now = Time.realtimeSinceStartup;
            if (now - _windowStart < 1f) return;
            _text = Report();
            if (now - _lastLog >= 5f)
            {
                _lastLog = now;
                Debug.Log("[WebPerf]\n" + _text);
            }
            _windowMs.Clear();
            _windowMax.Clear();
            _frameMsSum = _frameMsMax = _loopMsSum = 0;
            _frames = 0;
            _windowStart = now;
        }

        private void Add(string name, double ms)
        {
            _windowMs.TryGetValue(name, out var sum);
            _windowMs[name] = sum + ms;
            _windowMax.TryGetValue(name, out var max);
            if (ms > max) _windowMax[name] = ms;
        }

        private string Report()
        {
            var n = Mathf.Max(1, _frames);
            var sb = new StringBuilder(1024);
            sb.Append($"fps {_frames / Mathf.Max(0.001f, Time.realtimeSinceStartup - _windowStart):0.0}  ")
              .Append($"frame avg {_frameMsSum / n:0.0} ms max {_frameMsMax:0}  ")
              .Append($"unity loop {_loopMsSum / n:0.0} ms  outside {(_frameMsSum - _loopMsSum) / n:0.0} ms\n")
              .Append($"draws {Last(_drawCalls)} batches {Last(_batches)} setpass {Last(_setPass)} tris {Last(_triangles)}\n");
            var rows = new List<KeyValuePair<string, double>>(_windowMs);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            var shown = 0;
            foreach (var row in rows)
            {
                var avg = row.Value / n;
                if (avg < 0.2 || shown >= 16) break;
                sb.Append($"{avg,6:0.0} ms  (max {_windowMax[row.Key],5:0})  {row.Key}\n");
                shown++;
            }
            return sb.ToString();
        }

        private static string Last(ProfilerRecorder recorder) =>
            recorder.Valid ? recorder.LastValue.ToString() : "n/a";

        private void OnGUI()
        {
            _style ??= new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 11,
                normal = { textColor = Color.white },
            };
            GUI.Box(new Rect(8, 60, 470, 330), _text, _style);
        }
    }
}
