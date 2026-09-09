#nullable enable
using System;
using System.Collections.Generic;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>§67.14: keep a recognized utterance until server acceptance.</summary>
    public sealed class PlayerTextOutbox
    {
        public sealed class Message
        {
            public int NpcId { get; }
            public string AttachmentId { get; }
            public string MessageId { get; }
            public string Text { get; }
            public int CorrelationId { get; }
            public double NextAttempt { get; internal set; }
            public bool BackpressureReported { get; internal set; }
            public Message(int npcId, string attachmentId, string messageId, string text, int correlationId)
            {
                NpcId = npcId; AttachmentId = attachmentId; MessageId = messageId;
                Text = text; CorrelationId = correlationId;
            }
        }

        public enum Acknowledgment { Unknown, Delivered, Retry, Rejected }
        private readonly Queue<Message> _messages = new();
        private readonly int _capacity;
        private readonly int _maxCharacters;
        public int Count => _messages.Count;
        public PlayerTextOutbox(int capacity, int maxCharacters)
        {
            _capacity = Math.Max(1, capacity);
            _maxCharacters = Math.Max(1, maxCharacters);
        }

        public bool TryEnqueue(Message message, out string reason)
        {
            reason = message.Text.Length == 0 ? "EmptyText" :
                message.Text.Length > _maxCharacters ? "TextTooLong" :
                _messages.Count >= _capacity ? "OutboxFull" : string.Empty;
            if (reason.Length > 0) return false;
            _messages.Enqueue(message);
            return true;
        }

        public Message? Peek() => _messages.Count == 0 ? null : _messages.Peek();
        public void MarkSent(double now) { if (Peek() is { } message) message.NextAttempt = now + 5; }

        public Acknowledgment Accept(int correlationId, string messageId, bool accepted, string reason, double now)
        {
            var message = Peek();
            if (message == null || message.CorrelationId != correlationId || message.MessageId != messageId) return Acknowledgment.Unknown;
            if (accepted) { _messages.Dequeue(); return Acknowledgment.Delivered; }
            if (reason == "InboxFull")
            {
                message.NextAttempt = now + 2;
                return Acknowledgment.Retry;
            }
            _messages.Dequeue();
            return Acknowledgment.Rejected;
        }

        // Called only alongside an explicit failed-delivery notice (expired attachment/disable).
        public void RejectHead() { if (_messages.Count > 0) _messages.Dequeue(); }
        public void Clear() => _messages.Clear();
    }
}
