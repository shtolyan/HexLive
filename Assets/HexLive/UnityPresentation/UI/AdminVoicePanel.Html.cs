using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.UI
{
/// <summary>
/// §174.7: the admin voice window as the page's «adminVoice», its microphone
/// as a HUD button. The UI Toolkit root never shows on the web; the window's
/// open state, its RuntimeWindow (world gate) and every request stay here.
/// The page has its own text fields, so it hands over the text with the
/// command; confirming is two buttons that answer «да» / «отмена», exactly
/// what the spoken or typed answer did.
/// </summary>
public sealed partial class AdminVoicePanel
{
    private const string HtmlScreen = "adminVoice";
    private static AdminVoicePanel _instance;
    private string _expires, _confirmationText = "";
    private bool _htmlAvailable;
    private (bool, string, string, bool, bool, bool, string, bool, bool)? _lastHtmlKey;

    /// <summary>The HUD microphone: -1 none on this server, 0 idle, 1 recording.</summary>
    public static int HtmlMicState =>
        _instance == null || !_instance._htmlAvailable ? -1 : _instance._capture.IsRecording ? 1 : 0;

    /// <summary>The HUD microphone: the window until access is granted, then record / stop.</summary>
    public static void HtmlMicrophone()
    {
        if (_instance != null && _instance._htmlAvailable) _instance.Microphone();
    }

    private void UpdateHtml(bool available)
    {
        _htmlAvailable = available;
        if (!HtmlUi.Active) return;
        if (!IsOpen)
        {
            if (_lastHtmlKey != null) { HtmlUi.Hide(HtmlScreen); _lastHtmlKey = null; }
            return;
        }

        var busy = _busy || _message.Length > 0;
        var canRequest = _accessState != "pending" && _source.Link.State == LinkState.Live;
        var confirm = _confirmation.Length > 0 ? _confirmationText : null;
        var mic = _sttAvailable && _agentAvailable;
        // Built only when something it shows changed: the window is open for minutes.
        var key = (true, _accessState, _approved ? _expires : null, ClosedTestAccess.Enabled, canRequest, busy, confirm, mic, _capture.IsRecording);
        if (_lastHtmlKey == key) return;
        _lastHtmlKey = key;
        HtmlUi.Show(HtmlScreen, new JObject
        {
            ["access"] = _accessState,
            ["expires"] = _approved ? _expires : null,
            ["showRequest"] = !ClosedTestAccess.Enabled,
            ["canRequest"] = canRequest,
            ["busy"] = busy,
            ["confirm"] = confirm,
            ["mic"] = mic ? new JObject { ["recording"] = _capture.IsRecording } : null,
        });
    }

    private void OnHtmlCommand(string command, JObject args)
    {
        switch (command)
        {
            case "close":
                SetOpen(false);
                break;
            case "request":
                if (_accessState == "pending" || !EnsureSecret()) break;
                Send("request_access");
                _accessState = "pending"; // the server's answer replaces it
                _status.text = L("access.pending");
                break;
            case "send":
                if (!_approved || _busy || _message.Length > 0) break;
                _context = CaptureContext();
                Submit((string)args["text"] ?? "");
                break;
            case "typed":
                if (!_approved || _busy || _message.Length > 0) break;
                SendTypedJson((string)args["json"] ?? "");
                break;
            case "confirm":
                if (_confirmation.Length == 0) break;
                Submit((bool?)args["accept"] == true ? "да" : "отмена");
                break;
            case "mic":
                Microphone();
                break;
        }
    }
}
}
