using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
public sealed class AdminVoicePanel : MonoBehaviour
{
    public static bool IsOpen { get; private set; }
    private static int _closedFrame = -1;
    public static bool BlocksGameInput => IsOpen || _closedFrame == Time.frameCount;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { IsOpen = false; _closedFrame = -1; }
    private SimulationRunnerBehaviour _source;
    private GameObject _ui;
    private PanelSettings _settings;
    private VisualElement _root, _modal, _debug;
    private Label _status;
    private TextField _input, _raw;
    private Button _request, _send, _sendRaw;
    private FmodMicrophoneCapture _capture;
    private PlayerVoiceCaptureOverlay _captureOverlay;
    private DeepgramSpeechTranscriber _transcriber;
    private CancellationTokenSource _lifetime;
    private string _server = "", _token = "", _epoch = "", _accessState = "unrequested", _message = "", _confirmation = "", _sttToken = "";
    private bool _approved, _busy, _sttAvailable, _agentAvailable;
    private float _nextStatus, _nextPoll, _deadline;
    private long _sttExpiry;
    private byte[] _wav;
    private AdminRequestContext _context;
    private static string L(string key) => Loc.Get("admin.voice." + key);
    private void Start()
    {
        _source = GetComponent<SimulationRunnerBehaviour>();
        _capture = new FmodMicrophoneCapture(); _transcriber = new DeepgramSpeechTranscriber(2000, true); _lifetime = new CancellationTokenSource();
        _ui = new GameObject("AdminVoiceUI"); _ui.transform.SetParent(transform, false);
        var doc = _ui.AddComponent<UIDocument>(); var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (baseSettings == null) { enabled = false; return; }
        _settings = Instantiate(baseSettings); _settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        _settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight; _settings.referenceResolution = new Vector2Int(1920, 1080);
        _settings.match = 1f; _settings.sortingOrder = 180; doc.panelSettings = _settings;
        doc.rootVisualElement.pickingMode = PickingMode.Ignore;
        _root = new VisualElement { pickingMode = PickingMode.Ignore }; _root.AddToClassList("admin-root"); doc.rootVisualElement.Add(_root);
        var style = Resources.Load<StyleSheet>("HexLive/UI/AdminVoice"); if (style != null) _root.styleSheets.Add(style);
        var toggle = new Button(Microphone) { tooltip = L("title") }; toggle.AddToClassList("admin-toggle");
        var icon = new VectorIcon(VectorIcon.Kind.Microphone, new Color(.86f, .69f, .37f)) { pickingMode = PickingMode.Ignore };
        icon.AddToClassList("admin-toggle-icon"); toggle.Add(icon); _root.Add(toggle);
        toggle.RegisterCallback<PointerEnterEvent>(_ => NpcSelection.PointerOverUi = true);
        toggle.RegisterCallback<PointerLeaveEvent>(_ => NpcSelection.PointerOverUi = false);
        _modal = new VisualElement(); _modal.AddToClassList("admin-modal"); _modal.AddToClassList("admin-hidden"); _root.Add(_modal);
        _modal.RegisterCallback<PointerDownEvent>(e => { if (e.target == _modal) SetOpen(false); e.StopPropagation(); });
        _modal.RegisterCallback<PointerUpEvent>(e => e.StopPropagation());
        var card = new VisualElement(); card.AddToClassList("admin-card"); _modal.Add(card);
        var header = new VisualElement(); header.AddToClassList("admin-header"); card.Add(header);
        var title = new Label(L("title")); title.AddToClassList("admin-title"); header.Add(title);
        var close = new Button(() => SetOpen(false)) { text = "×", tooltip = Loc.Get("loot.quantity_cancel") }; close.AddToClassList("admin-close"); header.Add(close);
        var scroll = new ScrollView(); scroll.AddToClassList("admin-content"); card.Add(scroll);
        var body = new VisualElement(); body.AddToClassList("admin-body"); scroll.Add(body);
        _status = new Label(); body.Add(_status);
        _request = new Button(() => { if (EnsureSecret()) { Send("request_access"); _request.SetEnabled(false); _status.text = L("access.pending"); } }) { text = L("request_access") }; body.Add(_request);
        _debug = new VisualElement(); _debug.AddToClassList("admin-hidden"); body.Add(_debug);
        _input = new TextField(L("command")) { multiline = true, maxLength = 2000 }; _debug.Add(_input);
        _send = new Button(() => { _context = CaptureContext(); Submit(_input.value); }) { text = L("send") }; _debug.Add(_send);
        _debug.Add(new Label(L("examples")));
        _raw = new TextField(L("typed_command")) { multiline = true, maxLength = 8000 }; _debug.Add(_raw);
        _sendRaw = new Button(SendTyped) { text = L("send_typed") }; _debug.Add(_sendRaw);
        _debug.Add(new Label(L("typed_help")));
        _captureOverlay = new PlayerVoiceCaptureOverlay(doc.rootVisualElement, FinishCapture, CancelCapture, () => L("title"));
    }
    public void OpenDebug()
    {
        if (_modal == null) return;
        _debug.EnableInClassList("admin-hidden", !_approved); SetOpen(true);
    }
    private void SetOpen(bool open)
    {
        if (IsOpen == open) return;
        IsOpen = open; _modal?.EnableInClassList("admin-hidden", !open);
        if (!open) { _closedFrame = Time.frameCount; CancelCapture(); _input?.Blur(); _raw?.Blur(); NpcSelection.PointerOverUi = false; }
    }
    private bool EnsureSecret()
    {
        if (_token.Length > 0) return true;
        try
        {
            var secret = AdminCredentialStore.Read(_server, _source.AdminClientId);
            if (secret.Length == 0)
            {
                var bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                secret = BitConverter.ToString(bytes).Replace("-", "");
                AdminCredentialStore.Write(_server, _source.AdminClientId, secret);
            }
            _token = secret; return true;
        }
        catch (Exception) { Notify(L("credential_error"), false); return false; }
    }
    private void Update()
    {
        if (_root == null || _source == null) return;
        var available = _source.AdminServer.Length > 0 && !LoadingScreen.IsActive;
        _root.EnableInClassList("admin-hidden", !available);
        if (!available || (IsOpen && Keyboard.current?.escapeKey.wasPressedThisFrame == true)) SetOpen(false);
        if (_server != _source.AdminServer)
        {
            CancelCapture(); _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new CancellationTokenSource();
            _server = _source.AdminServer; _token = _message = _confirmation = _epoch = _sttToken = ""; _approved = _busy = false; _accessState = "unrequested"; _nextStatus = 0;
            if (_server.Length > 0) EnsureSecret();
        }
        if (available && _token.Length > 0 && Time.unscaledTime >= _nextStatus)
        { _nextStatus = Time.unscaledTime + 5; Send("access_status"); }
        if (_capture.IsRecording && (!_source.IsReady || _source.Link.State != LinkState.Live || !_approved)) CancelCapture();
        if (_capture.IsRecording && _capture.Tick(Time.unscaledDeltaTime)) FinishCapture();
        _captureOverlay.Refresh(_capture);
        while (_source.TryTakeAdminResult(out var json))
        { try { Handle(JObject.Parse(json)); } catch (Exception) { _busy = false; Notify(L("error"), false); } }
        if (_message.Length > 0 && Time.unscaledTime >= _nextPoll)
        { _nextPoll = Time.unscaledTime + 1; Send("poll", new JObject { ["messageId"] = _message }); }
        if (_busy && Time.unscaledTime > _deadline) { _busy = false; _wav = null; Notify(L("timeout"), false); }
        _request.EnableInClassList("admin-hidden", _approved);
        _request.SetEnabled(_accessState != "pending" && _source.Link.State == LinkState.Live);
        _send.SetEnabled(_approved && !_busy && _message.Length == 0); _sendRaw.SetEnabled(_approved && !_busy && _message.Length == 0);
    }
    private void Send(string kind, JObject data = null)
    { data ??= new JObject(); data["kind"] = kind; data["token"] = _token; _source.SendAdmin(data.ToString(Formatting.None)); }
    private AdminRequestContext CaptureContext()
    {
        var context = new AdminRequestContext { Epoch = _epoch, PrimaryNpcId = NpcSelection.PrimaryId, SelectedNpcIds = NpcSelection.SelectedIds.ToArray() };
        var camera = FindAnyObjectByType<RtsCameraController>();
        if (camera != null)
        {
            var hit = camera.TryGetAdminCameraContext(out var ground, out var pos, out var forward, out var point);
            context.CameraPosition = new[] { pos.x, pos.y, pos.z }; context.CameraForward = new[] { forward.x, forward.y, forward.z };
            if (hit) { context.GroundQ = ground.Q; context.GroundR = ground.R; context.GroundPosition = new[] { point.x, point.y, point.z }; }
        }
        return context;
    }
    private void Microphone()
    {
        if (_capture.IsRecording) { FinishCapture(); return; }
        if (!_approved) { _debug.AddToClassList("admin-hidden"); SetOpen(true); _status.text = L("access." + _accessState); return; }
        if (_busy || _message.Length > 0) { Notify(L("thinking"), false); return; }
        if (!_source.IsReady || _source.Link.State != LinkState.Live) { Notify(L("reason.Disconnected"), false); return; }
        if (!_sttAvailable || !_agentAvailable) { Notify(L(!_sttAvailable ? "reason.SttUnavailable" : "reason.AgentOffline"), false); return; }
        SetOpen(false); _context = CaptureContext();
        if (!_capture.Start(out _)) Notify(L("microphone_error"), false);
        _captureOverlay.Refresh(_capture);
    }
    private void CancelCapture() { _capture?.Cancel(); _captureOverlay?.Hide(); }
    private void FinishCapture()
    {
        _wav = _capture.Stop(out _); _captureOverlay.Hide();
        if (_wav == null) { Notify(L("microphone_error"), false); return; }
        _busy = true; _deadline = Time.unscaledTime + 50;
        if (_sttExpiry > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000) Transcribe(_sttToken);
        else Send("stt");
    }
    private async void Transcribe(string token)
    {
        var wav = _wav; _wav = null; var lifetime = _lifetime; var context = _context;
        try
        {
            var text = await _transcriber.TranscribeAsync(wav, token, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            _busy = false;
            if (!_approved || context.Epoch != _epoch) { Notify(L("reason.WorldChanged"), false); return; }
            _context = context; Submit(text);
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested) { _busy = false; _sttToken = ""; _sttExpiry = 0; Notify(L("stt_error"), false); } }
    }
    private void Submit(string text)
    {
        if (!_approved || _busy || _message.Length > 0) return;
        if (string.IsNullOrWhiteSpace(text)) { Notify(L("empty"), false); return; }
        if (_confirmation.Length > 0)
        {
            var answer = text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
            if (answer is "да" or "подтверждаю" or "нет" or "отмена")
            {
                Send("confirm", new JObject { ["confirmationId"] = _confirmation, ["accept"] = answer is "да" or "подтверждаю" });
                _confirmation = ""; _busy = true; _deadline = Time.unscaledTime + 30; return;
            }
            Notify(L("confirm_required"), false); return;
        }
        _message = Guid.NewGuid().ToString("N"); _busy = true; _deadline = Time.unscaledTime + 190;
        Send("text", new JObject { ["messageId"] = _message, ["text"] = text, ["context"] = JObject.FromObject(_context) });
    }
    private void SendTyped()
    {
        try
        {
            var command = JObject.Parse(_raw.value); command["operationId"] = Guid.NewGuid().ToString("N");
            _busy = true; _deadline = Time.unscaledTime + 30;
            Send("debug_command", new JObject { ["command"] = command, ["epoch"] = _epoch });
        }
        catch (Exception) { _busy = false; Notify(L("reason.InvalidInput"), false); }
    }
    private void Handle(JObject envelope)
    {
        var kind = (string)envelope["kind"]; var p = envelope["payload"]; var accepted = (bool?)p?["accepted"] == true;
        if (kind is "access_status" or "request_access")
        {
            var wasApproved = _approved; var previousState = _accessState; _approved = accepted; _accessState = (string)p["state"] ?? "unrequested";
            _status.text = L("access." + _accessState);
            _debug.EnableInClassList("admin-hidden", !_approved);
            if (_approved && p["expiresUtc"]?.Type == JTokenType.String) _status.text += "\n" + (string)p["expiresUtc"];
            if (kind == "request_access") _nextStatus = 0;
            if (kind == "access_status") { _epoch = (string)p["epoch"] ?? ""; _sttAvailable = (bool?)p["sttAvailable"] == true; _agentAvailable = (bool?)p["agentAvailable"] == true; }
            if (!wasApproved && _approved) { SetOpen(false); Notify(L("access.approved"), true); }
            if (wasApproved && !_approved) { CancelCapture(); _confirmation = _message = ""; _busy = false; Notify(_status.text, false); }
            else if (!_approved && previousState != _accessState &&
                (_accessState is "rejected" or "expired" or "revoked" or "queue_full" or "invalid")) Notify(_status.text, false);
            return;
        }
        if (kind == "stt")
        {
            if (accepted && _wav != null) { _sttToken = (string)p["sttToken"]; _sttExpiry = (long?)p["expiresUtcMilliseconds"] ?? 0; Transcribe(_sttToken); }
            else { _busy = false; _wav = null; Notify(L("stt_error"), false); }
            return;
        }
        if (kind == "text" && accepted) return;
        if (kind == "poll" && accepted)
        {
            if ((string)p["messageId"] != _message || (bool?)p["done"] != true) return;
            _message = ""; _busy = false; _confirmation = (string)p["confirmationId"] ?? "";
            var text = (string)p["text"] ?? L("error");
            if (text == "AgentTimeout") text = L("timeout");
            if ((bool?)p["success"] != true && p["results"] is JArray results)
            {
                var done = results.Where(r => (bool?)r["accepted"] == true).Select(r => L("action." + (string)r["kind"]) + " #" + (int?)r["entityId"] + " " + (string)r["definitionId"]).ToArray();
                if (done.Length > 0) text += "\n" + L("partial") + " " + string.Join(", ", done);
            }
            if (_confirmation.Length > 0) text += "\n" + L("confirm_required") + "\n" + FormatConfirmation((string)p["confirmationText"]);
            Notify(text, (bool?)p["success"] == true); return;
        }
        _busy = false;
        if (kind == "debug_command" && (string)p?["reason"] == "ConfirmationRequired")
        {
            _confirmation = (string)p["confirmationId"];
            Notify(L("confirm_required") + "\n" + FormatConfirmation(new JObject { ["command"] = p["command"], ["target"] = p["target"] }.ToString()), false); return;
        }
        if (!accepted)
        {
            _message = "";
            if ((string)p?["reason"] == "AdminUnauthorized") { _approved = false; _accessState = "revoked"; _nextStatus = 0; }
        }
        var reason = (string)p?["reason"] ?? "InvalidInput";
        Notify(accepted ? L("done") : Reason(reason), accepted);
    }
    private static string Reason(string reason)
    { var key = "admin.voice.reason." + reason; var text = Loc.Get(key); return text == key ? L("error") : text; }
    private static void Notify(string text, bool success) => VoiceResponsePanel.PublishAdmin(text, success);
    private static string FormatConfirmation(string json)
    {
        var p = JObject.Parse(json); var c = p["command"]; var target = p["target"];
        var kind = (string)c["kind"]; var name = (string)target?["name"] ?? (string)target?["definitionId"] ?? "";
        var id = (int?)target?["npcId"] ?? (int?)target?["objectId"] ?? 0;
        var text = L("action." + kind) + " — " + name + " (#" + id + ")";
        if (kind == "remove_item") text += "\n" + (string)c["definitionId"] + " × " + (int?)c["count"];
        return text;
    }
    private void OnEnable() { if (_lifetime?.IsCancellationRequested == true) { _lifetime.Dispose(); _lifetime = new CancellationTokenSource(); _busy = false; } }
    private void OnDisable() { SetOpen(false); CancelCapture(); _lifetime?.Cancel(); _wav = null; }
    private void OnDestroy() { _capture?.Dispose(); _transcriber?.Dispose(); _lifetime?.Dispose(); if (_ui != null) Destroy(_ui); if (_settings != null) Destroy(_settings); }
}
}
