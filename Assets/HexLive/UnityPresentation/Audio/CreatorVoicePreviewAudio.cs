using HexLive.UnityPresentation.UI;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
// The same Player-owned localized FMOD files and Voices volume as NPC speech.
public sealed class CreatorVoicePreviewAudio : CreatorVoicePreview.IAudio
{
    private FmodSfx.Loop _playing;
    public void Prepare() => FmodSfx.Prewarm();
    public string[] Groups(string voice) => FmodSfx.VoicePreviewGroups(voice);
    public bool Ready(string group) => FmodSfx.HasSound(group);
    public bool Start(string group)
    {
        Stop(); _playing = FmodSfx.PlayTracked(group, Vector3.zero, 1f, listenerRelative: true);
        return _playing.IsValid;
    }
    public bool IsPlaying => FmodSfx.IsPlaying(ref _playing);
    public bool Failed => FmodSfx.InitializationFailed;
    public void Stop() => FmodSfx.StopLoop(ref _playing);
}
}
