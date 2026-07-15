using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.AI
{

public sealed class NPCExecutionState
{
    public ExecutionStatus Status { get; set; } = ExecutionStatus.None;

    public InteractionType? CurrentInteraction { get; set; }

    public ObjectId? TargetObject { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }

    public string FailureReason { get; set; } = string.Empty;

    public int LastCompletedTick { get; set; } = -1;

    // Spec 28.15E: the subject of the current Talk, chosen at talk start and
    // mirrored onto both participants so their bubbles agree. Null when not
    // talking; the presentation reads it to pick the overhead emoji.
    public TalkTopic? CurrentTalkTopic { get; set; }

    // Spec 28.15E: last resolved talk outcome, for the Sims-style "+/-"
    // relationship pop over the head. Tick of the roll (presentation fires the
    // pop once per new tick) and the signed affinity delta that was applied
    // (+ on a good chat, - on a quarrel). Magnitude drives single vs double.
    public int LastTalkResultTick { get; set; } = -1;
    public float LastTalkAffinityDelta { get; set; }

    // Presentation-only social cue: invitation, refusal, quarrel, aid, etc.
    // The renderer fires a short overhead bubble once per fresh tick/kind.
    public int LastSocialCueTick { get; set; } = -1;
    public string LastSocialCueKind { get; set; } = string.Empty;
    public EntityId? LastSocialCuePeerId { get; set; }

    // §Wardrobe-anim: the garment currently carried IN HAND during the second
    // beat of an undress (doffed off the body but not yet dropped on the floor).
    // Set at the mid-point of the undress window, cleared when it lands. The
    // view spawns a hand prop from its DefinitionId; dropping preserves its
    // wetness/durability. Null whenever nothing is mid-handoff.
    public ItemInstance HeldGarment { get; set; }
}

public enum ExecutionStatus
{
    None,
    Starting,
    InProgress,
    Completed,
    Failed,
    Waiting
}

}
