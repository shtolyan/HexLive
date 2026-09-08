using System;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>§163 explicit approval through the authenticated game connection.</summary>
    public sealed class AgentPairingPanel : Foldout
    {
        private readonly TextField _ticket = new() { maxLength = 41 };
        private readonly Label _status = new();
        private readonly Button _check;
        private readonly Button _approve;
        private string _pendingId = string.Empty;
        public AgentPairingPanel()
        {
            value = false;
            AddToClassList("agent-pairing");
            var sheet = Resources.Load<StyleSheet>("HexLive/UI/AgentPairing");
            if (sheet != null) styleSheets.Add(sheet);
            _check = new Button(() => Send(false));
            _approve = new Button(() => Send(true));
            _approve.SetEnabled(false);
            Add(_ticket); Add(_check); Add(_status); Add(_approve);
            _ticket.RegisterValueChangedCallback(_ => { _approve.SetEnabled(false); _pendingId = string.Empty; });
            RegisterCallback<AttachToPanelEvent>(_ => { RefreshLanguage(); Loc.LanguageChanged += RefreshLanguage; });
            RegisterCallback<DetachFromPanelEvent>(_ => Loc.LanguageChanged -= RefreshLanguage);
            schedule.Execute(Poll).Every(200);
        }
        private void RefreshLanguage()
        {
            text = Loc.Get("agent.pairing.title");
            _ticket.label = Loc.Get("agent.pairing.code");
            _check.text = Loc.Get("agent.pairing.check");
            _approve.text = Loc.Get("agent.pairing.approve");
        }
        private void Send(bool approve)
        {
            var parts = (_ticket.value ?? string.Empty).Trim().Split(':');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) || parts[1].Length != 8 ||
                (approve && parts[0] != _pendingId)) { _status.text = Loc.Get("agent.pairing.invalid"); return; }
            _pendingId = parts[0]; _approve.SetEnabled(false);
            (SimulationSource.Current as SimulationRunnerBehaviour)?.SendAgentPairing(parts[0], parts[1], approve);
            _status.text = Loc.Get("agent.pairing.waiting");
        }
        private void Poll()
        {
            if (SimulationSource.Current is not SimulationRunnerBehaviour runner) return;
            while (runner.TryTakeAgentPairing(out var result))
            {
                if (result.Id != _pendingId) continue;
                _status.text = result.Approved ? Loc.Get("agent.pairing.done") :
                    string.IsNullOrEmpty(result.Text) ? Loc.Get("agent.pairing.invalid") :
                    string.Format(Loc.Get("agent.pairing.confirm"), result.Text);
                _approve.SetEnabled(!result.Approved && !string.IsNullOrEmpty(result.Text));
            }
        }
    }
}
