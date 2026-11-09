using System;
using System.Linq;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class BugReportPanel
    {
        private BugReportSender _sender;
        private int _captureSession;
        private bool _capturingReport, _waitingVoiceFinish;
        private string _capturedContext = "", _reportError = "";
        private byte[] _capturedPng, _voiceWav;
        private BugReportQueue.Job _reviewJob;
        private Texture2D _reportTexture;
        private Image _reportPreview;
        private Label _reportStatus;
        private Button _recordReportButton;
        private ScrollView _reportJobs;
        private VisualElement _reportReview, _reportRecording;
        private Label _voiceTime, _voiceHint;
        private Button _voiceDone;
        private readonly System.Collections.Generic.List<VisualElement> _voiceBars = new();
        private float _reportMeterNext;
        private const string ReportImageKey = "bug-report-preview";

        private void BuildVoiceRecordingCard()
        {
            _reportRecording.style.alignItems = Align.Center;
            var icon = new VectorIcon(VectorIcon.Kind.Microphone, new Color(.941f, .706f, .361f));
            icon.style.width = 88; icon.style.height = 88; icon.style.marginTop = 12; icon.style.marginBottom = 18;
            _reportRecording.Add(icon);
            var wave = new VisualElement(); wave.style.flexDirection = FlexDirection.Row;
            wave.style.alignItems = Align.Center; wave.style.height = 110;
            _voiceBars.Clear();
            for (int i = 0; i < 41; i++)
            {
                var bar = new VisualElement(); bar.style.width = 5; bar.style.marginLeft = 2; bar.style.marginRight = 2;
                bar.style.backgroundColor = i % 3 == 0 ? Text : new Color(.941f, .706f, .361f);
                wave.Add(bar); _voiceBars.Add(bar);
            }
            _reportRecording.Add(wave);
            _voiceTime = new Label(); _voiceTime.style.color = Dim; _voiceTime.style.marginTop = 8;
            _reportRecording.Add(_voiceTime);
            _voiceHint = new Label(); _voiceHint.style.color = Text; _voiceHint.style.whiteSpace = WhiteSpace.Normal;
            _voiceHint.style.maxWidth = Length.Percent(100); _voiceHint.style.fontSize = 12;
            _voiceHint.style.unityTextAlign = TextAnchor.MiddleCenter;
            _voiceHint.style.marginTop = 10; _voiceHint.style.marginBottom = 18;
            _reportRecording.Add(_voiceHint);
            var actions = MakeActionRow();
            actions.Add(MakeButton(Loc.Get("bugs.cancel"), Raised, () => SetQuickVisible(false)));
            _voiceDone = new Button(ToggleReportRecording) { text = Loc.Get("bugs.voice.done") };
            _voiceDone.style.backgroundColor = Accent; _voiceDone.style.color = Text; SetRadius(_voiceDone, 6);
            _voiceDone.style.minWidth = 150; _voiceDone.style.height = 42;
            actions.Add(_voiceDone); _reportRecording.Add(actions);
        }

        private void BeginQuickCapture()
        {
            _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
            if (!_sender.PrepareVoiceReport()) return;
            var originalWindows = HtmlUi.VisibleScreenNames();
            _quickVisible = true; _managerVisible = false; _capturingReport = true;
            _reportError = ""; _reviewJob = null; _capturedPng = _voiceWav = null;
            _quickInput.value = ""; SetReportPreview(null);
            int session = ++_captureSession;
            HtmlUi.Hide(HtmlQuick); HtmlUi.Hide(HtmlManager); ApplyVisible();
            _sender.StartCoroutine(_sender.CaptureScreenshot((png, error) =>
            {
                if (this == null || session != _captureSession || !_quickVisible) return;
                _capturedPng = png; _reportError = error; _capturedContext = CaptureContext(originalWindows);
                _capturingReport = false;
                _waitingVoiceFinish = true;
                _sender.StartRecording();
                ApplyVisible(); RefreshQuickReporter();
            }));
        }
        private string CaptureContext(string[] windows)
        {
            var frame = BugReportContextData.Capture(_runner?.CreateSnapshot(), NpcSelection.HasSelection ? NpcSelection.SelectedId : -1);
            frame.version = Application.version; frame.buildGuid = Application.buildGUID;
            frame.platform = Application.platform + " / " + Application.unityVersion;
            frame.language = Loc.Current.ToString(); frame.scene = SceneManager.GetActiveScene().name;
            frame.worldId = _runner?.AssignmentWorldId ?? ""; frame.windows = windows;
            frame.screenWidth = Screen.width; frame.screenHeight = Screen.height;
            var camera = Camera.main;
            if (camera != null)
            {
                var p = camera.transform.position; frame.cameraX = p.x; frame.cameraY = p.y; frame.cameraZ = p.z;
                frame.cameraPitch = camera.transform.eulerAngles.x; frame.cameraYaw = camera.transform.eulerAngles.y;
                frame.cameraZoom = camera.orthographic ? camera.orthographicSize : camera.fieldOfView;
            }
            var json = JsonUtility.ToJson(frame);
            // Keep a valid bounded JSON document, never truncate it mid-string.
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            {
                frame.truncated = true; frame.nearbyObjects = Array.Empty<BugReportContextData.Object>();
                frame.nearbyNpcs = Array.Empty<BugReportContextData.Npc>(); frame.events = Array.Empty<BugReportContextData.Event>();
                frame.nearbyMobs = Array.Empty<BugReportContextData.Mob>();
                if (frame.selected != null)
                {
                    frame.selected.knownObjects = Array.Empty<string>(); frame.selected.inventory = frame.selected.inventory.Take(16).ToArray();
                    frame.selected.worn = frame.selected.worn.Take(16).ToArray(); frame.selected.cooldowns = Array.Empty<string>();
                }
                json = JsonUtility.ToJson(frame);
            }
            return json;
        }
        private void SetReportPreview(byte[] png)
        {
            if (_reportTexture != null) Destroy(_reportTexture);
            _reportTexture = null;
            if (png != null && png.Length > 0)
            {
                _reportTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!_reportTexture.LoadImage(png)) { Destroy(_reportTexture); _reportTexture = null; }
            }
            if (_reportPreview != null) _reportPreview.image = _reportTexture;
            if (png != null && HtmlUi.Active)
                HtmlUi.Asset(ReportImageKey, "data:image/png;base64," + Convert.ToBase64String(png), replace: true);
        }
        private void ToggleReportRecording()
        {
            if (_capturingReport) return;
            if (_reviewJob == null && _voiceWav != null && !_sender.IsRecording) { SubmitQuickReport(); return; }
            if (_sender.IsRecording) { _sender.FinishRecording(); return; }
            _reportError = ""; _voiceWav = null; _waitingVoiceFinish = true;
            if (!_sender.StartRecording()) _waitingVoiceFinish = false;
            RefreshQuickReporter();
        }
        private void ReporterChanged()
        {
            if (_quickVisible && _waitingVoiceFinish && !_sender.IsRecording)
            {
                _waitingVoiceFinish = false;
                if (_sender.HasRecording) { _voiceWav = _sender.TakeRecording(); SubmitQuickReport(); }
                else if (_reviewJob == null) SetQuickVisible(false);
            }
            _htmlDirty = true; RefreshQuickReporter();
        }
        private void SubmitQuickReport()
        {
            if (!_quickVisible || _capturingReport || _sender.IsRecording) return;
            var text = _quickInput.value?.Trim() ?? "";
            if (_reviewJob != null)
            {
                if (_voiceWav != null) { _reviewJob.Wav = _voiceWav; _reviewJob.DraftId = ""; }
                else if (text.Length > 0) { _reviewJob.Wav = null; _reviewJob.DraftId = ""; }
                if (text.Length == 0 && _reviewJob.Wav == null) { _reportError = "bugs.voice.empty"; RefreshQuickReporter(); return; }
                if (!_sender.Retry(_reviewJob, text)) return;
                _reviewJob = null;
            }
            else if (!_sender.Enqueue(_capturedContext, _capturedPng, _voiceWav, text))
            { RefreshQuickReporter(); return; }
            _quickInput.value = ""; _voiceWav = null; SetQuickVisible(false);
        }
        private void ReviewFailedReport(string requestId)
        {
            var job = _sender.Queue.Jobs.FirstOrDefault(j => j.RequestId == requestId && j.State == BugReportQueue.Delivery.Failed);
            if (job == null) return;
            _sender.ClearError();
            _sender.CancelRecording(); _waitingVoiceFinish = false; _captureSession++;
            _reviewJob = job; _capturingReport = false; _quickVisible = true; _managerVisible = false;
            _capturedContext = job.Context; _capturedPng = job.Screenshot; _voiceWav = null;
            _reportError = job.Error; _quickInput.value = job.Text; SetReportPreview(job.Screenshot);
            ApplyVisible(); RefreshQuickReporter();
        }
        public void OpenFailedReport()
        {
            var job = _sender.Queue.Jobs.FirstOrDefault(j => j.State == BugReportQueue.Delivery.Failed);
            if (job != null) ReviewFailedReport(job.RequestId); else SetQuickVisible(true);
        }
        private void RefreshRecordingMeter()
        {
            if (!_quickVisible || !_sender.IsRecording || Time.unscaledTime < _reportMeterNext) return;
            _reportMeterNext = Time.unscaledTime + .1f; _htmlDirty = true;
            RefreshVoiceMeter();
            if (_reportStatus != null) _reportStatus.text = Loc.Get("bugs.voice.recording") + " " + Mathf.FloorToInt(_sender.RecordingSeconds) + "s";
        }
        private void RefreshQuickReporter()
        {
            if (_sender == null) return;
            if (_reportReview != null) _reportReview.style.display = _reviewJob != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_reportRecording != null) _reportRecording.style.display = _reviewJob == null ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshVoiceMeter();
            if (_recordReportButton != null)
                _recordReportButton.text = Loc.Get(_sender.IsRecording ? "bugs.voice.done" : "bugs.voice.record");
            if (_reportStatus != null)
            {
                string error = !string.IsNullOrEmpty(_sender.Error) ? _sender.Error : _reportError;
                _reportStatus.text = !string.IsNullOrEmpty(error) ? Loc.Get(error) :
                    string.Format(Loc.Get("bugs.queue.counts"), BugReportSender.Pending, BugReportSender.Failed);
            }
            if (_reportJobs == null) return;
            _reportJobs.Clear();
            foreach (var job in _sender.Queue.Jobs.ToArray())
            {
                var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row;
                var label = new Label(Loc.Get("bugs.queue." + job.State.ToString().ToLowerInvariant())); label.style.flexGrow = 1;
                row.Add(label);
                if (job.State == BugReportQueue.Delivery.Failed)
                {
                    row.Add(MakeButton(Loc.Get("bugs.queue.review"), Raised, () => ReviewFailedReport(job.RequestId)));
                    row.Add(MakeButton(Loc.Get("bugs.queue.discard"), Raised, () => { _sender.Discard(job); if (_reviewJob == job) SetQuickVisible(false); }));
                }
                _reportJobs.Add(row);
            }
        }
        private void RefreshVoiceMeter()
        {
            if (_voiceTime == null || _sender == null) return;
            var ready = _sender.MicrophoneReady;
            int seconds = Mathf.FloorToInt(_sender.RecordingSeconds);
            _voiceTime.text = ready ? $"{seconds / 60}:{seconds % 60:00} / 1:30" : Loc.Get("bugs.voice.permission");
            _voiceHint.text = Loc.Get(string.IsNullOrEmpty(_sender.Error) ? "bugs.voice.hint" : _sender.Error);
            _voiceDone.text = Loc.Get(_voiceWav != null && !string.IsNullOrEmpty(_sender.Error) ? "bugs.queue.retry" : "bugs.voice.done");
            _voiceDone.SetEnabled(ready || _voiceWav != null);
            for (int i = 0; i < _voiceBars.Count; i++)
                _voiceBars[i].style.height = 6 + Mathf.Clamp01(_sender.Level) * (18 + Mathf.Abs(Mathf.Sin(i * 1.63f + Time.unscaledTime * 7)) * 80);
        }
    }
}
