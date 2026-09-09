#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>§159: PCM16 mono microphone capture through FMOD Core only.</summary>
    public sealed class FmodMicrophoneCapture : IDisposable
    {
        // Both voice entry points share the FMOD device and master-mute owner.
        private static FmodMicrophoneCapture? _owner;
        private const float MaxSeconds = 30f;
        private const float SilenceToFinishSeconds = 3f;
        private const float VoiceRms = 0.008f;
        private const float MinimumSeconds = 0.25f;

        private readonly List<byte> _pcm = new(44100 * 2 * 8);
        private FMOD.Sound _sound;
        private int _driver = -1;
        private int _sampleRate = 44100;
        private uint _lastPosition;
        private uint _bufferBytes;
        private float _elapsed;
        private float _silence;
        private bool _heardVoice;
        private bool _recording;
        private FMOD.ChannelGroup _captureMaster;
        private bool _restoreMasterMute;
        private bool _ownsMasterMute;

        public bool IsRecording => _recording;
        public float Level { get; private set; }

        public bool Start(out string error)
        {
            error = string.Empty;
            if (_recording) return true;
            // Requesting here would interrupt the conversation. Startup owns the OS prompt.
            try
            {
                if (!MicrophonePermission.Granted) { error = "MicrophonePermissionDenied"; return false; }
            }
            catch (Exception) { error = "MicrophonePermissionUnavailable"; return false; }
            if (_owner != null && _owner != this) { error = "MicrophoneBusy"; return false; }
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.getRecordNumDrivers(out var drivers, out var connected) != FMOD.RESULT.OK ||
                drivers == 0 || connected == 0)
            {
                error = "NoMicrophone";
                return false;
            }

            _driver = -1;
            for (var i = 0; i < drivers; i++)
            {
                if (core.getRecordDriverInfo(i, out _, out var rate, out _, out _, out var state) ==
                        FMOD.RESULT.OK && state.HasFlag(FMOD.DRIVER_STATE.CONNECTED))
                {
                    _driver = i;
                    _sampleRate = Mathf.Clamp(rate, 8000, 48000);
                    break;
                }
            }
            if (_driver < 0)
            {
                error = "NoMicrophone";
                return false;
            }

            _bufferBytes = (uint)(_sampleRate * 2 * (MaxSeconds + 1f));
            var info = new FMOD.CREATESOUNDEXINFO
            {
                cbsize = Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                length = _bufferBytes,
                numchannels = 1,
                defaultfrequency = _sampleRate,
                format = FMOD.SOUND_FORMAT.PCM16
            };
            var result = core.createSound(string.Empty,
                FMOD.MODE.OPENUSER | FMOD.MODE.LOOP_NORMAL, ref info, out _sound);
            if (result != FMOD.RESULT.OK || core.recordStart(_driver, _sound, true) != FMOD.RESULT.OK)
            {
                _sound.release();
                _sound = default;
                error = "MicrophoneStartFailed";
                return false;
            }

            _pcm.Clear();
            _lastPosition = 0;
            _elapsed = 0f;
            _silence = 0f;
            _heardVoice = false;
            _recording = true;
            _owner = this;
            Level = 0f;
            try
            {
                if (MuteForCapture()) return true;
            }
            catch (Exception) { /* Never leave a take running after mute setup fails. */ }
            EndRecording();
            error = "MicrophoneMuteFailed";
            return false;
        }

        /// <summary>Returns true when VAD/max duration has completed the take.</summary>
        public bool Tick(float unscaledDeltaTime)
        {
            if (!_recording) return false;
            _elapsed += Mathf.Max(0f, unscaledDeltaTime);
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.getRecordPosition(_driver, out var position) != FMOD.RESULT.OK)
            {
                Cancel();
                return true;
            }

            var from = _lastPosition * 2;
            var to = position * 2;
            var length = to >= from ? to - from : _bufferBytes - from + to;
            if (length > 0)
            {
                var chunk = ReadRing(from, length);
                AppendRecordedChunk(chunk);
            }
            _lastPosition = position;
            return CaptureComplete;
        }

        private bool CaptureComplete => _elapsed >= MaxSeconds || (_heardVoice && _silence >= SilenceToFinishSeconds);

        private void AppendRecordedChunk(byte[] chunk)
        {
            _pcm.AddRange(chunk);
            var rms = Rms(chunk);
            Level = Mathf.Clamp01((20f * Mathf.Log10(Mathf.Max(rms, 0.0001f)) + 60f) / 45f);
            if (rms >= VoiceRms)
            {
                _heardVoice = true;
                _silence = 0f;
            }
            else if (_heardVoice)
            {
                _silence += chunk.Length / (_sampleRate * 2f);
            }
        }

        public byte[]? Stop(out string error)
        {
            error = string.Empty;
            if (!_recording) return null;
            try
            {
                CaptureTail();
            }
            finally
            {
                EndRecording();
            }

            if (_pcm.Count < _sampleRate * 2 * MinimumSeconds)
            {
                error = "TooShort";
                return null;
            }
            if (!_heardVoice)
            {
                error = "TooQuiet";
                return null;
            }
            return Wav(_pcm.ToArray(), _sampleRate);
        }

        public void Cancel()
        {
            EndRecording();
            _pcm.Clear();
        }

        private void EndRecording()
        {
            try
            {
                if (_recording)
                {
                    _recording = false;
                    try { FMODUnity.RuntimeManager.CoreSystem.recordStop(_driver); }
                    finally
                    {
                        _sound.release();
                        _sound = default;
                    }
                }
            }
            finally
            {
                if (_owner == this) _owner = null;
                // Master mute is independent of the user's per-category volumes.
                RestoreCaptureMix();
                Level = 0f;
            }
        }

        private bool MuteForCapture()
        {
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.getMasterChannelGroup(out _captureMaster) != FMOD.RESULT.OK ||
                _captureMaster.getMute(out _restoreMasterMute) != FMOD.RESULT.OK)
                return false;
            _ownsMasterMute = true;
            return _captureMaster.setMute(true) == FMOD.RESULT.OK;
        }

        private void RestoreCaptureMix()
        {
            if (!_ownsMasterMute) return;
            try { _captureMaster.setMute(_restoreMasterMute); }
            finally { _ownsMasterMute = false; _captureMaster = default; }
        }

        private void CaptureTail()
        {
            if (FMODUnity.RuntimeManager.CoreSystem.getRecordPosition(_driver, out var position) !=
                FMOD.RESULT.OK) return;
            var from = _lastPosition * 2;
            var to = position * 2;
            var length = to >= from ? to - from : _bufferBytes - from + to;
            if (length > 0) _pcm.AddRange(ReadRing(from, length));
            _lastPosition = position;
        }

        private byte[] ReadRing(uint offset, uint length)
        {
            var bytes = new byte[length];
            if (_sound.@lock(offset, length, out var p1, out var p2, out var l1, out var l2) !=
                FMOD.RESULT.OK) return Array.Empty<byte>();
            try
            {
                if (l1 > 0) Marshal.Copy(p1, bytes, 0, (int)l1);
                if (l2 > 0) Marshal.Copy(p2, bytes, (int)l1, (int)l2);
            }
            finally
            {
                _sound.unlock(p1, p2, l1, l2);
            }
            return bytes;
        }

        private static float Rms(IReadOnlyList<byte> bytes)
        {
            if (bytes.Count < 2) return 0f;
            double square = 0;
            var samples = bytes.Count / 2;
            for (var i = 0; i < samples; i++)
            {
                var value = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8);
                var normalized = value / 32768d;
                square += normalized * normalized;
            }
            return (float)Math.Sqrt(square / samples);
        }

        private static byte[] Wav(byte[] pcm, int sampleRate)
        {
            using var stream = new MemoryStream(44 + pcm.Length);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
            writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
            writer.Flush();
            return stream.ToArray();
        }


        public void Dispose() => Cancel();
    }
}
