using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Bootstrap.Remote;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
/// <summary>§67.14/#357: synthetic PCM and actual runtime FMOD/UITK, never record a device.</summary>
public sealed class PlayerVoiceCaptureRuntimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(FmodMicrophoneCapture capture, string name, object value) =>
        typeof(FmodMicrophoneCapture).GetField(name, Private)!.SetValue(capture, value);
    private static object Invoke(FmodMicrophoneCapture capture, string name, params object[] arguments) =>
        typeof(FmodMicrophoneCapture).GetMethod(name, Private)!.Invoke(capture, arguments);
    private static void ExpectMissingDriver(bool readsPosition)
    {
        // The fixture deliberately has no recording device. Only these exact native
        // errors are expected; mix/UITK errors must still fail the runtime test.
        if (readsPosition) LogAssert.Expect(LogType.Error,
            new Regex(@"^\[FMOD\] SystemI::getRecordPosition : Invalid driver ID\.\s*$"));
        LogAssert.Expect(LogType.Error,
            new Regex(@"^\[FMOD\] SystemI::recordStop : Invalid driver ID\.\s*$"));
    }
    private static void Click(Button button)
    {
        Assert.That(button.enabledInHierarchy, Is.True);
        // Exercise the real UITK Clickable and its registered callbacks. Navigation
        // submit is focus-routed in runtime panels and cannot target an unfocused button.
        typeof(Clickable).GetMethod("SimulateSingleClick", Private)!.Invoke(button.clickable,
            new object[] { null, 0 });
    }

    private static byte[] Pcm(float amplitude)
    {
        var sample = (short)(amplitude * short.MaxValue);
        var bytes = new byte[44100 * 2];
        for (var i = 0; i < bytes.Length; i += 2)
        { bytes[i] = (byte)sample; bytes[i + 1] = (byte)(sample >> 8); }
        return bytes;
    }

    [Test]
    public void DefaultTranscriberKeepsLongSpeechAndExplicitlyRejectsOverflow()
    {
        using var transcriber = new DeepgramSpeechTranscriber();
        var bound = typeof(DeepgramSpeechTranscriber).GetMethod("BoundTranscript", Private)!;
        var text = new string('я', 3000);
        Assert.That(bound.Invoke(transcriber, new object[] { text }), Is.EqualTo(text));
        var exception = Assert.Throws<TargetInvocationException>(() =>
            bound.Invoke(transcriber, new object[] { new string('я', 4097) }));
        Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(exception.InnerException.Message, Is.EqualTo("TranscriptTooLong"));
    }

    [UnityTest]
    public IEnumerator SyntheticPcmDrivesCenteredOverlayAndDoneCancelButtons()
    {
        var host = new GameObject("VoiceCaptureLayout");
        var document = host.AddComponent<UIDocument>();
        var template = Resources.Load<PanelSettings>("HexLive/DebugPanelSettings");
        Assert.That(template, Is.Not.Null);
        var settings = Object.Instantiate(template);
        settings.scaleMode = PanelScaleMode.ConstantPixelSize;
        document.panelSettings = settings;
        var capture = new FmodMicrophoneCapture();
        var overlayType = typeof(CharacterPanel).Assembly.GetType("HexLive.UnityPresentation.UI.PlayerVoiceCaptureOverlay")!;
        byte[] finished = null;
        var cancelled = false;
        var overlay = Activator.CreateInstance(overlayType, new object[] {
            document.rootVisualElement, (Action)(() => finished = capture.Stop(out _)),
            (Action)(() => { cancelled = true; capture.Cancel(); }), null });
        void Refresh() => overlayType.GetMethod("Refresh")!.Invoke(overlay, new object[] { capture });
        try
        {
            document.rootVisualElement.style.width = 800;
            document.rootVisualElement.style.height = 600;
            Set(capture, "_recording", true); // Synthetic take, no recordStart.
            Invoke(capture, "AppendRecordedChunk", Pcm(.05f));
            Refresh();
            for (var i = 0; i < 6; i++) yield return null;
            var root = document.rootVisualElement;
            var card = root.Q(className: "agent-capture-card");
            Assert.That(card.panel, Is.Not.Null);
            Assert.That(Vector2.Distance(card.worldBound.center, root.worldBound.center), Is.LessThan(2));
            Assert.That(root.Query(className: "agent-capture-lit").ToList().Count, Is.GreaterThan(0));
            Invoke(capture, "AppendRecordedChunk", Pcm(0));
            Invoke(capture, "AppendRecordedChunk", Pcm(0));
            Assert.That(typeof(FmodMicrophoneCapture).GetProperty("CaptureComplete", Private)!.GetValue(capture), Is.False);
            Invoke(capture, "AppendRecordedChunk", Pcm(0));
            Assert.That(typeof(FmodMicrophoneCapture).GetProperty("CaptureComplete", Private)!.GetValue(capture), Is.True);
            Refresh();
            Assert.That(root.Query(className: "agent-capture-lit").ToList(), Is.Empty);
            ExpectMissingDriver(readsPosition: true);
            Click(root.Q<Button>(className: "agent-capture-done"));
            yield return null;
            Assert.That(finished, Is.Not.Null);
            Assert.That(finished.Length, Is.GreaterThan(44));
            Assert.That(capture.IsRecording, Is.False);
            Set(capture, "_recording", true);
            Refresh();
            ExpectMissingDriver(readsPosition: false);
            Click(root.Q<Button>(className: "agent-capture-cancel"));
            yield return null;
            Assert.That(cancelled, Is.True);
            Assert.That(capture.IsRecording, Is.False);
            Assert.That(root.Q("player-voice-capture").ClassListContains("agent-capture-visible"), Is.False);
        }
        finally
        {
            // Do not mask an earlier assertion with cleanup of the synthetic take.
            Set(capture, "_recording", false);
            capture.Dispose(); Object.Destroy(host); Object.Destroy(settings);
        }
    }

    [UnityTest]
    public IEnumerator ReconnectHandshakeWaitsForAttachmentWithoutLosingTextOrWav()
    {
        var owner = new GameObject("VoiceDeliveryMethods");
        owner.SetActive(false);
        var character = owner.AddComponent<CharacterPanel>();
        var runner = owner.AddComponent<SimulationRunnerBehaviour>();
        var backend = new RemoteSocketBackend("ws://127.0.0.1/unused"); // Never Initialize/connect.
        typeof(RemoteSocketBackend).GetField("_ready", Private)!.SetValue(backend, true);
        typeof(SimulationRunnerBehaviour).GetField("_backend", Private)!.SetValue(runner, backend);
        typeof(CharacterPanel).GetField("_runner", Private)!.SetValue(character, runner);
        var outbox = (PlayerTextOutbox)typeof(CharacterPanel).GetField("_playerTextOutbox", Private)!.GetValue(character);
        var pendingWav = typeof(CharacterPanel).GetField("_capturedPlayerInputs", Private)!.GetValue(character);
        var capturedType = typeof(CharacterPanel).GetNestedType("CapturedPlayerInput", BindingFlags.NonPublic)!;
        var captured = Activator.CreateInstance(capturedType, true);
        capturedType.GetField("NpcId")!.SetValue(captured, 901);
        capturedType.GetField("AttachmentId")!.SetValue(captured, "same-attachment");
        capturedType.GetField("Wav")!.SetValue(captured, new byte[44]);
        pendingWav.GetType().GetMethod("Enqueue")!.Invoke(pendingWav, new[] { captured });
        var dispatch = typeof(RemoteSocketBackend).GetMethod("Dispatch", Private)!;
        void Deliver(byte[] frame) => dispatch.Invoke(backend,
            new object[] { (FrameKind)frame[0], frame.Skip(1).ToArray() });
        void Send() => typeof(CharacterPanel).GetMethod("SendPendingPlayerText", Private)!.Invoke(character, null);
        void StartStt() => typeof(CharacterPanel).GetMethod("StartNextPlayerTranscription", Private)!.Invoke(character, null);
        try
        {
            var state = new AgentStateFrame { NpcId = 901, Attached = true, AttachmentId = "same-attachment" };
            Deliver(AgentWire.AgentState(state));
            var text = new PlayerTextOutbox.Message(901, state.AttachmentId, "kept-message", "сохрани", 77);
            outbox.TryEnqueue(text, out _);
            Deliver(Frame.Handshake(new Handshake { AgentIntegrationEnabled = true, ControlEnabled = true, SttAvailable = true }));
            Assert.That(backend.IsReady, Is.True, "Same-world reconnect keeps the presentation ready.");
            Send(); StartStt();
            Assert.That(outbox.Peek(), Is.SameAs(text));
            Assert.That(pendingWav.GetType().GetProperty("Count")!.GetValue(pendingWav), Is.EqualTo(1));
            Assert.That(typeof(CharacterPanel).GetField("_waitingPlayerConnection", Private)!.GetValue(character), Is.True);
            foreach (var phase in new[] { AgentPhase.Thinking, AgentPhase.Acting, AgentPhase.Speaking })
            {
                state.Phase = phase;
                Deliver(AgentWire.AgentState(state));
                Send();
                typeof(CharacterPanel).GetMethod("ResetAgentVoiceSelection", Private)!.Invoke(character, null);
                Assert.That(outbox.Peek(), Is.SameAs(text), "Phase/selection must not discard an unacknowledged message.");
            }
            StartStt();
            Assert.That(pendingWav.GetType().GetProperty("Count")!.GetValue(pendingWav), Is.EqualTo(0));
            Assert.That(typeof(CharacterPanel).GetField("_pendingTokenCorrelation", Private)!.GetValue(character), Is.Not.EqualTo(0));
            Deliver(AgentWire.AgentTextResult(77, true, "kept-message", ""));
            typeof(CharacterPanel).GetMethod("DrainAgentWireResponses", Private)!.Invoke(character, null);
            Assert.That(outbox.Count, Is.Zero);
            foreach (var attached in new[] { false, true })
            {
                outbox.TryEnqueue(new PlayerTextOutbox.Message(901, "same-attachment", "rejected", "текст", 78), out _);
                state.Attached = attached;
                state.AttachmentId = attached ? "different-attachment" : "same-attachment";
                Deliver(AgentWire.AgentState(state));
                Send();
                Assert.That(outbox.Count, Is.Zero, "Only an explicit detached/different attachment rejects delivery.");
            }
            yield return null;
        }
        finally
        {
            typeof(SimulationRunnerBehaviour).GetField("_backend", Private)!.SetValue(runner, null);
            backend.Shutdown(); Object.Destroy(owner);
        }
    }

    [UnityTest]
    public IEnumerator RealMasterMuteRestoresOnStopCancelDeviceErrorAndDisable()
    {
        var core = FMODUnity.RuntimeManager.CoreSystem;
        Assert.That(core.getMasterChannelGroup(out var master), Is.EqualTo(FMOD.RESULT.OK));
        master.getMute(out var originalMute);
        master.getVolume(out var originalVolume);
        try
        {
            foreach (var initialMute in new[] { false, true })
            foreach (var ending in new[] { "stop", "cancel", "error", "disable" })
            {
                master.setMute(initialMute);
                using var capture = new FmodMicrophoneCapture();
                Assert.That(Invoke(capture, "MuteForCapture"), Is.True);
                master.getMute(out var muted);
                Assert.That(muted, Is.True);
                Set(capture, "_recording", true); // Invalid driver exercises error cleanup, not actual capture.
                Invoke(capture, "AppendRecordedChunk", Pcm(.05f));
                ExpectMissingDriver(readsPosition: ending == "stop" || ending == "error");
                if (ending == "stop") capture.Stop(out _);
                else if (ending == "cancel") capture.Cancel();
                else if (ending == "error") Assert.That(capture.Tick(.1f), Is.True);
                else capture.Dispose();
                master.getMute(out var restored);
                master.getVolume(out var volume);
                Assert.That(restored, Is.EqualTo(initialMute), ending);
                Assert.That(volume, Is.EqualTo(originalVolume), "User mix must not change.");
                Assert.That(capture.IsRecording, Is.False);
                Assert.That(capture.Level, Is.Zero);
            }
            using var owner = new FmodMicrophoneCapture();
            using var other = new FmodMicrophoneCapture();
            var ownerField = typeof(FmodMicrophoneCapture).GetField("_owner", BindingFlags.Static | BindingFlags.NonPublic)!;
            ownerField.SetValue(null, owner);
            try
            {
                Assert.That(other.Start(out var reason), Is.False);
                Assert.That(reason, Is.EqualTo("MicrophoneBusy"));
            }
            finally { ownerField.SetValue(null, null); }
            yield return null;
        }
        finally { master.setMute(originalMute); }
    }
}
}
