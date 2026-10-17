#nullable enable
using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace HexLive.UnityPresentation.Platform
{

/// <summary>
/// §174: the web client's windows are HTML, drawn by the page over the canvas
/// (<c>Assets/WebGLTemplates/HexLive/ui</c>). This is the C# half of that seam.
/// A window keeps its logic in C# — when to open, what a button does — and
/// hands the page only a JSON model; the page answers with commands.
///
/// <see cref="Active"/> is true only after the page has answered the hello with
/// the same <see cref="Protocol"/>. Until then (desktop, editor, an old cached
/// page) every call is a no-op and the window falls back to its UI Toolkit
/// version (§174.1: desktop keeps the old UI for development).
/// </summary>
public static class HtmlUi
{
    /// <summary>Bump together with <c>PROTOCOL</c> in <c>ui/bridge.js</c>.
    /// 2: <c>asset</c> messages (images a model refers to by key).</summary>
    public const int Protocol = 2;

    /// <summary>The page runs the HTML interface of the same protocol.</summary>
    public static bool Active { get; private set; }

    /// <summary>Raised once, when the page has answered the hello.</summary>
    public static event Action? Activated;

    /// <summary>The pointer is over an interactive HTML surface.</summary>
    public static bool PointerOverUi => (_bits & PointerOver) != 0;

    /// <summary>A text field of the page has focus — the keyboard is the page's.</summary>
    public static bool TextInputFocused => (_bits & TextFocus) != 0;

    /// <summary>
    /// The world must not take this pointer: it is over HTML, or a press that
    /// began on HTML is still held, or it was released this very frame
    /// (the same gesture rule as <c>RuntimeWindow.BlocksWorldInput</c>).
    /// </summary>
    public static bool BlocksWorldInput =>
        (_bits & (PointerOver | PressHeld)) != 0 || Time.frameCount <= _releaseFrame;

    private const int PointerOver = 1;
    private const int PressHeld = 2;
    private const int TextFocus = 4;
    private const int MaxMessagesPerFrame = 64;

    private static readonly Dictionary<string, Action<string, JObject>> Handlers = new();
    private static readonly HashSet<string> Shown = new();
    private static readonly HashSet<string> Assets = new();
    private static int _bits;
    private static int _releaseFrame = -1;

    /// <summary>Route the page's commands for <paramref name="screen"/> to <paramref name="handler"/>.</summary>
    public static void Handle(string screen, Action<string, JObject> handler) => Handlers[screen] = handler;

    public static void Unhandle(string screen, Action<string, JObject> handler)
    {
        if (Handlers.TryGetValue(screen, out var current) && current == handler)
        {
            Handlers.Remove(screen);
        }
    }

    /// <summary>Open <paramref name="screen"/> or replace its model if it is open.</summary>
    public static void Show(string screen, JObject? model = null)
    {
        if (!Active)
        {
            return;
        }

        Shown.Add(screen);
        Post(new JObject { ["t"] = "show", ["screen"] = screen, ["model"] = model ?? new JObject() });
    }

    public static void Hide(string screen)
    {
        if (!Shown.Remove(screen))
        {
            return;
        }

        Post(new JObject { ["t"] = "hide", ["screen"] = screen });
    }

    /// <summary>
    /// §174.2: an image (data URL) the page keeps under <paramref name="key"/>.
    /// Models refer to it by key, so a model update never carries pictures —
    /// a creator thumbnail travels once, not with every keystroke. Each key is
    /// sent once per page.
    /// </summary>
    public static void Asset(string key, string dataUrl)
    {
        if (!Active || !Assets.Add(key))
        {
            return;
        }

        Post(new JObject { ["t"] = "asset", ["key"] = key, ["url"] = dataUrl });
    }

    /// <summary>The page already holds this asset.</summary>
    public static bool HasAsset(string key) => Assets.Contains(key);

    private static void Post(JObject message)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        HexUiPost(message.ToString(Newtonsoft.Json.Formatting.None));
#endif
    }

    /// <summary>
    /// §174.4: the page renders every string through <c>t(key)</c> from the
    /// same I2 terms the desktop reads (§58) — the whole table of the current
    /// language, once per language. ~3.5k terms, sent only on change.
    /// </summary>
    private static void SendStrings()
    {
        var terms = new JObject();
        foreach (var term in I2.Loc.LocalizationManager.GetTermsList())
        {
            terms[term] = Loc.Get(term);
        }

        Post(new JObject
        {
            ["t"] = "strings",
            ["lang"] = Loc.Current == Language.Russian ? "ru" : "en",
            ["terms"] = terms,
        });
    }

    private static void Receive(string json)
    {
        JObject message;
        try
        {
            message = JObject.Parse(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[HtmlUi] unreadable message from the page: {ex.Message}");
            return;
        }

        switch ((string?)message["t"])
        {
            case "ready":
                var protocol = (int?)message["v"] ?? 0;
                if (protocol != Protocol)
                {
                    // A page cached from another build: keep UI Toolkit rather
                    // than talk past each other.
                    Debug.LogWarning($"[HtmlUi] page protocol {protocol}, player {Protocol} — HTML interface off");
                    return;
                }

                if (!Active)
                {
                    Active = true;
                    Debug.Log($"[HtmlUi] page interface v{Protocol} active");
                    SendStrings();
                    Activated?.Invoke();
                }

                return;
            case "cmd":
                var screen = (string?)message["screen"] ?? string.Empty;
                var command = (string?)message["cmd"] ?? string.Empty;
                if (!Handlers.TryGetValue(screen, out var handler))
                {
                    Debug.LogWarning($"[HtmlUi] no handler for {screen}.{command}");
                    return;
                }

                try
                {
                    handler(command, message["args"] as JObject ?? new JObject());
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                return;
        }
    }

    private static void OnLanguageChanged()
    {
        if (Active)
        {
            SendStrings();
        }
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void HexUiPost(string json);
    [DllImport("__Internal")] private static extern string? HexUiTake();
    [DllImport("__Internal")] private static extern int HexUiPointer();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        var host = new GameObject("HexHtmlUi") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<Pump>();
        Loc.LanguageChanged += OnLanguageChanged;
        // The page answers with "ready"; until then Active stays false.
        Post(new JObject { ["t"] = "hello", ["v"] = Protocol });
    }

    /// <summary>Drains the page once per frame, before any world input reads the gate.</summary>
    [DefaultExecutionOrder(-10000)]
    private sealed class Pump : MonoBehaviour
    {
        private void Update()
        {
            var previous = _bits;
            _bits = HexUiPointer();
            if ((previous & PressHeld) != 0 && (_bits & PressHeld) == 0)
            {
                _releaseFrame = Time.frameCount;
            }

            if (((previous ^ _bits) & TextFocus) != 0)
            {
                // §174.3: Unity swallows every key of the document by default,
                // so an HTML text field could not be typed into.
                WebGLInput.captureAllKeyboardInput = (_bits & TextFocus) == 0;
            }

            for (var i = 0; i < MaxMessagesPerFrame; i++)
            {
                var json = HexUiTake();
                if (string.IsNullOrEmpty(json))
                {
                    break;
                }

                Receive(json!);
            }
        }
    }
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Active = false;
        Activated = null;
        Handlers.Clear();
        Shown.Clear();
        Assets.Clear();
        _bits = 0;
        _releaseFrame = -1;
    }
}
}
