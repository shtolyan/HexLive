using System.Text.Json;

namespace HexLive.AgentCore.Studio;

public enum RelationshipDirection { Decrease, Unchanged, Increase }
public sealed record RelationshipAssessment(bool LearnedSomethingSignificant,
    RelationshipDirection Trust, RelationshipDirection Sympathy,
    bool SeriousHarm, string Reason, string? VoiceName = null, string? NamingReason = null);
public sealed record RelationshipSnapshot(float Familiarity, float Trust, float Sympathy,
    string VoiceName, DateTimeOffset? LastContactUtc);

/// <summary>§163: deterministic bounded updates; no model-controlled arbitrary numbers.</summary>
public sealed class VoiceRelationship
{
    private readonly HashSet<string> _messages = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    public RelationshipSnapshot Snapshot { get; private set; }
    public const int DeduplicationCapacity = 4096;

    public VoiceRelationship(RelationshipSnapshot initial)
    {
        if (!float.IsFinite(initial.Familiarity) || !float.IsFinite(initial.Trust) ||
            !float.IsFinite(initial.Sympathy)) throw new InvalidDataException("InvalidRelationship");
        Snapshot = initial with { Familiarity = Math.Clamp(initial.Familiarity, 0, 1),
            Trust = Math.Clamp(initial.Trust, 0, 1), Sympathy = Math.Clamp(initial.Sympathy, -1, 1) };
    }

    public bool Apply(IReadOnlyList<string> messageIds, RelationshipAssessment assessment,
        DateTimeOffset now)
    {
        // Reject partially replayed batches too: regenerate an assessment using only new inputs.
        if (messageIds.Count == 0 || messageIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128) ||
            messageIds.Distinct(StringComparer.Ordinal).Count() != messageIds.Count)
            throw new InvalidDataException("InvalidRelationshipMessageIds");
        if (messageIds.Any(_messages.Contains)) return false;
        if (!Enum.IsDefined(assessment.Trust) || !Enum.IsDefined(assessment.Sympathy) ||
            string.IsNullOrWhiteSpace(assessment.Reason) || assessment.Reason.Length > 240)
            throw new InvalidDataException("InvalidRelationshipAssessment");
        var name = Snapshot.VoiceName;
        if (assessment.VoiceName is { } proposed)
        {
            proposed = proposed.Trim();
            if (proposed.Length is < 1 or > 48 || proposed.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(assessment.NamingReason) || assessment.NamingReason.Length > 240)
                throw new InvalidDataException("InvalidVoiceName");
            name = proposed;
        }
        static float Delta(RelationshipDirection direction, float positive, float negative) => direction switch
        {
            RelationshipDirection.Increase => positive,
            RelationshipDirection.Decrease => -negative,
            _ => 0,
        };
        Snapshot = new(
            Math.Clamp(Snapshot.Familiarity + (assessment.LearnedSomethingSignificant ? .02f : 0), 0, 1),
            Math.Clamp(Snapshot.Trust + Delta(assessment.Trust, .03f, assessment.SeriousHarm ? .06f : .03f), 0, 1),
            Math.Clamp(Snapshot.Sympathy + Delta(assessment.Sympathy, .02f, assessment.SeriousHarm ? .04f : .02f), -1, 1),
            name, now);
        foreach (var id in messageIds) { _messages.Add(id); _order.Enqueue(id); }
        while (_order.Count > DeduplicationCapacity) _messages.Remove(_order.Dequeue());
        return true;
    }

    public string BuildPromptBlock() => "<voice_relationship>\n" + JsonSerializer.Serialize(Snapshot) +
        "\n</voice_relationship>\n" +
        "Familiarity determines recognition, trust determines reliance on advice, sympathy determines warmth. " +
        "Remain autonomous. A familiar person can be disliked. Assess only new messages in context; " +
        "positive, negative and unchanged outcomes are all valid. Disagreement is not abuse. " +
        "Existing dislike does not justify automatically penalizing a new message. " +
        "Do not claim that the voice is inherently precious or that you always miss it.";

    public string ExportState() => JsonSerializer.Serialize(new Saved(Snapshot, _order.ToArray()));
    public static VoiceRelationship Restore(string json)
    {
        var saved = JsonSerializer.Deserialize<Saved>(json) ?? throw new InvalidDataException("InvalidRelationship");
        if (saved.MessageIds.Length > DeduplicationCapacity || saved.MessageIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("InvalidRelationshipHistory");
        var relation = new VoiceRelationship(saved.Snapshot);
        foreach (var id in saved.MessageIds.Distinct(StringComparer.Ordinal))
        { relation._messages.Add(id); relation._order.Enqueue(id); }
        return relation;
    }
    private sealed record Saved(RelationshipSnapshot Snapshot, string[] MessageIds);
}
