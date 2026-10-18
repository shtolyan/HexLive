using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using HexLive.UnityPresentation.Localization;
using HexLive.UnityPresentation.Platform;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>§160/§161 one non-overwriting subtitle queue for NPCs and the administrator.</summary>
    internal sealed class VoiceResponsePanel
    {
        private sealed class Message { public string Text; public bool? Success; public float Seconds; }
        private static readonly Queue<Message> Admin = new();
        private static readonly Queue<Message> Npcs = new();
        private readonly Label _label;
        private float _until;
        private const string HtmlScreen = "voiceSubtitle";
        private bool _htmlShown;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { Admin.Clear(); Npcs.Clear(); }
        public VoiceResponsePanel(VisualElement parent)
        {
            var sheet = Resources.Load<StyleSheet>("HexLive/UI/VoiceResponse");
            if (sheet != null && !parent.styleSheets.Contains(sheet)) parent.styleSheets.Add(sheet);
            _label = new Label { pickingMode = PickingMode.Ignore }; _label.AddToClassList("voice-response"); _label.AddToClassList("voice-response-hidden"); parent.Add(_label);
        }
        public static void PublishAdmin(string text, bool success) => Enqueue(Admin, new Message
        { Text = Loc.Get("admin.voice.title") + ": " + text, Success = success, Seconds = Mathf.Clamp(text.Length / 12f, 5f, 20f) });
        public static void PublishNpc(string text, float seconds) => Enqueue(Npcs, new Message { Text = text, Seconds = seconds });
        private static void Enqueue(Queue<Message> queue, Message message)
        { if (string.IsNullOrWhiteSpace(message.Text)) return; if (queue.Count >= 32) queue.Dequeue(); queue.Enqueue(message); }
        public void Tick()
        {
            if (Time.unscaledTime < _until) return;
            if (Admin.Count == 0 && Npcs.Count == 0)
            {
                _label.AddToClassList("voice-response-hidden");
                if (_htmlShown) { HtmlUi.Hide(HtmlScreen); _htmlShown = false; }
                return;
            }
            var next = Admin.Count > 0 ? Admin.Dequeue() : Npcs.Dequeue();
            _until = Time.unscaledTime + next.Seconds;
            if (HtmlUi.Active)
            {
                // §174.7: the page draws the line; the label stays hidden.
                HtmlUi.Show(HtmlScreen, new JObject
                {
                    ["text"] = next.Text,
                    ["tone"] = next.Success == true ? "success" : next.Success == false ? "error" : null,
                });
                _htmlShown = true;
                return;
            }
            _label.text = next.Text; _label.RemoveFromClassList("voice-response-hidden");
            _label.EnableInClassList("voice-response-success", next.Success == true);
            _label.EnableInClassList("voice-response-error", next.Success == false);
        }
    }
}
