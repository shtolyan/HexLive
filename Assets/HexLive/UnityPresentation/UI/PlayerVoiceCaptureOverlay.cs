#nullable enable
using System;
using System.Collections.Generic;
using HexLive.UnityPresentation.Audio;
using HexLive.UnityPresentation.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>§160/§161 shared recording card; transport and command permissions belong to the caller.</summary>
    internal sealed class PlayerVoiceCaptureOverlay
    {
        private readonly VisualElement _overlay;
        private readonly Label _title;
        private readonly Button _done, _cancel;
        private readonly List<VisualElement> _bars = new();
        private readonly Func<string>? _context;

        public PlayerVoiceCaptureOverlay(VisualElement parent, Action finish, Action cancel, Func<string>? context = null)
        {
            _context = context;
            var sheet = Resources.Load<StyleSheet>("HexLive/UI/AgentCapture");
            if (sheet != null && !parent.styleSheets.Contains(sheet)) parent.styleSheets.Add(sheet);
            _overlay = new VisualElement { name = "player-voice-capture" };
            _overlay.AddToClassList("agent-capture");
            var card = new VisualElement(); card.AddToClassList("agent-capture-card");
            var icon = new VectorIcon(VectorIcon.Kind.Microphone, new Color(0.18f, 0.92f, 0.88f));
            icon.AddToClassList("agent-capture-icon"); card.Add(icon);
            _title = new Label(); _title.AddToClassList("agent-capture-title"); card.Add(_title);
            var meter = new VisualElement(); meter.AddToClassList("agent-capture-meter");
            for (var i = 0; i < 18; i++)
            {
                var bar = new VisualElement(); bar.AddToClassList("agent-capture-bar");
                meter.Add(bar); _bars.Add(bar);
            }
            card.Add(meter);
            _done = new Button(finish); _done.AddToClassList("agent-capture-done"); card.Add(_done);
            _cancel = new Button(() => { cancel(); Hide(); });
            _cancel.AddToClassList("agent-capture-cancel"); card.Add(_cancel);
            _overlay.Add(card);
            _overlay.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            _overlay.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            parent.Add(_overlay);
        }

        public void Hide() => _overlay.RemoveFromClassList("agent-capture-visible");

        public void Refresh(FmodMicrophoneCapture? capture)
        {
            if (capture?.IsRecording != true) { Hide(); return; }
            _overlay.BringToFront(); _overlay.AddToClassList("agent-capture-visible");
            var context = _context?.Invoke();
            _title.text = string.IsNullOrEmpty(context) ? Loc.Get("agent.voice.listening")
                : context + " · " + Loc.Get("agent.voice.listening");
            _done.text = "✓ " + Loc.Get("agent.voice.capture_done");
            _cancel.text = Loc.Get("loot.quantity_cancel");
            for (var i = 0; i < _bars.Count; i++)
                _bars[i].EnableInClassList("agent-capture-lit", capture.Level > i / (float)_bars.Count);
        }
    }
}
