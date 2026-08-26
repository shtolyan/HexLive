#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fast §152 runtime acceptance in the Editor. Unlike a Player release build,
/// this enters the real bootstrap scene directly and can be repeated after each
/// source edit without IL2CPP or content rebuilding.
/// </summary>
[InitializeOnLoad]
public static class AtomicContentPlayModeSmoke
{
    private const string SessionKey = "HexLive.AtomicContentPlayModeSmoke";
    private const string Active = "active";
    private const string Exiting = "exiting";
    private const string ExitEditorKey = "HexLive.AtomicContentPlayModeSmoke.ExitEditor";
    private static readonly string RequestPath =
        Path.GetFullPath(Path.Combine("Temp", "HexLiveAtomicContentSmoke.request"));
    private const double RegistryTimeoutSeconds = 30;
    private const double TotalTimeoutSeconds = 600;
    private const double StableWorldSeconds = 5;
    private const string ScreenshotPath = "/private/tmp/hexlive-atomic-editor-smoke.png";
    private const string StagingTokenPath = "/private/tmp/hexlive-staging-player.token";
    private const string StagingUrl = "ws://62.146.235.120:5124/watch";

    private static readonly Dictionary<string, int> ExpectedCounts = new(StringComparer.Ordinal)
    {
        ["actor"] = 5,
        ["audio"] = 1128,
        ["building"] = 7,
        ["config"] = 754,
        ["hair"] = 16,
        ["mob"] = 1,
        ["object"] = 37,
        ["prosthetic"] = 8,
        ["ui"] = 0,
        ["vfx"] = 69,
        ["wear"] = 685,
    };

    private static bool _installed;
    private static bool _connectRequested;
    private static bool _loadsStarted;
    private static bool _screenshotRequested;
    private static int _pendingLoads;
    private static int _passedLoads;
    private static double _startedAt;
    private static double _readyAt;
    private static string _failure = string.Empty;

    static AtomicContentPlayModeSmoke()
    {
        if (SessionState.GetString(SessionKey, string.Empty) == Active)
        {
            Install();
        }
        else if (File.Exists(RequestPath))
        {
            File.Delete(RequestPath);
            SessionState.SetBool(ExitEditorKey, false);
            EditorApplication.delayCall += Run;
        }
    }

    [MenuItem("HexLive/Atomic Content/Run Staging Smoke")]
    private static void RunInteractive()
    {
        SessionState.SetBool(ExitEditorKey, false);
        Run();
    }

    public static void Run()
    {
        _connectRequested = false;
        _loadsStarted = false;
        _screenshotRequested = false;
        _pendingLoads = 0;
        _passedLoads = 0;
        _readyAt = 0;
        _failure = string.Empty;
        // Configure the endpoint before Play Mode. PrototypeRuntimeBootstrap
        // asks ContentAssetService for the registry during scene Awake, which
        // is earlier than the first editor Update/BeginConnect callback. A
        // fresh Editor process otherwise probes localhost:5123, pins a stale
        // offline cache and makes the staging smoke test production by accident.
        SessionConfig.UseServer(
            StagingUrl,
            File.Exists(StagingTokenPath) ? StagingTokenPath : null);
        SessionState.SetString(SessionKey, Active);
        _startedAt = EditorApplication.timeSinceStartup;
        Install();

        // A previous bootstrap attempt may have left runtime singletons in a
        // failed state after an endpoint or cache change. Start every smoke in
        // a fresh play session so SubsystemRegistration resets them.
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = false;
            return;
        }

