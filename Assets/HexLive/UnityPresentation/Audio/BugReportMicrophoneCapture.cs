using System;
using System.Runtime.InteropServices;

namespace HexLive.UnityPresentation.Audio
{
    // §114.8 / §161: FMOD records on native platforms; HTML5 needs browser media input.
    public sealed class BugReportMicrophoneCapture : IDisposable
    {
        public BugReportMicrophoneCapture(float nativeMaxSeconds = 90f, bool nativeFinishOnSilence = false)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _maxSeconds = (int)Math.Clamp(nativeMaxSeconds, 1f, 90f);
#else
            _native = new FmodMicrophoneCapture(nativeMaxSeconds, nativeFinishOnSilence);
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int HexLiveBugMicStart(int maxSeconds);
        [DllImport("__Internal")] private static extern int HexLiveBugMicState();
        [DllImport("__Internal")] private static extern float HexLiveBugMicLevel();
        [DllImport("__Internal")] private static extern int HexLiveBugMicStop();
        [DllImport("__Internal")] private static extern int HexLiveBugMicCopy([Out] byte[] destination, int capacity);
        [DllImport("__Internal")] private static extern void HexLiveBugMicCancel();
        private static BugReportMicrophoneCapture _browserOwner;
        private readonly int _maxSeconds;
        private bool _recording, _ownsMute, _previousMute;
        private FMOD.ChannelGroup _master;
        public bool IsRecording => _recording;
        public bool IsReady => _recording && HexLiveBugMicState() == 2;
        public float Level => _recording ? HexLiveBugMicLevel() : 0;
        public bool Start(out string error)
        {
            error = "";
            if (_recording) return true;
            if (_browserOwner != null) { error = "MicrophoneBusy"; return false; }
            try
            {
                if (FMODUnity.RuntimeManager.CoreSystem.getMasterChannelGroup(out _master) != FMOD.RESULT.OK ||
                    _master.getMute(out _previousMute) != FMOD.RESULT.OK) return false;
                _ownsMute = true;
                _browserOwner = this;
                if (_master.setMute(true) != FMOD.RESULT.OK || HexLiveBugMicStart(_maxSeconds) != 1)
                { Cancel(); error = "MicrophoneUnavailable"; return false; }
                _recording = true; return true;
            }
            catch { Cancel(); error = "MicrophoneUnavailable"; return false; }
        }
        public bool Tick(float delta) => _recording && (HexLiveBugMicState() < 0 || HexLiveBugMicState() == 3);
        public byte[] Stop(out string error)
        {
            error = "";
            if (!_recording || !ReferenceEquals(_browserOwner, this)) { error = "NoRecording"; return null; }
            try
            {
                int size = HexLiveBugMicStop();
                if (size < 44 || size > _maxSeconds * 192000 * 2 + 44) { error = "NoRecording"; return null; }
                var wav = new byte[size];
                if (HexLiveBugMicCopy(wav, size) != size) { error = "NoRecording"; return null; }
                return wav;
            }
            finally { Cancel(); }
        }
        public void Cancel()
        {
            _recording = false;
            try
            {
                if (ReferenceEquals(_browserOwner, this))
                {
                    _browserOwner = null;
                    HexLiveBugMicCancel();
                }
            }
            finally
            {
                if (_ownsMute)
                {
                    _ownsMute = false;
                    try { _master.setMute(_previousMute); }
                    finally { _master = default; }
                }
            }
        }
        public void Dispose() => Cancel();
#else
        private readonly FmodMicrophoneCapture _native;
        public bool IsRecording => _native.IsRecording;
        public bool IsReady => _native.IsRecording;
        public float Level => _native.Level;
        public bool Start(out string error) => _native.Start(out error);
        public bool Tick(float delta) => _native.Tick(delta);
        public byte[] Stop(out string error) => _native.Stop(out error);
        public void Cancel() => _native.Cancel();
        public void Dispose() => _native.Dispose();
#endif
    }
}
