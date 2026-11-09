using System;
using System.Collections;
using System.Linq;
using System.Text;
using HexLive.UnityPresentation.Audio;
using UnityEngine;
using UnityEngine.Networking;

namespace HexLive.UnityPresentation.UI
{
    // §114.8: one session queue, independent of the report window and game speed.
    public sealed class BugReportSender : MonoBehaviour
    {
        [Serializable] public sealed class Connection { public string api, project, token; }
        [Serializable] private sealed class Draft { public string id, status, text, error; }
        [Serializable] private sealed class Attachment
        { public string filename = "screenshot.png", contentType = "image/png", dataBase64; }
        [Serializable] private sealed class Submission
        { public string text, context, reportedInVersion, requestId; public Attachment[] attachments; }
        [Serializable] private sealed class Receipt { public int id; }
        private static BugReportSender _instance;
        public static BugReportSender Ensure()
        {
            if (_instance != null) return _instance;
            var owner = new GameObject("BugReportSender"); DontDestroyOnLoad(owner);
            return _instance = owner.AddComponent<BugReportSender>();
        }
        public static int Pending => _instance != null ? _instance.Queue.PendingCount : 0;
        public static int Failed => _instance != null ? _instance.Queue.FailedCount : 0;
        public static int Completed => _instance != null ? _instance.Queue.CompletedCount : 0;
        public readonly BugReportQueue Queue = new();
        public event Action Changed;
        public bool IsRecording => _microphone?.IsRecording == true;
        public bool MicrophoneReady => _microphone?.IsReady == true;
        public float Level => _microphone?.Level ?? 0;
        public float RecordingSeconds => IsRecording && _recordingReady ? Time.realtimeSinceStartup - _recordingStart : 0;
        public string Error { get; private set; } = "";
        public string ToastKey { get; private set; } = "";
        public bool ToastWarning { get; private set; }
        public int LastReportId { get; private set; }
        public string Toast => Time.realtimeSinceStartup < _toastUntil ? ToastKey : "";
        private Connection _connection;
        private BugReportMicrophoneCapture _microphone;
        private UnityWebRequest _request;
        private float _recordingStart, _toastUntil;
        private byte[] _completedRecording;
        private bool _recordingReady;
        private const long VoiceReservation = 4 * 1024 * 1024 + 90 * 16000 * 2 + 44 + 128 * 1024 + BugReportQueue.MaxEntries * 24000;
        public bool Configured => _connection != null && _connection.project == "hexlive" &&
            !string.IsNullOrWhiteSpace(_connection.token) && BugReportConnectionPolicy.IsApi(_connection.api);
        private void Awake()
        {
            _instance = this;
            var asset = Resources.Load<TextAsset>("HexLive/RuntimeReporterConnection");
            if (asset != null) try { _connection = JsonUtility.FromJson<Connection>(asset.text); } catch { }
            _microphone = new BugReportMicrophoneCapture();
        }
        private void Update()
        {
            if (IsRecording && MicrophoneReady && !_recordingReady)
            { _recordingReady = true; _recordingStart = Time.realtimeSinceStartup; }
            if (IsRecording && _microphone.Tick(Time.unscaledDeltaTime)) FinishRecording();
            var job = Queue.BeginNext(Time.realtimeSinceStartup);
            if (job != null) StartCoroutine(Deliver(job));
        }
        public bool StartRecording()
        {
            Error = ""; _completedRecording = null;
            _recordingReady = false;
            if (!_microphone.Start(out _)) { ShowError("bugs.voice.unavailable"); return false; }
            _recordingStart = Time.realtimeSinceStartup; Notify(); return true;
        }
        public void FinishRecording()
        {
            if (!IsRecording) return;
            var raw = _microphone.Stop(out _);
            try { _completedRecording = BugReportWav.Prepare(raw); Error = ""; }
            catch { Error = "bugs.voice.empty"; _completedRecording = null; SetToast(Error, true); }
            Notify();
        }
        public byte[] TakeRecording()
        { var value = _completedRecording; _completedRecording = null; return value; }
        public bool HasRecording => _completedRecording != null;
        public void ClearError() { Error = ""; if (ToastWarning) ToastKey = ""; ToastWarning = false; }
        public bool PrepareVoiceReport()
        {
            ClearError();
            if (!Configured) { ShowError("bugs.report.not_configured"); return false; }
            if (!Queue.CanFit(VoiceReservation)) { ShowError("bugs.queue.full"); return false; }
            return true;
        }
        public bool RetryFirstFailed()
        {
            var job = Queue.Jobs.FirstOrDefault(j => j.State == BugReportQueue.Delivery.Failed);
            if (job == null) return false;
            ClearError(); return Retry(job, job.Text);
        }
        private void SetToast(string key, bool warning = false)
        { ToastKey = key; ToastWarning = warning; _toastUntil = Time.realtimeSinceStartup + (warning ? 6 : 4); }
        private void ShowError(string key) { Error = key; SetToast(key, true); Notify(); }
        public void CancelRecording()
        { _microphone?.Cancel(); _completedRecording = null; Notify(); }
        public bool Enqueue(string context, byte[] image, byte[] wav, string text)
        {
            Error = "";
            if (!Configured) { Error = "bugs.report.not_configured"; Notify(); return false; }
            if (string.IsNullOrWhiteSpace(text) && wav == null)
            { Error = "bugs.voice.empty"; Notify(); return false; }
            if (!Queue.Enqueue(new BugReportQueue.Job(context, image, wav, Bound(text, 12000))))
            { Error = "bugs.queue.full"; Notify(); return false; }
            SetToast("bugs.queue.accepted"); Notify(); return true;
        }
        public bool Retry(BugReportQueue.Job job, string text)
        {
            Error = "";
            if (!Queue.Resubmit(job, Bound(text, 12000))) return false;
            Notify(); return true;
        }
        public void Discard(BugReportQueue.Job job)
        { if (Queue.Discard(job)) Notify(); }
        private IEnumerator Deliver(BugReportQueue.Job job)
        {
            try
            {
                foreach (var step in DeliverSteps(job)) yield return step;
            }
            finally
            {
                if (Queue.Working == job)
                    Queue.Fail(job, "bugs.send.failed");
                if (job.State == BugReportQueue.Delivery.Failed) SetToast(job.Error, true);
                Notify();
            }
        }
        private System.Collections.Generic.IEnumerable<object> DeliverSteps(BugReportQueue.Job job)
        {
            if (!Configured) { Queue.Fail(job, "bugs.report.not_configured"); yield break; }
            if (job.Wav != null)
            {
                if (job.Wav == null) { Queue.Fail(job, "bugs.voice.empty"); yield break; }
                job.State = BugReportQueue.Delivery.Recognizing; Notify();
                if (string.IsNullOrEmpty(job.DraftId))
                {
                    using var call = Request("/transcriptions", "POST", job.Wav, "audio/wav");
                    _request = call; yield return call.SendWebRequest(); _request = null;
                    if (!Succeeded(job, call)) yield break;
                    job.DraftId = Read<Draft>(call.downloadHandler.text)?.id;
                    if (string.IsNullOrEmpty(job.DraftId)) { Queue.RetryNetwork(job, Time.realtimeSinceStartup, "bugs.send.failed"); yield break; }
                }
                float until = Time.realtimeSinceStartup + 300;
                while (true)
                {
                    if (Time.realtimeSinceStartup > until) { Queue.Fail(job, "bugs.voice.timeout"); yield break; }
                    yield return new WaitForSecondsRealtime(.5f);
                    using var call = Request("/transcriptions/" + UnityWebRequest.EscapeURL(job.DraftId), "GET");
                    _request = call; yield return call.SendWebRequest(); _request = null;
                    if (!Succeeded(job, call)) yield break;
                    var draft = Read<Draft>(call.downloadHandler.text);
                    if (draft == null) { Queue.RetryNetwork(job, Time.realtimeSinceStartup, "bugs.send.failed"); yield break; }
                    if (draft.status == "failed") { Queue.Fail(job, "bugs.voice.failed"); yield break; }
                    if (draft.status != "complete") continue;
                    var recognized = Bound(draft.text, 12000).Trim();
                    if (recognized.Length == 0) { Queue.Fail(job, "bugs.voice.empty"); yield break; }
                    job.Text = Bound((string.IsNullOrWhiteSpace(job.Text) ? "" : job.Text.Trim() + "\n\n") + recognized, 12000);
                    job.Wav = null; break;
                }
            }
            if (string.IsNullOrWhiteSpace(job.Text)) { Queue.Fail(job, "bugs.voice.empty"); yield break; }
            job.State = BugReportQueue.Delivery.Sending; Notify();
            var payload = new Submission { text = job.Text, context = job.Context, requestId = job.RequestId,
                reportedInVersion = Application.version + " / " + Application.platform,
                attachments = job.Screenshot.Length == 0 ? Array.Empty<Attachment>() :
                    new[] { new Attachment { dataBase64 = Convert.ToBase64String(job.Screenshot) } } };
            using (var call = Request("/reports", "POST", Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload))))
            {
                _request = call; yield return call.SendWebRequest(); _request = null;
                if (!Succeeded(job, call)) yield break;
                var receipt = Read<Receipt>(call.downloadHandler.text);
                if (receipt == null || receipt.id <= 0) { Queue.RetryNetwork(job, Time.realtimeSinceStartup, "bugs.send.failed"); yield break; }
                LastReportId = receipt.id; Queue.Finish(job);
                SetToast("bugs.queue.sent"); Notify();
            }
        }
        private bool Succeeded(BugReportQueue.Job job, UnityWebRequest call)
        {
            if (call.result == UnityWebRequest.Result.Success) return true;
            if (BugReportQueue.Retryable(call.responseCode)) Queue.RetryNetwork(job, Time.realtimeSinceStartup, "bugs.send.failed");
            else Queue.Fail(job, "bugs.send.refused");
            return false;
        }
        private UnityWebRequest Request(string path, string method, byte[] bytes = null, string type = "application/json")
        {
            var call = new UnityWebRequest(_connection.api.TrimEnd('/') + path, method)
                { downloadHandler = new DownloadHandlerBuffer(), timeout = method == "POST" ? 120 : 30 };
            if (bytes != null) call.uploadHandler = new UploadHandlerRaw(bytes);
            call.SetRequestHeader("Authorization", "Bearer " + _connection.token);
            call.SetRequestHeader("X-Flashback-Project", "hexlive"); call.SetRequestHeader("Content-Type", type);
            return call;
        }
        private static T Read<T>(string json) where T : class
        { try { return JsonUtility.FromJson<T>(json); } catch { return null; } }
        private static string Bound(string value, int max) => value == null ? "" : value.Substring(0, Math.Min(max, value.Length));
        private void Notify() => Changed?.Invoke();
        private void InterruptRecording()
        {
            if (!IsRecording) return;
            _microphone.Cancel(); _completedRecording = null; ShowError("bugs.voice.interrupted");
        }
        private void OnApplicationFocus(bool focus) { if (!focus) InterruptRecording(); }
        private void OnApplicationPause(bool paused) { if (paused) InterruptRecording(); }
        private void OnDisable()
        {
            _request?.Abort(); StopAllCoroutines(); _microphone?.Cancel();
            if (Queue.Working != null) Queue.RetryNetwork(Queue.Working, Time.realtimeSinceStartup, "bugs.send.failed");
        }
        private void OnDestroy()
        {
            _request?.Abort(); _microphone?.Dispose();
            if (_instance == this) _instance = null;
        }
        public IEnumerator CaptureScreenshot(Action<byte[], string> done)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D full = null, small = null;
            byte[] png = null; string error = "";
            try
            {
                full = ScreenCapture.CaptureScreenshotAsTexture();
                float scale = Mathf.Min(1f, 1600f / Mathf.Max(full.width, full.height));
                int width = Mathf.Max(1, Mathf.RoundToInt(full.width * scale));
                int height = Mathf.Max(1, Mathf.RoundToInt(full.height * scale));
                var target = RenderTexture.GetTemporary(width, height, 0); var previous = RenderTexture.active;
                try
                {
                    Graphics.Blit(full, target); RenderTexture.active = target;
                    small = new Texture2D(width, height, TextureFormat.RGB24, false);
                    small.ReadPixels(new Rect(0, 0, width, height), 0, 0); small.Apply(); png = small.EncodeToPNG();
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
                if (png.Length > 4 * 1024 * 1024) { png = null; error = "bugs.capture.failed"; }
            }
            catch { error = "bugs.capture.failed"; }
            finally { if (full != null) Destroy(full); if (small != null) Destroy(small); }
            done(png, error);
        }
    }
}
