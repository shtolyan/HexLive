using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;

namespace HexLive.Tests
{
public sealed class CreatorVoicePreviewTests
{
    private sealed class Audio : CreatorVoicePreview.IAudio
    {
        public bool Available, Playing, Failure, StartSucceeds = true;
        public string LastReadyGroup;
        public int Stops;
        public readonly List<string> Started = new();
        public string[] Choices = { "a", "b" };
        public void Prepare() { }
        public string[] Groups(string voice) => Array.ConvertAll(Choices, g => voice + "_" + g);
        public bool Ready(string group) { LastReadyGroup = group; return Available; }
        public bool Start(string group) { if (!StartSucceeds) return false; Started.Add(group); Playing = true; return true; }
        public bool IsPlaying => Playing;
        public bool Failed => Failure;
        public void Stop() { Playing = false; Stops++; }
    }
    [Test]
    public void LazyDownloadKeepsOneChosenGroupUntilItActuallyPlays()
    {
        var audio = new Audio(); float now = 0; using var preview = new CreatorVoicePreview(audio, () => now, _ => 0);
        preview.Play("marta"); var chosen = audio.LastReadyGroup;
        preview.Tick(); preview.Tick();
        Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Loading));
        Assert.That(audio.LastReadyGroup, Is.EqualTo(chosen)); Assert.That(audio.Started, Is.Empty);
        audio.Available = true; preview.Tick();
        Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Playing)); Assert.That(audio.Started, Is.EqualTo(new[] { chosen }));
    }
    [Test]
    public void SwitchingVoiceWhileLoadingNeverStartsThePreviousVoice()
    {
        var audio = new Audio(); using var preview = new CreatorVoicePreview(audio, () => 0, _ => 0);
        preview.Play("marta"); preview.Play("molly"); audio.Available = true; preview.Tick();
        Assert.That(audio.Started, Is.EqualTo(new[] { "molly_a" }));
        Assert.That(preview.Voice, Is.EqualTo("molly"));
    }
    [Test]
    public void TryingAnotherLineStopsPlaybackAndAvoidsTheLastGroup()
    {
        var audio = new Audio { Available = true }; using var preview = new CreatorVoicePreview(audio, () => 0, _ => 0);
        preview.Play("jana"); preview.Play("jana");
        Assert.That(audio.Started, Is.EqualTo(new[] { "jana_a", "jana_b" })); Assert.That(audio.Stops, Is.GreaterThanOrEqualTo(2));
    }
    [Test]
    public void StopWhileLoadingCancelsAnyFuturePlayback()
    {
        var audio = new Audio(); using var preview = new CreatorVoicePreview(audio, () => 0);
        preview.Play("marta"); preview.Stop(); audio.Available = true; preview.Tick();
        Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Idle)); Assert.That(audio.Started, Is.Empty);
    }
    [Test]
    public void DisposeStopsCurrentPlaybackAndCannotRestart()
    {
        var audio = new Audio { Available = true }; var preview = new CreatorVoicePreview(audio, () => 0, _ => 0);
        preview.Play("jolly"); preview.Dispose(); preview.Play("marta"); preview.Tick();
        Assert.That(audio.Playing, Is.False); Assert.That(audio.Started, Is.EqualTo(new[] { "jolly_a" }));
    }
    [Test]
    public void EndOfClipReturnsToIdle()
    {
        var audio = new Audio { Available = true }; using var preview = new CreatorVoicePreview(audio, () => 0);
        preview.Play("kshishtof"); audio.Playing = false; preview.Tick();
        Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Idle));
    }
    [Test]
    public void TimeoutAndExplicitRetryRecoverWithoutBlockingCreation()
    {
        var audio = new Audio(); float now = 0; using var preview = new CreatorVoicePreview(audio, () => now, _ => 0);
        preview.Play("masha"); now = 21; preview.Tick();
        Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Failed));
        audio.Available = true; preview.Play("masha");
        Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Playing));
    }
    [Test]
    public void EmptyManifestCanArriveLaterAndFailedAudioDoesNotRemainBusy()
    {
        var audio = new Audio { Choices = Array.Empty<string>() }; using var preview = new CreatorVoicePreview(audio, () => 0, _ => 0);
        preview.Play("marta"); Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Loading));
        audio.Choices = new[] { "greeting" }; audio.Available = true; preview.Tick();
        Assert.That(audio.Started, Is.EqualTo(new[] { "marta_greeting" }));
        audio.Failure = true; preview.Play("molly"); Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Failed));
    }
    [Test]
    public void MissingChannelFailsInsteadOfClaimingPlayback()
    {
        var audio = new Audio { Available = true, StartSucceeds = false }; using var preview = new CreatorVoicePreview(audio, () => 0);
        preview.Play("marta"); Assert.That(preview.State, Is.EqualTo(CreatorVoicePreview.PlaybackState.Failed)); Assert.That(audio.Playing, Is.False);
    }
}
}
