using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§159/§160 presentation contracts guarded without booting Unity.</summary>
public sealed class MashaPresentationContractTests
{
    [Test]
    public void MartaSkinSwapPreservesAllFiveAuthoredJanaEyeSlots()
    {
        var source = Read("Assets", "HexLive", "UnityPresentation", "Wearing", "NpcActorView.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain(
                "ApplySkinSet(skinSet, preserveAuthoredEyes: useAuthoredAppearance)"));
            foreach (var slot in new[] { "Irises", "Sclera", "Pupils", "Cornea", "EyeMoisture" })
            {
                Assert.That(source, Does.Contain($"string.Equals(materialName, \"{slot}\""),
                    $"ApplySkinSet(Marta) может заменить авторский слот глаз Яны: {slot}.");
            }
            Assert.That(source, Does.Not.Contain("string.Equals(materialName, \"EyeSocket\""),
                "EyeSocket — материал лица и должен следовать SkinSet Marta (§85).");
        });
    }

    [Test]
    public void PlayerAudioGoesDirectlyToDeepgramAndOnlyTextEntersWatch()
    {
        var microphone = Read("Assets", "HexLive", "UnityPresentation", "Audio",
            "FmodMicrophoneCapture.cs");
        var transcriber = Read("Assets", "HexLive", "UnityPresentation", "Audio",
            "DeepgramSpeechTranscriber.cs");
        var panel = Read("Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.AgentVoice.cs");

        Assert.Multiple(() =>
        {
            Assert.That(microphone, Does.Contain("recordStart"));
            Assert.That(microphone, Does.Not.Contain("UnityEngine.Microphone"));
            Assert.That(transcriber, Does.Contain("api.deepgram.com/v1/listen"));
            Assert.That(transcriber, Does.Contain("AuthenticationHeaderValue(\"Bearer\""));
            Assert.That(transcriber, Does.Contain("model=nova-3"));
            Assert.That(panel, Does.Contain("RequestSttToken"));
            Assert.That(panel, Does.Contain("TranscribeAsync(wav, token"));
            Assert.That(panel, Does.Contain("SendAgentText"));
            Assert.That(panel, Does.Not.Contain("SendAgentAudio"));
            Assert.That(panel, Does.Not.Contain("profileId=masha"),
                "Generic UI не должен выбирать особый профиль.");
        });
    }

    [Test]
    public void DynamicSpeechStaysInsideFmodAndRuntimeVisemePipeline()
    {
        var panel = Read("Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.AgentVoice.cs");
        var actor = Read("Assets", "HexLive", "UnityPresentation", "Wearing",
            "NpcActorView.cs");
        var baker = Read("Assets", "HexLive", "UnityPresentation", "Audio",
            "VoiceVisemeBaker.cs");

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("VectorIcon.Kind.Microphone"));
            Assert.That(panel, Does.Contain("VoiceVisemeBaker.TryBake"));
            Assert.That(panel, Does.Contain("snapshot:/AgentReplyFocus"));
            Assert.That(actor, Does.Contain("FmodSfx.PlayFileTracked"));
            Assert.That(actor, Does.Contain("listenerRelative"));
            Assert.That(baker, Does.Contain("HXLS"));
            Assert.That(baker, Does.Contain("WaveMustBePcm16Mono44100"));
        });
    }

    [Test]
    public void VoiceCaptureDuckHasReproducibleFmodAuthoringAndRuntimeFallback()
    {
        var microphone = Read("Assets", "HexLive", "UnityPresentation", "Audio",
            "FmodMicrophoneCapture.cs");
        var authoring = Read("FMODStudio", "Scripts", "configure_voice_capture_duck.js");

        Assert.Multiple(() =>
        {
            Assert.That(authoring, Does.Contain("snapshot:/VoiceCaptureDuck"));
            Assert.That(authoring, Does.Contain("SnapshotProperty"));
            Assert.That(authoring, Does.Contain("propertyName = \"volume\""));
            Assert.That(authoring, Does.Contain("scopedVolume.value = -18"));
            Assert.That(authoring, Does.Contain("ADSRModulator"));
            Assert.That(authoring, Does.Contain("attackTime = 0.15"));
            Assert.That(authoring, Does.Contain("releaseTime = 0.15"));
            Assert.That(microphone, Does.Contain("DuckLinear = 0.12589254f"));
            Assert.That(microphone, Does.Contain("setFadePointRamp"));
            Assert.That(microphone, Does.Contain("StopFallbackDuck();"));
        });
    }

    [Test]
    public void CharacterPanelReadsTransientRelationAndJournalFromAgentState()
    {
        var agentPanel = Read("Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.AgentVoice.cs");
        var journalPanel = Read("Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.Journal.cs");
        var mainPanel = Read("Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.cs");

        Assert.Multiple(() =>
        {
            Assert.That(agentPanel, Does.Contain("TryGetAgentState"));
            Assert.That(agentPanel, Does.Contain("state.RelationView"));
            Assert.That(agentPanel, Does.Contain("state.JournalEntry"));
            Assert.That(agentPanel, Does.Not.Contain("/memory"));
            Assert.That(agentPanel, Does.Not.Contain("MollyBridge"));
            Assert.That(journalPanel, Does.Contain("_agentJournal"));
            Assert.That(mainPanel, Does.Contain("_agentFamiliarity"));
        });
    }

    private static string Read(params string[] segments)
    {
        var path = RepoPaths.Root;
        foreach (var segment in segments) path = Path.Combine(path, segment);
        return File.ReadAllText(path);
    }
}

}
