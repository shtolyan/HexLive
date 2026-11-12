using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using HexLive.Simulation.Wire;

namespace HexLive.Server.Voice;

public sealed class PlayerSpeechRelay(TimeProvider? clock = null)
{
    private const int AttemptsPerMinute = 8;
    private const int MaxConcurrent = 8;
    private const int MaxLiveUtterances = 12;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<Attempt> _attempts = new();
    private readonly Queue<(int Generation, DateTimeOffset Until, CommittedAgentUtterance Utterance)> _live = new();
    private long _sequence;

    internal enum Admission { Accepted, Duplicate, Busy }
    internal sealed class Attempt(string owner, int generation, Guid id, DateTimeOffset started)
    {
        public string Owner { get; } = owner;
        public int Generation { get; } = generation;
        public Guid Id { get; } = id;
        public DateTimeOffset Started { get; } = started;
        public bool Pending { get; set; } = true;
        public bool Delivered { get; set; }
    }

    public long LatestSequence { get { lock (_gate) return _sequence; } }

    internal Admission Begin(string owner, int generation, Guid id, out Attempt? attempt)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _attempts.RemoveAll(row => !row.Pending && now - row.Started >= TimeSpan.FromMinutes(1));
            attempt = null;
            if (_attempts.Any(row => row.Owner == owner && row.Generation == generation && row.Id == id && row.Delivered))
                return Admission.Duplicate;
            if (_attempts.Count(row => row.Pending) >= MaxConcurrent ||
                _attempts.Any(row => row.Owner == owner && row.Pending) ||
                _attempts.Count(row => row.Owner == owner) >= AttemptsPerMinute)
                return Admission.Busy;
            attempt = new Attempt(owner, generation, id, now);
            _attempts.Add(attempt);
            return Admission.Accepted;
        }
    }

    internal void Complete(Attempt attempt, int npcId, string text, string language, byte[] wav, int durationMs)
    {
        lock (_gate)
        {
            if (!attempt.Pending) return;
            var id = "player-" + Guid.NewGuid().ToString("N");
            var metadata = new AgentUtteranceMetadata
            {
                UtteranceId = id, TurnId = id, Text = text, Language = language, Emotion = "neutral",
                Delivery = AgentSpeechDelivery.PlayerBroadcast, Priority = AgentSpeechPriority.Talk,
                TotalBytes = wav.Length, DurationMilliseconds = durationMs,
                Sha256 = Convert.ToHexString(SHA256.HashData(wav)).ToLowerInvariant()
            };
            _live.Enqueue((attempt.Generation, _clock.GetUtcNow().AddSeconds(15),
                new CommittedAgentUtterance(++_sequence, npcId, metadata, wav)));
            while (_live.Count > MaxLiveUtterances) _live.Dequeue();
            attempt.Delivered = true;
            attempt.Pending = false;
        }
    }

    internal void Abort(Attempt attempt) { lock (_gate) attempt.Pending = false; }

    public CommittedAgentUtterance[] After(long sequence, int generation)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            while (_live.TryPeek(out var first) && first.Until <= now) _live.Dequeue();
            return _live.Where(row => row.Generation == generation && row.Utterance.Sequence > sequence)
                .Select(row => row.Utterance).ToArray();
        }
    }
}
