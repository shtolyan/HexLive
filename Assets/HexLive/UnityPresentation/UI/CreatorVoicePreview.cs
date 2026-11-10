using System;
using System.Linq;

namespace HexLive.UnityPresentation.UI
{
// Presentation-only request state. World/account state changes only on Submit.
public sealed class CreatorVoicePreview : IDisposable
{
    public enum PlaybackState { Idle, Loading, Playing, Failed }
    public interface IAudio
    {
        void Prepare();
        string[] Groups(string voice);
        bool Ready(string group);
        bool Start(string group);
        bool IsPlaying { get; }
        bool Failed { get; }
        void Stop();
    }
    private readonly IAudio _audio;
    private readonly Func<float> _clock;
    private readonly Func<int, int> _pick;
    private string _group, _lastGroup, _lastVoice;
    private float _deadline;
    private bool _disposed;
    public PlaybackState State { get; private set; }
    public string Voice { get; private set; }
    public CreatorVoicePreview(IAudio audio, Func<float> clock, Func<int, int> pick = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        var random = new Random(); _pick = pick ?? random.Next;
    }
    public void Play(string voice)
    {
        if (_disposed) return;
        Stop(); Voice = voice;
        if (string.IsNullOrEmpty(voice)) { State = PlaybackState.Failed; return; }
        State = PlaybackState.Loading; _deadline = _clock() + 20f;
        _audio.Prepare(); Tick();
    }
    public void Tick()
    {
        if (_disposed) return;
        if (State == PlaybackState.Playing)
        {
            if (!_audio.IsPlaying) State = PlaybackState.Idle;
            return;
        }
        if (State != PlaybackState.Loading) return;
        if (_audio.Failed || _clock() >= _deadline) { _audio.Stop(); State = PlaybackState.Failed; return; }
        if (_group == null)
        {
            var groups = (_audio.Groups(Voice) ?? Array.Empty<string>()).Distinct().ToArray();
            if (groups.Length == 0) return; // Web manifest can still be loading.
            var choices = Voice == _lastVoice && groups.Length > 1 ? groups.Where(g => g != _lastGroup).ToArray() : groups;
            _group = choices[Math.Max(0, Math.Min(choices.Length - 1, _pick(choices.Length)))];
        }
        // Retry this exact chosen group, rather than silently choosing another
        // file each frame while WebAudioFiles downloads the first sample.
        if (!_audio.Ready(_group)) return;
        if (!_audio.Start(_group)) { State = PlaybackState.Failed; return; }
        _lastVoice = Voice; _lastGroup = _group; State = PlaybackState.Playing;
    }
    public void Stop()
    {
        _audio.Stop(); _group = null; State = PlaybackState.Idle;
    }
    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }
}
}
