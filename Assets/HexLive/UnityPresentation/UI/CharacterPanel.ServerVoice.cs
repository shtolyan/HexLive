#nullable enable
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §171.11: in the browser the voice button of the portrait window is a PHONE:
    /// call your own character, talk, hang up — through the game server's voice
    /// brain (ElevenLabs Agents), no Agent Studio attachment needed. The whole call
    /// lives in that small window (Толя, 11.10): no capture overlay, no subtitles over
    /// the world — the player keeps seeing her. Listening, turn-taking and her voice
    /// are the browser's. The desktop keeps the §160 Agent Studio path unchanged.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private WebVoiceAgent? _serverVoice;
        private Wearing.NpcActorView? _serverCallActor;

        private void ReleaseServerCallActor()
        {
            if (_serverCallActor != null) _serverCallActor.SetPhoneVoiceActive(false);
            _serverCallActor = null;
        }

        private bool ServerVoiceActive => _serverVoice != null &&
            _serverVoice.Current is WebVoiceAgent.Phase.Connecting or WebVoiceAgent.Phase.Listening or WebVoiceAgent.Phase.Speaking;

        /// <summary>The selected body is this player's, alive, on a live server link, with a player identity.</summary>
        private bool ServerVoiceEligible =>
            WebVoiceAgent.Supported && NpcSelection.Count == 1 && _agentVoiceNpc != null && _agentVoiceNpc.Health > 0f &&
            _runner != null && _runner.IsReady && _runner.Link.State == LinkState.Live &&
            _runner.IsAssignedNpc(_agentVoiceNpc.Id) && !string.IsNullOrEmpty(SessionConfig.ControlToken) &&
            WebVoiceAgent.PageServerHttpBase() != null && !LoadingScreen.IsReplaying;

        private bool ServerVoiceMode => ServerVoiceActive || ServerVoiceEligible ||
            (WebVoiceAgent.Supported && _callFailureTerm.Length > 0 && NpcSelection.Count == 1 &&
             _agentVoiceNpc?.Id.Value == _callFailureNpcId);

        private void ToggleServerVoice()
        {
            if (ServerVoiceActive) { _serverVoice!.Stop(); return; }
            if (!ServerVoiceEligible) return;
            ClearServerVoiceFailure();
            if (_serverVoice == null)
            {
                _serverVoice = new WebVoiceAgent();
                // The name is committed before the reply. Refresh after a transcript/reply
                // instead of waiting for the normal idle poll or a page reload.
                _serverVoice.Line += (_, _) => _serverRelationNextPoll = 0;
                _serverVoice.Alignment += a => _serverCallActor?.AppendPhoneAlignment(a.chars, a.starts, a.durations);
            }
            _serverVoice.Start(WebVoiceAgent.PageServerHttpBase()!, SessionConfig.ControlToken!, SessionConfig.ClientId,
                _agentVoiceNpc!.Id.Value);
            AttachServerCallActor();
        }

        private void AttachServerCallActor()
        {
            if (_serverCallActor != null || _serverVoice == null || !ServerVoiceActive) return;
            _worldRenderer ??= FindAnyObjectByType<Rendering.HexWorldRenderer>();
            if (_worldRenderer != null && _worldRenderer.TryGetActorView(_serverVoice.NpcId, out var actor))
            {
                _serverCallActor = actor;
                actor.SetPhoneVoiceActive(true);
            }
        }

        private void TickServerVoice()
        {
            PollServerVoiceRelation();
            if (_serverVoice == null) return;
            AttachServerCallActor(); // reserve before draining the first alignment batch
            _serverVoice.Pump();
            // The conversation belongs to the body it started with: switching selection,
            // losing the link or the character ends it (and stops the per-minute billing).
            if (ServerVoiceActive && (!ServerVoiceEligible || _agentVoiceNpc!.Id.Value != _serverVoice.NpcId))
                _serverVoice.Stop();
            if (_serverVoice.Current == WebVoiceAgent.Phase.Error)
            {
                Debug.LogWarning("[voice] " + _serverVoice.LastError);
                FailServerVoice(_serverVoice.LastError);
            }
            if (!ServerVoiceActive)
            {
                ReleaseServerCallActor();
            }
            if (_serverCallActor != null)
                _serverCallActor.SetPhoneSpeaking(_serverVoice.Current == WebVoiceAgent.Phase.Speaking);
        }

        private string _callFailureTerm = "";
        private int _callFailureNpcId;

        private void ClearServerVoiceFailure() => _callFailureTerm = "";

        private void FailServerVoice(string detail)
        {
            _callFailureTerm = detail switch
            {
                "session:402" => "agent.call.credit",
                "session:401" => "agent.call.signin",
                "session:403" => "agent.call.forbidden",
                "session:429" => "agent.call.busy",
                "session:502" => "agent.call.provider",
                "session:504" or "connect_timeout:session" => "agent.call.server_timeout",
                "connect_timeout:sdk" => "agent.call.module_timeout",
                "connect_timeout:webrtc" => "agent.call.connection_timeout",
                _ => "agent.call.failed",
            };
            _callFailureNpcId = _serverVoice?.NpcId ?? 0;
            _serverVoice?.Stop();
            ReleaseServerCallActor();
        }

        private void ApplyServerVoiceBalance(float balanceUsd)
        {
            if (balanceUsd <= 0 && _serverVoice?.Current is WebVoiceAgent.Phase.Listening or WebVoiceAgent.Phase.Speaking)
                FailServerVoice("session:402");
        }
        private bool _serverRelationRequestPending;
        private float _serverRelationNextPoll;

        private void PollServerVoiceRelation()
        {
            if (!ServerVoiceEligible || _serverRelationRequestPending || Time.unscaledTime < _serverRelationNextPoll) return;
            _serverRelationNextPoll = Time.unscaledTime + 3f;
            StartCoroutine(ReadServerVoiceRelation(_agentVoiceNpc!.Id.Value, SessionConfig.ControlToken!,
                WebVoiceAgent.PageServerHttpBase()!));
        }

        private System.Collections.IEnumerator ReadServerVoiceRelation(int npcId, string bearer, string server)
        {
            _serverRelationRequestPending = true;
            try
            {
                using var request = UnityWebRequest.Get(server.TrimEnd('/') + "/api/voice/v1/relationship/" + npcId);
                request.SetRequestHeader("Authorization", "Bearer " + bearer);
                request.SetRequestHeader("X-HexLive-Client-Id", SessionConfig.ClientId);
                request.timeout = 10;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success || !ServerVoiceEligible ||
                    _agentVoiceNpc!.Id.Value != npcId || SessionConfig.ControlToken != bearer) yield break;
                _agentAttachedNpcId = npcId;
                ParseAgentRelation(request.downloadHandler.text);
            }
            finally { _serverRelationRequestPending = false; }
        }

        /// <summary>
        /// Returns true when the button belongs to the call this frame. Three states in
        /// one place: green handset «Позвонить» → rocking handset «Звоню…» → red handset
        /// to hang up, captioned «Слушает»/«Говорит».
        /// </summary>
        private bool RefreshServerVoiceButton()
        {
            if (_agentVoiceButton == null || _agentVoiceGlyph == null) return false;
            if (_agentVoiceNpc == null || _agentVoiceNpc.Id.Value != _callFailureNpcId) ClearServerVoiceFailure();
            if (!ServerVoiceMode)
            {
                if (_agentVoiceStateLabel != null)
                {
                    _agentVoiceStateLabel.style.color = TextMute;
                    _agentVoiceStateLabel.style.fontSize = 8.5f;
                }
                // Back to the desktop/Agent Studio microphone if the call mode goes away.
                if (_agentVoiceGlyph.Current != VectorIcon.Kind.Microphone)
                {
                    _agentVoiceGlyph.SetKind(VectorIcon.Kind.Microphone);
                    _agentVoiceGlyph.style.rotate = new Rotate(0f);
                }
                return false;
            }
            _agentVoiceButton.style.display = DisplayStyle.Flex;
            _agentVoiceButton.SetEnabled(ServerVoiceActive || ServerVoiceEligible);
            RenderServerVoiceButton();
            return true;
        }

        private void RenderServerVoiceButton()
        {
            if (_agentVoiceButton == null || _agentVoiceGlyph == null || _agentVoiceStateLabel == null) return;
            var phase = _serverVoice?.Current ?? WebVoiceAgent.Phase.Off;
            var inCall = phase is WebVoiceAgent.Phase.Listening or WebVoiceAgent.Phase.Speaking;
            var ringing = phase == WebVoiceAgent.Phase.Connecting;
            var failed = !inCall && !ringing && _callFailureTerm.Length > 0;
            var tint = inCall ? Crit : failed ? Warn : Good;

            _agentVoiceGlyph.SetKind(inCall ? VectorIcon.Kind.PhoneHangUp : VectorIcon.Kind.Phone);
            _agentVoiceGlyph.SetColor(tint);
            // A ringing phone rocks; at rest it is still.
            _agentVoiceGlyph.style.rotate = new Rotate(ringing ? Mathf.Sin(Time.unscaledTime * 18f) * 14f : 0f);
            SetBorderColor(_agentVoiceButton, tint);
            _agentVoiceStateLabel!.text = phase switch
            {
                WebVoiceAgent.Phase.Connecting => Loc.Get("agent.call.ringing"),
                WebVoiceAgent.Phase.Listening => Loc.Get("agent.call.listening"),
                WebVoiceAgent.Phase.Speaking => Loc.Get("agent.call.speaking"),
                _ when failed => Loc.Get(_callFailureTerm),
                _ => Loc.Get("agent.call.start"),
            };
            _agentVoiceStateLabel.style.color = failed ? Warn : TextMute;
            _agentVoiceStateLabel.style.fontSize = failed ? 11f : 8.5f;
            _agentVoiceButton!.tooltip = failed
                ? Loc.Get(_callFailureTerm) + "\n" + Loc.Get("agent.call.button")
                : Loc.Get(inCall || ringing ? "agent.call.hangup" : "agent.call.button");
        }
    }
}
