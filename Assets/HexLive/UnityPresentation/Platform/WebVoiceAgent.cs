#nullable enable
using System;
using System.Text;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace HexLive.UnityPresentation.Platform
{

/// <summary>
/// §171.11: talking to your own character in the browser. ElevenLabs Agents owns
/// the microphone, turn-taking, interruption and the voice (WebRTC, played by the
/// browser itself); the game server's brain decides what she says and does. This
/// class only starts/stops the session and drains its events once a frame.
/// Off the web it reports <see cref="Supported"/> = false and does nothing.
/// </summary>
public sealed class WebVoiceAgent
{
    public enum Phase { Off, Connecting, Listening, Speaking, Error }

    [Serializable] private sealed class Batch { public VoiceEvent[] items = Array.Empty<VoiceEvent>(); }

    [Serializable]
    public sealed class VoiceEvent
    {
        public string type = "";
        public string state = "";
        public string detail = "";
        public string text = "";
        public string[] chars = Array.Empty<string>();
        public int[] starts = Array.Empty<int>();
        public int[] durations = Array.Empty<int>();
        public float seconds;
    }

    public Phase Current { get; private set; } = Phase.Off;
    public string LastError { get; private set; } = "";
    public int NpcId { get; private set; }

    /// <summary>(speaker is "user" or "agent", text) — final lines for subtitles.</summary>
    public event Action<string, string>? Line;
    /// <summary>Per-character audio timing of what she is saying now (lip-sync input).</summary>
    public event Action<VoiceEvent>? Alignment;

    public static bool Supported => WebPage.IsWeb;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void HexVoiceStart(string server, string bearer, string clientId, int npcId);
    [DllImport("__Internal")] private static extern void HexVoiceStop();
    [DllImport("__Internal")] private static extern int HexVoiceNextSize();
    [DllImport("__Internal")] private static extern int HexVoiceTake(byte[] buffer, int capacity);
#endif

    /// <param name="serverHttpBase">https://host of the game server (same origin as the page).</param>
    /// <param name="bearer">account session (§171.9) or the shared player token.</param>
    public void Start(string serverHttpBase, string bearer, string clientId, int npcId)
    {
        if (!Supported || Current is Phase.Connecting or Phase.Listening or Phase.Speaking) return;
        NpcId = npcId;
        LastError = "";
        Current = Phase.Connecting;
#if UNITY_WEBGL && !UNITY_EDITOR
        HexVoiceStart(serverHttpBase, bearer, clientId, npcId);
#endif
    }

    public void Stop()
    {
        if (Current == Phase.Off) return;
#if UNITY_WEBGL && !UNITY_EDITOR
        HexVoiceStop();
#endif
        Current = Phase.Off;
    }

    /// <summary>Call once a frame while the panel is open.</summary>
    public void Pump()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        var size = HexVoiceNextSize();
        if (size <= 0) return;
        var buffer = new byte[size + 1];
        var taken = HexVoiceTake(buffer, buffer.Length);
        if (taken <= 0) return;
        Apply(Encoding.UTF8.GetString(buffer, 0, taken));
#endif
    }

    /// <summary>Applies one JSON batch of events (public for tests and the editor preview).</summary>
    public void Apply(string jsonArray)
    {
        Batch batch;
        try { batch = JsonUtility.FromJson<Batch>("{\"items\":" + jsonArray + "}"); }
        catch (ArgumentException) { return; }
        foreach (var e in batch.items ?? Array.Empty<VoiceEvent>())
        {
            switch (e.type)
            {
                case "state":
                    Current = e.state switch
                    {
                        "connecting" => Phase.Connecting,
                        "listening" => Phase.Listening,
                        "speaking" => Phase.Speaking,
                        "error" => Phase.Error,
                        _ => Phase.Off,
                    };
                    if (Current == Phase.Error) LastError = e.detail;
                    break;
                case "user":
                case "agent":
                    if (e.text.Length > 0) Line?.Invoke(e.type, e.text);
                    break;
                case "align":
                    Alignment?.Invoke(e);
                    break;
            }
        }
    }

    /// <summary>§168.10: the page's own game server as an https base, from its wss://host/watch.</summary>
    public static string? PageServerHttpBase()
    {
        var watch = WebPage.PageServerUrl;
        if (watch == null || !Uri.TryCreate(watch, UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme == "wss" ? "https" : "http";
        return scheme + "://" + uri.Authority;
    }
}
}
