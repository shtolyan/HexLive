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
        private const float MaxSeconds = 30f;
        private const float SilenceToFinishSeconds = 0.8f;
        private const float VoiceRms = 0.008f;
        private const float MinimumSeconds = 0.25f;
        private const float DuckLinear = 0.12589254f; // -18 dB
        private const float DuckFadeSeconds = 0.15f;

        private readonly List<byte> _pcm = new(44100 * 2 * 8);
        private FMOD.Sound _sound;
        private FMOD.Studio.EventInstance _duck;
        private FMOD.Studio.Bus _fallbackDuckBus;
        private FMOD.ChannelGroup _fallbackDuckGroup;
        private float _fallbackRestoreVolume = 1f;
        private int _fallbackSampleRate = 44100;
        private bool _fallbackDucking;
        private int _driver = -1;
        private int _sampleRate = 44100;
        private uint _lastPosition;
        private uint _bufferBytes;
        private float _elapsed;
        private float _silence;
        private bool _heardVoice;
        private bool _recording;

        public bool IsRecording => _recording;

        public bool Start(out string error)
        {
            error = string.Empty;
            if (_recording) return true;
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.getRecordNumDrivers(out var drivers, out var connected) != FMOD.RESULT.OK ||
                drivers == 0 || connected == 0)
            {
                error = "NoMicrophone";
                return false;
            }

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
            StartDuck();
            return true;
        }

        /// <summary>Returns true when VAD/max duration has completed the take.</summary>
        public bool Tick(float unscaledDeltaTime)
        {
            if (!_recording) return false;
            _elapsed += Mathf.Max(0f, unscaledDeltaTime);
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.getRecordPosition(_driver, out var position) != FMOD.RESULT.OK)
                return true;

            var from = _lastPosition * 2;
            var to = position * 2;
            var length = to >= from ? to - from : _bufferBytes - from + to;
            if (length > 0)
            {
                var chunk = ReadRing(from, length);
                _pcm.AddRange(chunk);
                var rms = Rms(chunk);
                if (rms >= VoiceRms)
                {
                    _heardVoice = true;
                    _silence = 0f;
                }
                else if (_heardVoice)
                {
                    _silence += unscaledDeltaTime;
                }
            }
            _lastPosition = position;
            return _elapsed >= MaxSeconds || (_heardVoice && _silence >= SilenceToFinishSeconds);
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
            if (!_heardVoice || Rms(_pcm) < 0.004f)
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
                    FMODUnity.RuntimeManager.CoreSystem.recordStop(_driver);
                    _sound.release();
                    _sound = default;
                    _recording = false;
                }
            }
            finally
            {
                // The mix is restored even if Core reports a device failure.
                StopDuck();
            }
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

        private void StartDuck()
        {
            try
            {
                _duck = FMODUnity.RuntimeManager.CreateInstance("snapshot:/VoiceCaptureDuck");
                if (_duck.isValid() && _duck.start() == FMOD.RESULT.OK) return;
            }
            catch (Exception)
            {
                _duck = default;
            }

            // The committed authoring script creates the snapshot. Keep capture usable when an
            // older local bank is loaded: this still ducks through FMOD, with the same gain and
            // DSP-clock ramp, and is always restored by StopDuck.
            if (TryStartFallbackDuck())
                Debug.LogWarning("[AgentVoice] VoiceCaptureDuck bank entry is unavailable; using FMOD master-bus fallback");
            else
                Debug.LogWarning("[AgentVoice] FMOD snapshot VoiceCaptureDuck is unavailable");
        }

        private void StopDuck()
        {
            if (_duck.isValid())
            {
                _duck.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                _duck.release();
                _duck = default;
            }
            StopFallbackDuck();
        }

        private bool TryStartFallbackDuck()
        {
            var studio = FMODUnity.RuntimeManager.StudioSystem;
            if (studio.getBus("bus:/", out _fallbackDuckBus) != FMOD.RESULT.OK ||
                _fallbackDuckBus.lockChannelGroup() != FMOD.RESULT.OK)
                return false;
            if (_fallbackDuckBus.getChannelGroup(out _fallbackDuckGroup) != FMOD.RESULT.OK ||
                _fallbackDuckGroup.getVolume(out _fallbackRestoreVolume) != FMOD.RESULT.OK ||
                FMODUnity.RuntimeManager.CoreSystem.getSoftwareFormat(
                    out _fallbackSampleRate, out _, out _) != FMOD.RESULT.OK ||
                !ScheduleFallbackRamp(_fallbackRestoreVolume, _fallbackRestoreVolume * DuckLinear))
            {
                _fallbackDuckBus.unlockChannelGroup();
                _fallbackDuckBus = default;
                _fallbackDuckGroup = default;
                return false;
            }
            _fallbackDucking = true;
            return true;
        }

        private void StopFallbackDuck()
        {
            if (!_fallbackDucking) return;
            try
            {
                var current = _fallbackRestoreVolume * DuckLinear;
                _fallbackDuckGroup.getVolume(out current);
                ScheduleFallbackRamp(current, _fallbackRestoreVolume);
            }
            finally
            {
                _fallbackDuckBus.unlockChannelGroup();
                _fallbackDuckBus = default;
                _fallbackDuckGroup = default;
                _fallbackDucking = false;
            }
        }

        private bool ScheduleFallbackRamp(float from, float to)
        {
            if (_fallbackDuckGroup.getDSPClock(out var dspClock, out var parentClock) !=
                FMOD.RESULT.OK) return false;
            var now = parentClock != 0 ? parentClock : dspClock;
            var end = now + (ulong)Math.Max(1,
                Math.Round(_fallbackSampleRate * DuckFadeSeconds));
            // Replace only the short interval owned by this capture; unrelated later fades stay.
            _fallbackDuckGroup.removeFadePoints(now, end);
            return _fallbackDuckGroup.addFadePoint(now, Mathf.Clamp01(from)) == FMOD.RESULT.OK &&
                   _fallbackDuckGroup.setFadePointRamp(end, Mathf.Clamp01(to)) == FMOD.RESULT.OK;
        }

        public void Dispose() => Cancel();
    }
}
