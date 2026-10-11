#nullable enable
using HexLive.Simulation.Wire;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §171.11: in the browser the microphone button talks to the player's OWN
    /// character through the game server's voice brain (ElevenLabs Agents) — no
    /// Agent Studio attachment needed. Listening, turn-taking and her voice are the
    /// browser's; the panel only starts/stops the session and shows subtitles.
    /// The desktop keeps the §160 Agent Studio path unchanged.
    /// </summary>
    public sealed partial class CharacterPanel
    {
        private WebVoiceAgent? _serverVoice;

        private bool ServerVoiceActive => _serverVoice != null &&
            _serverVoice.Current is WebVoiceAgent.Phase.Connecting or WebVoiceAgent.Phase.Listening or WebVoiceAgent.Phase.Speaking;

        /// <summary>The selected body is this player's, alive, on a live server link, with a player identity.</summary>
        private bool ServerVoiceEligible =>
            WebVoiceAgent.Supported && NpcSelection.Count == 1 && _agentVoiceNpc != null && _agentVoiceNpc.Health > 0f &&
            _runner != null && _runner.IsReady && _runner.Link.State == LinkState.Live &&
            _runner.IsAssignedNpc(_agentVoiceNpc.Id) && !string.IsNullOrEmpty(SessionConfig.ControlToken) &&
            WebVoiceAgent.PageServerHttpBase() != null && !LoadingScreen.IsReplaying;

        private bool ServerVoiceMode => ServerVoiceActive || ServerVoiceEligible;

        private void ToggleServerVoice()
        {
            if (ServerVoiceActive) { _serverVoice!.Stop(); return; }
            if (!ServerVoiceEligible) return;
            if (_serverVoice == null)
            {
                _serverVoice = new WebVoiceAgent();
                _serverVoice.Line += (speaker, text) =>
                    ShowAgentSubtitle(speaker == "user" ? "› " + text : text, speaker == "user" ? 3f : 6f);
            }
            _serverVoice.Start(WebVoiceAgent.PageServerHttpBase()!, SessionConfig.ControlToken!, SessionConfig.ClientId,
                _agentVoiceNpc!.Id.Value);
        }

        private void TickServerVoice()
        {
            if (_serverVoice == null) return;
            _serverVoice.Pump();
            // The conversation belongs to the body it started with: switching selection,
            // losing the link or the character ends it (and stops the per-minute billing).
            if (ServerVoiceActive && (!ServerVoiceEligible || _agentVoiceNpc!.Id.Value != _serverVoice.NpcId))
                _serverVoice.Stop();
            if (_serverVoice.Current == WebVoiceAgent.Phase.Error && _serverVoice.LastError.Length > 0)
            {
                Debug.LogWarning("[voice] " + _serverVoice.LastError);
                ShowAgentSubtitle(Loc.Get("agent.voice.input_failed"), 5f);
                _serverVoice.Stop();
            }
        }

        /// <summary>Returns true when the button belongs to the server voice this frame.</summary>
        private bool RefreshServerVoiceButton()
        {
            if (_agentVoiceButton == null || !ServerVoiceMode) return false;
            _agentVoiceButton.style.display = DisplayStyle.Flex;
            _agentVoiceButton.SetEnabled(true);
            var phase = _serverVoice?.Current ?? WebVoiceAgent.Phase.Off;
            var live = phase is WebVoiceAgent.Phase.Listening or WebVoiceAgent.Phase.Speaking;
            _agentVoiceGlyph!.SetColor(live ? Warn : Text);
            SetBorderColor(_agentVoiceButton, live ? Warn : NeonCyanDim);
            _agentVoiceStateLabel!.text = phase switch
            {
                WebVoiceAgent.Phase.Connecting => Loc.Get("agent.voice.thinking"),
                WebVoiceAgent.Phase.Listening => Loc.Get("agent.voice.listening"),
                WebVoiceAgent.Phase.Speaking => Loc.Get("agent.voice.speaking"),
                _ => Loc.Get("agent.voice.ready"),
            };
            _agentVoiceButton.tooltip = Loc.Get("agent.voice.button");
            return true;
        }
    }
}