        EditorApplication.delayCall += EnterPlayMode;
    }

    private static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        Application.logMessageReceived += LogReceived;
    }

    private static void EnterPlayMode()
    {
        if (SessionState.GetString(SessionKey, string.Empty) == Active &&
            !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.Log("[AtomicContentSmoke] Entering the real bootstrap scene in Play Mode.");
            EditorApplication.isPlaying = true;
        }
    }

    private static void PlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            _startedAt = EditorApplication.timeSinceStartup;
            Debug.Log("[AtomicContentSmoke] Play Mode entered.");
        }
        else if (state == PlayModeStateChange.EnteredEditMode &&
                 SessionState.GetString(SessionKey, string.Empty) == Active)
        {
            EditorApplication.delayCall += EnterPlayMode;
        }
        else if (state == PlayModeStateChange.EnteredEditMode &&
                 SessionState.GetString(SessionKey, string.Empty) == Exiting)
        {
            ExitEditor(string.IsNullOrEmpty(_failure) ? 0 : 1);
        }
    }

    private static void Update()
    {
        var phase = SessionState.GetString(SessionKey, string.Empty);
        if (phase == Exiting)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ExitEditor(string.IsNullOrEmpty(_failure) ? 0 : 1);
            }
            return;
        }
        if (phase == Active && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EnterPlayMode();
            return;
        }
        if (phase != Active || !EditorApplication.isPlaying)
        {
            return;
        }

        RequestRemoteConnect();

        var elapsed = EditorApplication.timeSinceStartup - _startedAt;
        if (elapsed > TotalTimeoutSeconds)
        {
            Fail($"timeout after {elapsed:F1}s");
            return;
        }

        var service = ContentAssetService.Instance;
        if (!service.RegistryReady)
        {
            if (elapsed > RegistryTimeoutSeconds)
            {
                Fail("live registry did not become ready");
            }
            return;
        }
        if (!string.IsNullOrEmpty(service.LastError))
        {
            Fail("registry/cache error: " + service.LastError);
            return;
        }

        if (!_loadsStarted)
        {
            _loadsStarted = true;
            foreach (var expected in ExpectedCounts)
            {
                var actual = service.Records(expected.Key).Count;
                if (actual != expected.Value)
                {
                    Fail($"{expected.Key} record count {actual}, expected {expected.Value}");
                    return;
                }
            }

            if (!ValidateEmojiFallback(service, "tool.spear", "🔱") ||
                !ValidateEmojiFallback(service, "item.bandage", "🩹"))
            {
                return;
            }

            LoadRepresentativeObjects(service);
                Debug.Log("[AtomicContentSmoke] Registry has exactly 2710 content records; " +
                      "representative payload validation started.");
        }

        if (_pendingLoads != 0 || _passedLoads != 11)
        {
            return;
        }

        var runner = UnityEngine.Object.FindAnyObjectByType<SimulationRunnerBehaviour>();
        if (runner == null || !runner.Link.IsRemote || !runner.IsReady || LoadingScreen.IsActive)
        {
            return;
        }

        var renderer = UnityEngine.Object.FindAnyObjectByType<
            HexLive.UnityPresentation.Rendering.HexWorldRenderer>();
        var snapshot = runner.CreateSnapshot();
        var actorIds = new List<int>();
        if (snapshot != null)
        {
            foreach (var npc in snapshot.Npcs)
            {
                actorIds.Add(npc.Id.Value);
            }
        }
        if (renderer == null || actorIds.Count == 0 ||
            !renderer.ActorsReady(actorIds) ||
            !HexLive.UnityPresentation.Wearing.Garments.ContentQueue.IsIdle)
        {
            _readyAt = 0;
            return;
        }

        if (_readyAt <= 0)
        {
            _readyAt = EditorApplication.timeSinceStartup;
            Debug.Log($"[AtomicContentSmoke] Remote world ready at tick {runner.CurrentTick}; " +
                      "loading curtain is down.");
            return;
        }

        if (!_screenshotRequested &&
            EditorApplication.timeSinceStartup - _readyAt >= StableWorldSeconds)
        {
            if (File.Exists(ScreenshotPath))
            {
                File.Delete(ScreenshotPath);
            }
            ScreenCapture.CaptureScreenshot(ScreenshotPath);
            _screenshotRequested = true;
            return;
        }

        if (_screenshotRequested && File.Exists(ScreenshotPath) &&
            new FileInfo(ScreenshotPath).Length > 0)
        {
            Debug.Log($"[AtomicContentSmoke] PASS: live world + 2710 content records + " +
                      $"11 representative payload checks; screenshot={ScreenshotPath}");
            Finish(leaveInteractivePlayRunning: true);
        }
    }

    private static bool ValidateEmojiFallback(
        ContentAssetService service, string id, string expectedGlyph)
    {
        if (!service.TryGetRecord("object", id, out var record))
        {
            Fail("missing fallback test record: object/" + id);
            return false;
        }
        if (record.HasRealIcon)
        {
            Fail($"marked bootstrap placeholder is treated as real icon: object/{id}");
            return false;
        }
        var glyph = ItemIcons.FallbackGlyph(id);
        if (!string.Equals(glyph, expectedGlyph, StringComparison.Ordinal))
        {
            Fail($"fallback glyph for {id} is '{glyph}', expected '{expectedGlyph}'");
            return false;
        }
        if (ItemIcons.Load(id) != null)
        {
            Fail($"bootstrap placeholder suppressed emoji fallback: object/{id}");
            return false;
        }

        Debug.Log($"[AtomicContentSmoke] emoji fallback OK: {id} → {glyph}");
        return true;
    }

    private static void RequestRemoteConnect()
    {
        if (_connectRequested ||
            SessionConfig.Mode != HexLive.UnityPresentation.Bootstrap.SimulationMode.Remote)
        {
            return;
        }

        var loading = UnityEngine.Object.FindAnyObjectByType<LoadingScreen>();
        var url = SessionConfig.ServerUrl;
        if (loading == null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (File.Exists(StagingTokenPath))
        {
            SessionConfig.UseServer(url, StagingTokenPath);
        }

        var beginConnect = typeof(LoadingScreen).GetMethod(
            "BeginConnect", BindingFlags.Instance | BindingFlags.NonPublic);
        if (beginConnect == null)
        {
            Fail("LoadingScreen.BeginConnect automation seam is missing");
            return;
        }

        beginConnect.Invoke(loading, new object[] { url });
        _connectRequested = true;
        Debug.Log("[AtomicContentSmoke] Requested the normal remote connect path: " + url);
    }

    private static void LoadRepresentativeObjects(ContentAssetService service)
    {
        LoadMain(service, "actor", "Jolly");
        LoadMain(service, "wear", "clothing.skirt_anarchy");
        LoadIcon(service, "wear", "clothing.skirt_anarchy");
        LoadMain(service, "hair", "AdellHair");
        LoadMain(service, "prosthetic", "arm.mechanical.l");
        LoadMain(service, "object", "bed.basic");
        LoadMain(service, "building", "building.hut_1hex");
        LoadMain(service, "mob", "dog");
        LoadMain(service, "vfx", "blood.blood_0");
        LoadFile(service, "audio", "bank.master");
        LoadFile(service, "config", "simdata");
    }

    private static void LoadMain(ContentAssetService service, string type, string id)
    {
        _pendingLoads++;
        service.LoadMain<UnityEngine.Object>(type, id, handle =>
        {
            var valid = handle?.Asset != null;
            handle?.Dispose();
            CompleteLoad(type + "/" + id + ":main", valid);
        });
    }

    private static void LoadIcon(ContentAssetService service, string type, string id)
    {
        _pendingLoads++;
        service.LoadIcon(type, id, handle =>
        {
            var valid = handle?.Asset != null;
            handle?.Dispose();
            CompleteLoad(type + "/" + id + ":icon", valid);
        });
    }

    private static void LoadFile(ContentAssetService service, string type, string id)
    {
        _pendingLoads++;
        service.GetRawFile(type, id, path =>
            CompleteLoad(type + "/" + id + ":file", !string.IsNullOrEmpty(path) && File.Exists(path)));
    }

    private static void CompleteLoad(string label, bool valid)
    {
        _pendingLoads--;
        if (!valid)
        {
            Fail("payload did not load: " + label);
            return;
        }

        _passedLoads++;
        Debug.Log("[AtomicContentSmoke] payload OK: " + label);
    }

    private static void LogReceived(string condition, string stackTrace, LogType type)
    {
        if (SessionState.GetString(SessionKey, string.Empty) != Active)
        {
            return;
        }

        var fatal = condition.Contains("Insecure connection not allowed", StringComparison.Ordinal) ||
                    condition.Contains("Bundle ", StringComparison.Ordinal) &&
                    condition.Contains("не открывается", StringComparison.Ordinal) ||
                    condition.Contains("Required runtime shader is unavailable", StringComparison.Ordinal) ||
                    type == LogType.Exception && condition.Contains("AtomicContent", StringComparison.Ordinal);
        if (fatal)
        {
            Fail("fatal runtime log: " + condition);
        }
    }

    private static void Fail(string message)
    {
        if (!string.IsNullOrEmpty(_failure))
        {
            return;
        }

        _failure = message;
        Debug.LogError("[AtomicContentSmoke] FAIL: " + message);
        Finish();
    }

    private static void Finish(bool leaveInteractivePlayRunning = false)
    {
        var interactive = !Application.isBatchMode &&
                          !SessionState.GetBool(ExitEditorKey, false);
        if (leaveInteractivePlayRunning && interactive && string.IsNullOrEmpty(_failure))
        {
            SessionState.EraseString(SessionKey);
            SessionState.EraseBool(ExitEditorKey);
            EditorApplication.update -= Update;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            Application.logMessageReceived -= LogReceived;
            _installed = false;
            Debug.Log("[AtomicContentSmoke] Interactive PASS; Play Mode remains running for inspection.");
            return;
        }

        SessionState.SetString(SessionKey, Exiting);
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = false;
        }
        else
        {
            ExitEditor(string.IsNullOrEmpty(_failure) ? 0 : 1);
        }
    }

    private static void ExitEditor(int code)
    {
        var shouldExit = Application.isBatchMode ||
                         SessionState.GetBool(ExitEditorKey, false);
        SessionState.EraseString(SessionKey);
        SessionState.EraseBool(ExitEditorKey);
        EditorApplication.update -= Update;
        EditorApplication.playModeStateChanged -= PlayModeChanged;
        Application.logMessageReceived -= LogReceived;
        _installed = false;
        if (shouldExit)
        {
            EditorApplication.Exit(code);
        }
        else
        {
            Debug.Log(code == 0
                ? "[AtomicContentSmoke] Interactive run complete; Editor remains open."
                : "[AtomicContentSmoke] Interactive run failed; Editor remains open.");
        }
    }
}
#endif
