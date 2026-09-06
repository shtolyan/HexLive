using System;
using System.Threading;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
/// <summary>§161 a client conversation, independent of character attachments.</summary>
public sealed class AdminVoicePanel : MonoBehaviour
{
    private SimulationRunnerBehaviour _source;
    private GameObject _ui;
    private PanelSettings _settings;
    private VisualElement _root, _body;
    private TextField _binding, _input;
    private Label _client, _reply;
    private Button _send, _mic, _yes, _no;
    private string _token = "", _server = "", _message = "", _confirmation = "";
    private float _nextPoll, _deadline;
    private int _selectedNpc;
    private FmodMicrophoneCapture _capture;
    private PlayerVoiceCaptureOverlay _captureOverlay;
    private DeepgramSpeechTranscriber _transcriber;
    private byte[] _wav;
    private CancellationTokenSource _lifetime;
    private bool _busy, _recordingForConfirmation;

    private void Start()
    {
        _source = GetComponent<SimulationRunnerBehaviour>();
        _lifetime = new CancellationTokenSource(); _capture = new FmodMicrophoneCapture(); _transcriber = new DeepgramSpeechTranscriber(2000, rejectOverflow: true);
        _ui = new GameObject("AdminVoiceUI"); _ui.transform.SetParent(transform, false);
        var doc = _ui.AddComponent<UIDocument>();
        var baseSettings = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        if (baseSettings == null) { enabled = false; return; }
        _settings = Instantiate(baseSettings); _settings.sortingOrder = 180; doc.panelSettings = _settings;
        _root = new VisualElement(); _root.AddToClassList("admin-root"); doc.rootVisualElement.Add(_root);
        var style = Resources.Load<StyleSheet>("HexLive/AdminVoice"); if (style != null) _root.styleSheets.Add(style);
        _body = new VisualElement(); _body.AddToClassList("admin-body"); _body.AddToClassList("admin-hidden");
        var toggle = new Button(() => { _body.ToggleInClassList("admin-hidden"); if (_body.ClassListContains("admin-hidden")) CancelCapture(); }) { text = L("title") };
        _root.Add(toggle); _root.Add(_body);
        doc.rootVisualElement.pickingMode = PickingMode.Ignore;
        _captureOverlay = new PlayerVoiceCaptureOverlay(doc.rootVisualElement, FinishCapture, CancelCapture, () => L("title"));
        _client = new Label(); _body.Add(_client);
        _binding = new TextField(L("code")); _body.Add(_binding);
        _body.Add(new Button(() => Send("bind", new JObject { ["code"] = _binding.value.Trim() })) { text = L("bind") });
        _input = new TextField(L("command")) { multiline = true, maxLength = 2000 }; _body.Add(_input);
        _send = new Button(() => Submit(_input.value)) { text = L("send") }; _body.Add(_send);
        _mic = new Button(Microphone) { text = L("microphone") };
        _mic.AddToClassList("admin-microphone");
        var microphoneIcon = new VectorIcon(VectorIcon.Kind.Microphone, new Color(0.18f, 0.92f, 0.88f));
        microphoneIcon.AddToClassList("admin-microphone-icon"); microphoneIcon.pickingMode = PickingMode.Ignore;
        _mic.Insert(0, microphoneIcon); _body.Add(_mic);
        var scroll = new ScrollView(); scroll.AddToClassList("admin-reply"); _reply = new Label(); scroll.Add(_reply); _body.Add(scroll);
        _yes = new Button(() => Confirm(true)) { text = L("yes") }; _no = new Button(() => Confirm(false)) { text = L("no") };
        _body.Add(_yes); _body.Add(_no); RefreshButtons();
    }
    private static string L(string key) => Loc.Get("admin.voice." + key);
    private void Update()
    {
        if (_root == null || _source == null) return;
        _root.EnableInClassList("admin-hidden", string.IsNullOrEmpty(_source.AdminServer));
        if (_source.AdminServer != _server)
        {
            _server = _source.AdminServer; _token = _message = _confirmation = ""; _busy = false; _wav = null; _capture.Cancel();
            if (_server.Length > 0)
            {
                _client.text = L("client") + " " + _source.AdminClientId;
                try { _token = AdminCredentialStore.Read(_server, _source.AdminClientId); }
                catch (Exception) { _reply.text = L("credential_error"); }
                if (_token.Length > 0) Send("status");
            }
        }
        if (_capture.IsRecording && (!_source.IsReady || _source.Link.State != LinkState.Live || LoadingScreen.IsReplaying)) CancelCapture();
        if (_capture.IsRecording && _capture.Tick(Time.unscaledDeltaTime)) FinishCapture();
        _captureOverlay.Refresh(_capture);
        while (_source.TryTakeAdminResult(out var json))
        {
            try { Handle(JObject.Parse(json)); }
            catch (Exception) { _reply.text = L("error"); _busy = false; _wav = null; }
        }
        if (_message.Length > 0 && Time.unscaledTime >= _nextPoll)
        { _nextPoll = Time.unscaledTime + 1; Send("poll", new JObject { ["messageId"] = _message }); }
        if (_busy && Time.unscaledTime > _deadline)
        { _busy = false; _wav = null; _reply.text = L("timeout"); }
        RefreshButtons();
    }
    private void Send(string kind, JObject request = null)
    {
        request ??= new JObject(); request["kind"] = kind; request["token"] = _token;
        _source.SendAdmin(request.ToString(Newtonsoft.Json.Formatting.None));
    }
    private void Submit(string text)
    {
        if (_busy || string.IsNullOrWhiteSpace(text) || _token.Length == 0) return;
        if (_confirmation.Length > 0)
        {
            var answer = text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
            if (answer == "да" || answer == "подтверждаю") { Confirm(true); return; }
            if (answer == "нет" || answer == "отмена") { Confirm(false); return; }
            _reply.text = L("confirm_required"); return;
        }
        _selectedNpc = NpcSelection.HasSelection ? NpcSelection.SelectedId : 0;
        SendText(text);
    }
    private void SendText(string text)
    {
        _input.value = text; _message = Guid.NewGuid().ToString("N"); _busy = true; _deadline = Time.unscaledTime + 190;
        _reply.text = L("thinking"); Send("text", new JObject { ["messageId"] = _message, ["text"] = text, ["npcId"] = _selectedNpc });
    }
    private void Confirm(bool accept)
    {
        if (_confirmation.Length == 0) return;
        Send("confirm", new JObject { ["confirmationId"] = _confirmation, ["accept"] = accept });
        _confirmation = ""; _busy = true; _deadline = Time.unscaledTime + 30;
    }
    private void Microphone()
    {
        if (_capture.IsRecording) { FinishCapture(); return; }
        if (_busy || _token.Length == 0 || !_source.IsReady || _source.Link.State != LinkState.Live || LoadingScreen.IsReplaying) return;
        _selectedNpc = NpcSelection.HasSelection ? NpcSelection.SelectedId : 0;
        _recordingForConfirmation = _confirmation.Length > 0;
        if (_capture.Start(out _)) _reply.text = L("listening"); else _reply.text = L("microphone_error");
        _captureOverlay.Refresh(_capture);
    }
    private void CancelCapture()
    {
        _capture?.Cancel(); _captureOverlay?.Hide(); _recordingForConfirmation = false;
        if (_reply != null && !_busy) _reply.text = L("ready");
    }
    private void FinishCapture()
    {
        _wav = _capture.Stop(out _);
        _captureOverlay.Hide();
        if (_wav == null) { _reply.text = L("microphone_error"); return; }
        _busy = true; _deadline = Time.unscaledTime + 50; _reply.text = L("recognizing"); Send("stt");
    }
    private async void Transcribe(string token)
    {
        var wav = _wav; _wav = null; var server = _server; var lifetime = _lifetime;
        try
        {
            var text = await _transcriber.TranscribeAsync(wav, token, lifetime.Token);
            if (lifetime.IsCancellationRequested || _server != server) return;
            _busy = false;
            if (string.IsNullOrWhiteSpace(text)) { _reply.text = L("empty"); return; }
            if (_recordingForConfirmation) Submit(text); else SendText(text);
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested && _server == server) { if (_reply != null) _reply.text = L("stt_error"); _busy = false; } }
    }
    private void Handle(JObject envelope)
    {
        var kind = (string)envelope["kind"]; var p = envelope["payload"];
        var accepted = (bool?)p?["accepted"] == true;
        if (kind == "bind" && accepted)
        {
            _token = (string)p["token"] ?? ""; _binding.value = "";
            try { AdminCredentialStore.Write(_server, _source.AdminClientId, _token); _reply.text = L("ready"); }
            catch (Exception) { _reply.text = L("credential_error"); }
            return;
        }
        if (kind == "stt")
        { if (accepted && _wav != null) Transcribe((string)p["sttToken"]); else { _busy = false; _wav = null; _reply.text = L("stt_error"); } return; }
        if (kind == "poll" && accepted)
        {
            if ((string)p["messageId"] != _message || (bool?)p["done"] != true) return;
            _message = ""; _busy = false;
            _confirmation = (string)p["confirmationId"] ?? "";
            _reply.text = (string)p["text"] ?? "";
            if (_confirmation.Length > 0) _reply.text += "\n" + L("confirm_required") + "\n" + FormatConfirmation((string)p["confirmationText"]);
            return;
        }
        if (kind == "text" && accepted) return;
        if (kind == "status" && accepted) { _reply.text = L("ready"); return; }
        _busy = false;
        if (!accepted)
        {
            _message = "";
            if ((string)p?["reason"] == "AdminUnauthorized") { _token = ""; _confirmation = ""; }
        }
        var reason = (string)p?["reason"] ?? "";
        _reply.text = accepted ? L("done") : L("error") + " " + reason;
    }
    private static string FormatConfirmation(string json)
    {
        var preview = JObject.Parse(json); var command = preview["command"]; var target = preview["target"];
        var kind = (string)command["kind"];
        var name = (string)target?["name"] ?? (string)target?["definitionId"] ?? "";
        var id = (int?)target?["npcId"] ?? (int?)target?["objectId"] ?? 0;
        var text = L("action." + kind) + " — " + name + " (#" + id + ")";
        if (kind == "remove_item") text += "\n" + (string)command["definitionId"] + " × " + (int?)command["count"];
        if (kind == "delete_npc" || kind == "clear_inventory")
            text += "\n" + L("items") + " " + ((int?)target?["inventoryCount"] ?? 0);
        return text;
    }

    private void RefreshButtons()
    {
        if (_send == null) return;
        _send.SetEnabled(!_busy && _message.Length == 0 && _token.Length > 0); _mic.SetEnabled(!_busy && _message.Length == 0 && _token.Length > 0);
        _yes.EnableInClassList("admin-hidden", _confirmation.Length == 0);
        _no.EnableInClassList("admin-hidden", _confirmation.Length == 0);
    }
    private void OnEnable()
    {
        if (_lifetime != null && _lifetime.IsCancellationRequested)
        { _lifetime.Dispose(); _lifetime = new CancellationTokenSource(); _busy = false; }
    }
    private void OnDisable()
    {
        CancelCapture(); _lifetime?.Cancel(); _wav = null;
    }
    private void OnDestroy()
    {
        _capture?.Dispose(); _transcriber?.Dispose(); _lifetime?.Dispose();
        if (_ui != null) Destroy(_ui); if (_settings != null) Destroy(_settings);
    }
}
}
