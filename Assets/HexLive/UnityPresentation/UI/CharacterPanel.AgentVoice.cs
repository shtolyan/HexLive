#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>§160 generic MCP attachment UI with no authored-profile dependency.</summary>
    public sealed partial class CharacterPanel
    {
        private enum AgentVoiceUiState
        {
            Offline, NoStt, NoMicrophone, Ready, Listening, Recognizing, Thinking, Speaking
        }

        [Serializable]
        private sealed class AgentRelationPayload
        {
            public float familiarity;
            public float trust;
            public float affinity;
        }

        private VisualElement? _agentVoiceButton;
        private PlayerVoiceCaptureOverlay? _agentCaptureOverlay;
        private VectorIcon? _agentVoiceGlyph;
        private Label? _agentVoiceStateLabel;
        private VoiceResponsePanel? _voiceResponses;
        private NpcSnapshot? _agentVoiceNpc;
        private AgentStateFrame? _selectedAgentState;
        private FmodMicrophoneCapture? _agentMicrophone;
        private IPlayerSpeechTranscriber? _agentTranscriber;
        private CancellationTokenSource? _agentCts;
        private AgentVoiceUiState _agentVoiceUiState = AgentVoiceUiState.Offline;
        private readonly ConcurrentQueue<Action> _agentMainThread = new();
        private readonly HashSet<string> _announcedAttachments = new(StringComparer.Ordinal);
        private int _agentCorrelation;
        private int _pendingTokenCorrelation;
        private byte[]? _pendingPlayerWav;
        private const int MaxPendingCaptures = 16;
        private sealed class CapturedPlayerInput
        {
            public int NpcId;
            public string AttachmentId = string.Empty;
            public byte[] Wav = Array.Empty<byte>();
        }
        private readonly Queue<CapturedPlayerInput> _capturedPlayerInputs = new();
        private CapturedPlayerInput? _activePlayerInput;
        private bool _sttInFlight;
        private string _cachedSttToken = string.Empty;
        private long _cachedSttExpiry;
        private readonly HashSet<int> _pendingTextResults = new();
        private string _agentCachePath = string.Empty;
        private FMOD.Studio.EventInstance _agentReplyFocus;
        private float _agentReplyFocusUntil;
        private Wearing.NpcActorView? _agentReplyActor;
        private string _agentReplyWav = string.Empty;
        private bool _awaitingAgentTurn;
        private long _voiceSubmittedRevision;
        private float _voiceDeadline;
        private readonly SemaphoreSlim _agentBakeGate = new(1, 1);
        private sealed class PreparedAgentSpeech
        {
            public AgentSpeechBeginFrame Metadata = null!;
            public string WavPath = string.Empty;
            public string VisPath = string.Empty;
            public float Deadline;
        }
        private readonly Queue<PreparedAgentSpeech> _preparedAgentSpeech = new();

        // Public local view supplied by the attached agent. Existing relation
        // and journal panels consume these generic values without reading a
        // profile, local workspace or world-save companion object.
        private int _agentAttachedNpcId = -1;
        private bool _agentRelationReady;
        private float _agentFamiliarity;
        private float _agentTrust;
        private float _agentAffinity;
        private int _agentRelationTick = -1;
        private bool _agentJournalReady;
        private readonly List<JournalEntrySnapshot> _agentJournal = new();
        private readonly List<JournalEntrySnapshot> _agentMergedJournal = new();
        private string _agentJournalText = string.Empty;

        private void EnableAgentVoice()
        {
            if (_agentCts != null) return;
            _agentCts = new CancellationTokenSource();
            _agentTranscriber = new DeepgramSpeechTranscriber();
            _agentCachePath = Path.Combine(Application.temporaryCachePath, "HexLiveAgentVoice");
        }

        private void DisableAgentVoice()
        {
            _agentMicrophone?.Cancel();
            _agentMicrophone?.Dispose();
            _agentMicrophone = null;
            _agentCts?.Cancel();
            _agentCts?.Dispose();
            _agentCts = null;
            _agentTranscriber?.Dispose();
            _agentTranscriber = null;
            StopAgentReplyFocus();
            _pendingPlayerWav = null;
            _capturedPlayerInputs.Clear();
            _activePlayerInput = null;
            _sttInFlight = false;
            _cachedSttToken = string.Empty;
            _cachedSttExpiry = 0;
            _pendingTextResults.Clear();
            _pendingTokenCorrelation = 0;
            while (_agentMainThread.TryDequeue(out _)) { }
            _preparedAgentSpeech.Clear();
            ClearAgentSelection();
        }

        private void ResetAgentVoiceSelection()
        {
            _awaitingAgentTurn = false;
            _agentMicrophone?.Cancel();
            _agentVoiceNpc = null;
            _selectedAgentState = null;
            _agentVoiceUiState = AgentVoiceUiState.Offline;
            _agentCaptureOverlay?.Hide();
        }

        private void ClearAgentSelection()
        {
            ResetAgentVoiceSelection();
            _agentAttachedNpcId = -1;
            _agentRelationReady = false;
            _agentJournalReady = false;
            _agentJournal.Clear();
            _agentMergedJournal.Clear();
            _agentJournalText = string.Empty;
        }

        private void RefreshAgentVoice(NpcSnapshot npc)
        {
            _agentVoiceNpc = npc;
            if (_runner == null || !_runner.TryGetAgentState(npc.Id, out var state) || !state.Attached)
            {
                _selectedAgentState = null;
                if (_agentAttachedNpcId == npc.Id.Value)
                {
                    _agentAttachedNpcId = -1;
                    _agentRelationReady = false;
                    _agentJournalReady = false;
                    _journalSig = int.MinValue;
                    _relationSig.Clear();
                }
                return;
            }

            _selectedAgentState = state;
            _agentAttachedNpcId = npc.Id.Value;
            if (_announcedAttachments.Add(state.AttachmentId))
            {
                ShowAgentSubtitle(string.Format(Loc.Get("agent.connected"), state.DisplayName), 4f);
            }
            if (_thoughtValue != null)
            {
                _thoughtValue.text = state.Phase == AgentPhase.Thinking
                    ? Loc.Get("agent.thinking")
                    : string.IsNullOrWhiteSpace(state.IntentSummary)
                        ? _thoughtValue.text
                        : state.IntentSummary;
            }
            ParseAgentRelation(state.RelationView);
            RefreshAgentJournal(state.JournalEntry, npc.Id.Value);

            if (_awaitingAgentTurn && state.Revision > _voiceSubmittedRevision &&
                state.Phase is AgentPhase.Ready or AgentPhase.Speaking or AgentPhase.Error)
                _awaitingAgentTurn = false;

            if (_agentVoiceUiState is not (AgentVoiceUiState.Listening or
                AgentVoiceUiState.Recognizing) && _pendingTokenCorrelation == 0 && !_awaitingAgentTurn)
            {
                _agentVoiceUiState = state.Phase switch
                {
                    AgentPhase.Thinking or AgentPhase.Acting => AgentVoiceUiState.Thinking,
                    AgentPhase.Speaking => AgentVoiceUiState.Speaking,
                    _ => AgentVoiceUiState.Ready,
                };
            }
        }

        private void TickAgentVoice()
        {
            while (_agentMainThread.TryDequeue(out var action)) action();
            _voiceResponses?.Tick();
            if (_agentReplyFocusUntil > 0f && (Time.unscaledTime >= _agentReplyFocusUntil ||
                _agentReplyActor == null || !_agentReplyActor.IsExternalVoicePlaying(_agentReplyWav)))
                StopAgentReplyFocus();
            if (_voiceDeadline > 0 && Time.unscaledTime >= _voiceDeadline &&
                _pendingTokenCorrelation != 0)
            {
                _pendingTokenCorrelation = 0;
                _pendingPlayerWav = null;
                _activePlayerInput = null;
                _awaitingAgentTurn = false;
                _agentVoiceUiState = AgentVoiceUiState.Ready;
                ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
            }

            DrainAgentWireResponses();
            StartNextPlayerTranscription();
            PresentPreparedAgentSpeech();
            if (_agentMicrophone?.IsRecording == true)
            {
                if (!CaptureStillEligible())
                {
                    _agentMicrophone.Cancel();
                    _agentVoiceUiState = AgentVoiceUiState.Ready;
                }
                else if (_agentMicrophone.Tick(Time.unscaledDeltaTime))
                {
                    FinishAgentCapture();
                }
            }
            RefreshAgentVoiceButton();
            RefreshCaptureOverlay();
        }

        private void RefreshCaptureOverlay()
        {
            if (_agentMicrophone?.IsRecording != true) { _agentCaptureOverlay?.Hide(); return; }
            _agentCaptureOverlay ??= new PlayerVoiceCaptureOverlay(_root, FinishAgentCapture, () =>
            {
                _agentMicrophone?.Cancel();
                _agentVoiceUiState = AgentVoiceUiState.Ready;
            });
            _agentCaptureOverlay.Refresh(_agentMicrophone);
        }

        private VisualElement BuildAgentVoiceButton()
        {
            var button = new VisualElement();
            button.style.position = Position.Absolute;
            button.style.right = 12f;
            button.style.top = 144f;
            button.style.width = 58f;
            button.style.height = 58f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.backgroundColor = new Color(0.014f, 0.058f, 0.074f, 0.94f);
            SetBorder(button, NeonCyanDim, 1.5f);
            SetRadius(button, 18f);
            button.style.display = DisplayStyle.None;

            _agentVoiceGlyph = new VectorIcon(VectorIcon.Kind.Microphone, Text);
            _agentVoiceGlyph.style.width = 27f;
            _agentVoiceGlyph.style.height = 27f;
            _agentVoiceGlyph.pickingMode = PickingMode.Ignore;
            button.Add(_agentVoiceGlyph);

            _agentVoiceStateLabel = new Label();
            _agentVoiceStateLabel.style.position = Position.Absolute;
            _agentVoiceStateLabel.style.left = -65f;
            _agentVoiceStateLabel.style.right = -65f;
            _agentVoiceStateLabel.style.top = 39f;
            _agentVoiceStateLabel.style.height = 15f;
            _agentVoiceStateLabel.style.fontSize = 8.5f;
            _agentVoiceStateLabel.style.color = TextMute;
            _agentVoiceStateLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _agentVoiceStateLabel.pickingMode = PickingMode.Ignore;
            button.Add(_agentVoiceStateLabel);
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                ToggleAgentCapture();
                evt.StopPropagation();
            });
            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, GoldDim));
            button.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(button,
                _agentVoiceUiState == AgentVoiceUiState.Listening ? Warn : NeonCyanDim));
            _agentVoiceButton = button;
            return button;
        }

        private void BuildAgentSubtitleOverlay() => _voiceResponses = new VoiceResponsePanel(_root);

        private void ToggleAgentCapture()
        {
            if (_agentMicrophone?.IsRecording == true)
            {
                FinishAgentCapture();
                return;
            }
            if (!VoiceEligible()) return;
            _agentMicrophone ??= new FmodMicrophoneCapture();
            if (_agentMicrophone.Start(out _))
                _agentVoiceUiState = AgentVoiceUiState.Listening;
            else
                _agentVoiceUiState = AgentVoiceUiState.NoMicrophone;
            RefreshCaptureOverlay();
        }

        private void FinishAgentCapture()
        {
            if (_agentMicrophone == null) return;
            var wav = _agentMicrophone.Stop(out _);
            RefreshCaptureOverlay();
            if (wav == null)
            {
                _agentVoiceUiState = AgentVoiceUiState.Ready;
                return;
            }
            if (_runner == null || _agentVoiceNpc == null) return;
            _capturedPlayerInputs.Enqueue(new CapturedPlayerInput
            {
                NpcId = _agentVoiceNpc.Id.Value,
                AttachmentId = _selectedAgentState?.AttachmentId ?? string.Empty,
                Wav = wav,
            });
            _agentVoiceUiState = AgentVoiceUiState.Recognizing;
            StartNextPlayerTranscription();
        }

        private void StartNextPlayerTranscription()
        {
            if (_runner == null || _sttInFlight || _pendingTokenCorrelation != 0 ||
                _capturedPlayerInputs.Count == 0) return;
            _activePlayerInput = _capturedPlayerInputs.Dequeue();
            if (!_runner.IsReady || !_runner.TryGetAgentState(
                    new HexLive.Simulation.Common.EntityId(_activePlayerInput.NpcId), out var state) ||
                !state.Attached || state.AttachmentId != _activePlayerInput.AttachmentId)
            {
                _activePlayerInput = null;
                ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
                return;
            }
            _pendingPlayerWav = _activePlayerInput.Wav;
            if (_cachedSttExpiry > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000)
            {
                BeginPlayerTranscription(_cachedSttToken);
                return;
            }
            _pendingTokenCorrelation = ++_agentCorrelation;
            _voiceDeadline = Time.unscaledTime + 15f;
            _runner.RequestSttToken(_pendingTokenCorrelation);
        }

        private void BeginPlayerTranscription(string token)
        {
            var wav = _pendingPlayerWav;
            var input = _activePlayerInput;
            _pendingPlayerWav = null;
            if (wav == null || input == null || _agentTranscriber == null) return;
            _sttInFlight = true;
            _ = TranscribeAndSendAsync(wav, token, input.NpcId, input.AttachmentId,
                _agentCts?.Token ?? CancellationToken.None);
        }

        private void DrainAgentWireResponses()
        {
            if (_runner == null) return;
            while (_runner.TryTakeSttTokenResult(out var token))
            {
                if (token.CorrelationId != _pendingTokenCorrelation) continue;
                _pendingTokenCorrelation = 0;
                if (!token.Accepted || _pendingPlayerWav == null || _agentTranscriber == null)
                {
                    _pendingPlayerWav = null;
                    _activePlayerInput = null;
                    _agentVoiceUiState = AgentVoiceUiState.NoStt;
                    ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
                    continue;
                }
                _cachedSttToken = token.Token;
                _cachedSttExpiry = token.ExpiresUtcMilliseconds;
                BeginPlayerTranscription(token.Token);
            }
            while (_runner.TryTakeAgentTextResult(out var result))
            {
                if (!_pendingTextResults.Remove(result.CorrelationId)) continue;
                if (!result.Accepted)
                {
                    _awaitingAgentTurn = false;
                    ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
                }
            }
            while (_runner.TryTakeAgentSpeech(out var speech))
                _ = BakeAndPresentAgentSpeechAsync(speech, _agentCts?.Token ?? CancellationToken.None);
        }

        private async Task TranscribeAndSendAsync(byte[] wav, string token, int npcId, string attachmentId,
            CancellationToken cancellationToken)
        {
            try
            {
                var text = await _agentTranscriber!.TranscribeAsync(wav, token, cancellationToken)
                    .ConfigureAwait(false);
                _agentMainThread.Enqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    _sttInFlight = false;
                    _activePlayerInput = null;
                    if (text.Length == 0 || _runner == null || !_runner.IsReady ||
                        !_runner.TryGetAgentState(new HexLive.Simulation.Common.EntityId(npcId), out var state) ||
                        !state.Attached || state.AttachmentId != attachmentId)
                    {
                        _agentVoiceUiState = AgentVoiceUiState.Ready;
                        ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
                        return;
                    }
                    var correlation = ++_agentCorrelation;
                    _pendingTextResults.Add(correlation);
                    _voiceSubmittedRevision = _selectedAgentState?.Revision ?? 0;
                    _awaitingAgentTurn = true;
                    _voiceDeadline = Time.unscaledTime + 90f;
                    _runner.SendAgentText(correlation,
                        new HexLive.Simulation.Common.EntityId(npcId),
                        Guid.NewGuid().ToString("N"), "ru", text);
                    if (_agentMicrophone?.IsRecording != true)
                        _agentVoiceUiState = AgentVoiceUiState.Thinking;
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch
            {
                _agentMainThread.Enqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    _sttInFlight = false;
                    _activePlayerInput = null;
                    _cachedSttToken = string.Empty;
                    _cachedSttExpiry = 0;
                    ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
                });
            }
        }

        private async Task BakeAndPresentAgentSpeechAsync(AgentSpeechMessage speech,
            CancellationToken cancellationToken)
        {
            var metadata = speech.Metadata;
            try
            {
                await _agentBakeGate.WaitAsync(cancellationToken);
                try
                {
                var basename = "agent_" + SafeFileName(metadata.UtteranceId);
                var wavPath = Path.Combine(_agentCachePath, basename + ".wav");
                var visPath = Path.Combine(_agentCachePath, basename + ".vis");
                var hasAudio = false;
                try { hasAudio = speech.Wav.Length > 0 && await Task.Run(() =>
                {
                    Directory.CreateDirectory(_agentCachePath);
                    if (!VoiceVisemeBaker.TryBake(speech.Wav, metadata.Text, visPath, out _, cancellationToken)) return false;
                    File.WriteAllBytes(wavPath, speech.Wav);
                    TrimAgentVoiceCache(_agentCachePath);
                    return true;
                }, cancellationToken); }
                catch (IOException) { /* still deliver the exact subtitle */ }
                catch (UnauthorizedAccessException) { }
                _agentMainThread.Enqueue(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    if (_preparedAgentSpeech.Count >= 20) _preparedAgentSpeech.Dequeue();
                    _preparedAgentSpeech.Enqueue(new PreparedAgentSpeech
                    { Metadata = metadata, WavPath = hasAudio ? wavPath : string.Empty,
                      VisPath = visPath, Deadline = Time.unscaledTime + 35f });
                });
                }
                finally { _agentBakeGate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void PresentPreparedAgentSpeech()
        {
            if (_preparedAgentSpeech.Count == 0 || _agentReplyFocusUntil > 0f ||
                _agentMicrophone?.IsRecording == true) return;
            var pending = _preparedAgentSpeech.Peek();
            var metadata = pending.Metadata;
            if (_runner == null || !_runner.TryGetAgentState(
                    new HexLive.Simulation.Common.EntityId(metadata.NpcId), out var state) || !state.Attached)
            {
                _preparedAgentSpeech.Dequeue();
                return;
            }
            var direct = metadata.Delivery == AgentSpeechDelivery.PlayerReply;
            var played = false;
            _worldRenderer ??= FindAnyObjectByType<Rendering.HexWorldRenderer>();
            if (pending.WavPath.Length > 0 && _worldRenderer != null &&
                _worldRenderer.TryGetActorView(metadata.NpcId, out var actor))
            {
                played = actor.SayExternalVoice(pending.WavPath, pending.VisPath, metadata.Emotion, direct);
                if (played && direct)
                {
                    StartAgentReplyFocus(metadata.DurationMilliseconds);
                    _agentReplyActor = actor;
                    _agentReplyWav = pending.WavPath;
                }
            }
            // A direct reply waits for an existing Talk/Alarm and lazy-loaded
            // actor. It is never discarded merely because the mouth was busy.
            if (!played && direct && pending.WavPath.Length > 0 &&
                Time.unscaledTime < pending.Deadline) return;
            _preparedAgentSpeech.Dequeue();
            var name = _runner?.CreateSnapshot()?.Npcs.Find(n => n.Id.Value == metadata.NpcId)?.DisplayName;
            ShowAgentSubtitle((string.IsNullOrEmpty(name) ? Loc.Get("agent.default_name") :
                Loc.Get("npc." + name.ToLowerInvariant() + ".name")) + ": " + metadata.Text,
                Mathf.Clamp(Mathf.Max(metadata.Text.Length / 12f, metadata.DurationMilliseconds / 1000f), 2.5f, 31f));
        }

        private bool VoiceEligible()
        {
            var state = _selectedAgentState;
            return _agentVoiceNpc != null && _agentVoiceNpc.Health > 0f &&
                   state is { Attached: true } &&
                   (state.Capabilities & AgentCapabilities.PlayerText) != 0 &&
                   _runner != null && _runner.IsReady && _runner.SupportsAgentIntegration &&
                   _runner.SttAvailable && !LoadingScreen.IsReplaying &&
                   _capturedPlayerInputs.Count + (_activePlayerInput != null ? 1 : 0) < MaxPendingCaptures;
        }

        private bool CaptureStillEligible() => _selectedAgentState is { Attached: true } &&
            _agentVoiceNpc != null && _agentVoiceNpc.Id.Value == _selectedAgentState.NpcId &&
            _runner != null && _runner.IsReady && _runner.Link.State == LinkState.Live &&
            _agentVoiceNpc.Health > 0f && !LoadingScreen.IsReplaying;

        private void RefreshAgentVoiceButton()
        {
            if (_agentVoiceButton == null) return;
            var visible = _selectedAgentState is { Attached: true } && NpcSelection.Count == 1;
            _agentVoiceButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;
            if (_runner == null || !_runner.SupportsAgentIntegration)
                _agentVoiceUiState = AgentVoiceUiState.Offline;
            else if (!_runner.SttAvailable)
                _agentVoiceUiState = AgentVoiceUiState.NoStt;
            var recording = _agentMicrophone?.IsRecording == true;
            _agentVoiceButton.SetEnabled(recording || VoiceEligible());
            _agentVoiceGlyph!.SetColor(recording ? Warn : Text);
            SetBorderColor(_agentVoiceButton, recording ? Warn : NeonCyanDim);
            var pending = _capturedPlayerInputs.Count + (_activePlayerInput != null ? 1 : 0);
            _agentVoiceStateLabel!.text = recording ? Loc.Get("agent.voice.listening") : pending > 0
                ? string.Format(Loc.Get("agent.voice.queued"), pending)
                : AgentVoiceStateText(_agentVoiceUiState);
            _agentVoiceButton.tooltip = Loc.Get(pending >= MaxPendingCaptures
                ? "agent.voice.queue_full" : "agent.voice.button");
        }

        private static string AgentVoiceStateText(AgentVoiceUiState state) => state switch
        {
            AgentVoiceUiState.Listening => Loc.Get("agent.voice.listening"),
            AgentVoiceUiState.Recognizing => Loc.Get("agent.voice.recognizing"),
            AgentVoiceUiState.Thinking => Loc.Get("agent.voice.thinking"),
            AgentVoiceUiState.Speaking => Loc.Get("agent.voice.speaking"),
            AgentVoiceUiState.NoStt => Loc.Get("agent.voice.no_stt"),
            AgentVoiceUiState.NoMicrophone => Loc.Get("agent.voice.no_microphone"),
            AgentVoiceUiState.Offline => Loc.Get("agent.voice.offline"),
            _ => Loc.Get("agent.voice.ready"),
        };

        private void ParseAgentRelation(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                var value = JsonUtility.FromJson<AgentRelationPayload>(json);
                if (value == null) return;
                _agentFamiliarity = Mathf.Clamp01(value.familiarity);
                _agentTrust = Mathf.Clamp01(value.trust);
                _agentAffinity = Mathf.Clamp01(value.affinity);
                _agentRelationTick = _runner?.CurrentTick ?? 0;
                _agentRelationReady = true;
                _relationSig.Clear();
            }
            catch (ArgumentException) { }
        }

        private void RefreshAgentJournal(string text, int npcId)
        {
            text = text?.Trim() ?? string.Empty;
            if (text.Length == 0 || text == _agentJournalText) return;
            _agentJournalText = text;
            _agentJournal.Clear();
            _agentJournal.Add(new JournalEntrySnapshot
            {
                Tick = _runner?.CurrentTick ?? 0,
                Type = "AgentNarrative",
                Extra = text,
            });
            _agentJournalReady = true;
            _journalSig = int.MinValue;
        }

        private void ShowAgentSubtitle(string text, float seconds) => VoiceResponsePanel.PublishNpc(text, seconds);

        private void StartAgentReplyFocus(int durationMilliseconds)
        {
            StopAgentReplyFocus();
            try
            {
                _agentReplyFocus = FMODUnity.RuntimeManager.CreateInstance("snapshot:/AgentReplyFocus");
                if (_agentReplyFocus.isValid()) _agentReplyFocus.start();
                _agentReplyFocusUntil = Time.unscaledTime +
                    Mathf.Clamp(durationMilliseconds / 1000f + 0.4f, 0.5f, 31f);
            }
            catch (Exception)
            {
                _agentReplyFocus = default;
                _agentReplyFocusUntil = 0f;
            }
        }

        private void StopAgentReplyFocus()
        {
            if (_agentReplyFocus.isValid())
            {
                _agentReplyFocus.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                _agentReplyFocus.release();
            }
            _agentReplyFocus = default;
            _agentReplyFocusUntil = 0f;
            _agentReplyActor = null;
            _agentReplyWav = string.Empty;
        }

        private static string SafeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Length <= 80 ? value : value.Substring(0, 80);
        }

        private static void TrimAgentVoiceCache(string directory)
        {
            var files = new DirectoryInfo(directory).GetFiles("*.wav");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (var i = 20; i < files.Length; i++)
            {
                try
                {
                    var vis = Path.ChangeExtension(files[i].FullName, ".vis");
                    files[i].Delete();
                    if (File.Exists(vis)) File.Delete(vis);
                }
                catch (IOException) { }
            }
        }

        private void LocalizeAgentVoice() => RefreshAgentVoiceButton();
    }
}
