#nullable enable
using System;
using System.Collections;
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    public sealed partial class CharacterPanel
    {
        private const int PlayerSpeechCaptureSeconds = 15;
        private BugReportMicrophoneCapture? _playerSpeechMic;
        private PlayerVoiceCaptureOverlay? _playerSpeechOverlay;
        private Button? _playerSpeechButton;
        private UnityWebRequest? _playerSpeechRequest;
        private int _playerSpeechNpcId = -1;
        private string _playerSpeechWorld = "";

        private static string? PlayerSpeechServer()
        {
            if (WebVoiceAgent.PageServerHttpBase() is { } page) return page;
            if (!Uri.TryCreate(SessionConfig.ServerUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("ws" or "wss")) return null;
            return (uri.Scheme == "wss" ? "https://" : "http://") + uri.Authority;
        }

        private bool PlayerSpeechVisible => _runner != null && _runner.IsReady && _runner.SttAvailable &&
            _runner.Link.State == LinkState.Live && _runner.AssignmentWorldId.Length > 0 && NpcSelection.Count == 1 &&
            _agentVoiceNpc is { Health: > 0f } npc && _runner.IsAssignedNpc(npc.Id) &&
            !string.IsNullOrEmpty(SessionConfig.ControlToken) && PlayerSpeechServer() != null &&
            !LoadingScreen.IsReplaying;

        private bool CanSpeakAsCharacter => PlayerSpeechVisible && !ServerVoiceActive && !_callPreparing &&
            _agentMicrophone?.IsRecording != true && _playerSpeechRequest == null &&
            _agentVoiceNpc is { IsUnconscious: false, IsDying: false } npc &&
            npc.CurrentInteraction is not ("Sleep" or "Romance");

        private string PlayerSpeechState => _playerSpeechMic?.IsRecording == true ? "recording" :
            _playerSpeechRequest != null ? "sending" : "ready";

        private VisualElement BuildPlayerSpeechButton()
        {
            var button = new Button(TogglePlayerSpeech) { name = "player-speech", tooltip = Loc.Get("agent.speak.button") };
            button.style.width = 34f;
            button.style.height = Length.Percent(100f);
            button.style.flexShrink = 0f;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.backgroundColor = IdentityGlass;
            button.style.display = DisplayStyle.None;
            SetBorder(button, StrokeStrong, 1f);
            SetRadius(button, 10f);
            var icon = new VectorIcon(VectorIcon.Kind.Microphone, Text) { pickingMode = PickingMode.Ignore };
            icon.style.width = 19f;
            icon.style.height = 19f;
            button.Add(icon);
            _playerSpeechButton = button;
            return button;
        }

        private void TogglePlayerSpeech()
        {
            if (_playerSpeechMic?.IsRecording == true) { FinishPlayerSpeech(); return; }
            if (!CanSpeakAsCharacter) return;
            _playerSpeechMic ??= new BugReportMicrophoneCapture(PlayerSpeechCaptureSeconds);
            if (!_playerSpeechMic.Start(out _))
            {
                ShowAgentSubtitle(Loc.Get("bugs.voice.unavailable"), 4f);
                return;
            }
            _playerSpeechNpcId = _agentVoiceNpc!.Id.Value;
            _playerSpeechWorld = _runner.AssignmentWorldId;
            _playerSpeechOverlay ??= new PlayerVoiceCaptureOverlay(_root, FinishPlayerSpeech, CancelPlayerSpeechCapture,
                () => Loc.Get("agent.speak.button"));
            TickPlayerSpeech();
        }

        private void TickPlayerSpeech()
        {
            if (_playerSpeechButton != null)
            {
                _playerSpeechButton.style.display = PlayerSpeechVisible ? DisplayStyle.Flex : DisplayStyle.None;
                _playerSpeechButton.SetEnabled(CanSpeakAsCharacter);
                _playerSpeechButton.tooltip = Loc.Get(_playerSpeechRequest != null ? "agent.speak.sending" : "agent.speak.button");
            }
            if (_playerSpeechMic?.IsRecording != true) return;
            if (!CanSpeakAsCharacter || _agentVoiceNpc!.Id.Value != _playerSpeechNpcId ||
                _runner.AssignmentWorldId != _playerSpeechWorld) CancelPlayerSpeechCapture();
            else if (_playerSpeechMic.Tick(Time.unscaledDeltaTime)) FinishPlayerSpeech();
            else _playerSpeechOverlay?.RefreshRecording(true, _playerSpeechMic.IsReady, _playerSpeechMic.Level);
        }

        private void CancelPlayerSpeechCapture()
        {
            _playerSpeechMic?.Cancel();
            _playerSpeechOverlay?.Hide();
            _playerSpeechNpcId = -1;
            _playerSpeechWorld = "";
        }

        private void DisablePlayerSpeech()
        {
            CancelPlayerSpeechCapture();
            _playerSpeechMic?.Dispose();
            _playerSpeechMic = null;
            _playerSpeechRequest?.Abort();
        }

        private void FinishPlayerSpeech()
        {
            if (_playerSpeechMic?.IsRecording != true) return;
            var npcId = _playerSpeechNpcId;
            var worldId = _playerSpeechWorld;
            var raw = _playerSpeechMic.Stop(out _);
            _playerSpeechOverlay?.Hide();
            _playerSpeechNpcId = -1;
            _playerSpeechWorld = "";
            var server = PlayerSpeechServer();
            var token = SessionConfig.ControlToken;
            if (!CanSpeakAsCharacter || server == null || token == null || _agentVoiceNpc!.Id.Value != npcId ||
                _runner.AssignmentWorldId != worldId) return;
            if (raw == null) { ShowAgentSubtitle(Loc.Get("bugs.voice.unavailable"), 4f); return; }
            byte[] wav;
            try { wav = BugReportWav.Prepare(raw); }
            catch (ArgumentException) { ShowAgentSubtitle(Loc.Get("bugs.voice.empty"), 4f); return; }
            StartCoroutine(SendPlayerSpeech(server, token, npcId, worldId, wav));
        }

        private IEnumerator SendPlayerSpeech(string server, string token, int npcId, string worldId, byte[] wav)
        {
            var language = Loc.Code.ToLowerInvariant();
            var url = server + $"/api/voice/v1/speak?npcId={npcId}&requestId={Guid.NewGuid():N}&language={language}" +
                "&worldId=" + UnityWebRequest.EscapeURL(worldId);
            using var request = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(wav),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 25
            };
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("X-HexLive-Client-Id", SessionConfig.ClientId);
            request.SetRequestHeader("Content-Type", "audio/wav");
            _playerSpeechRequest = request;
            try
            {
                yield return request.SendWebRequest();
                if (_agentCts == null || request.result == UnityWebRequest.Result.Success) yield break;
                var error = request.responseCode switch
                {
                    402 => "agent.call.credit",
                    422 => "bugs.voice.empty",
                    429 => "agent.call.busy",
                    _ => "agent.voice.input_failed"
                };
                ShowAgentSubtitle(Loc.Get(error), 4f);
            }
            finally { _playerSpeechRequest = null; }
        }

        private bool TryGetAudibleBroadcastActor(int npcId, out Wearing.NpcActorView? actor)
        {
            actor = null;
            if (_runner == null || !_runner.IsReady || LoadingScreen.IsReplaying) return false;
            _worldRenderer ??= FindAnyObjectByType<Rendering.HexWorldRenderer>();
            var camera = Camera.main;
            return camera != null && _worldRenderer != null && _worldRenderer.TryGetActorView(npcId, out actor) &&
                actor.gameObject.activeInHierarchy && actor.TryGetBodyCenter(out var center) &&
                (camera.transform.position - center).sqrMagnitude <= FmodSfx.VoiceMaxDistance * FmodSfx.VoiceMaxDistance;
        }
    }
}
