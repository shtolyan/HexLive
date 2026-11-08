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
        private float _reportMeterNext;
        private const string ReportImageKey = "bug-report-preview";

        private void BeginQuickCapture()
        {
            _runner ??= FindAnyObjectByType<SimulationRunnerBehaviour>();
            _sender.ClearError();
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
                _capturingReport = false; SetReportPreview(png); ApplyVisible(); RefreshQuickReporter();
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
            if (_sender.IsRecording) { _sender.FinishRecording(); return; }
            _reportError = ""; _voiceWav = null; _waitingVoiceFinish = true;
            if (!_sender.StartRecording()) _waitingVoiceFinish = false;
            RefreshQuickReporter();
        }
        private void ReporterChanged()
        {
            if (_quickVisible && _waitingVoiceFinish && !_sender.IsRecording && _sender.HasRecording)
            {
                _waitingVoiceFinish = false; _voiceWav = _sender.TakeRecording(); SubmitQuickReport();
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
            if (_reportStatus != null) _reportStatus.text = Loc.Get("bugs.voice.recording") + " " + Mathf.FloorToInt(_sender.RecordingSeconds) + "s";
        }
        private void RefreshQuickReporter()
        {
            if (_sender == null) return;
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
    }
}
