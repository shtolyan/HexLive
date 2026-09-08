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

    // §52 / bug #355: exact physical inventory item reserved across a delayed
    // transfer or timed vessel interaction. SourceIndex + definition cannot
    // distinguish two identical instances after one is moved. This transient
    // reference is rebuilt only when the action starts; a save during the
    // short action fails safely.
    public ItemInstance TargetInventoryItem { get; set; }

    // §128.5 / bug #355: rack and collector slots are physical world objects,
    // not ItemInstance references. Preserve that object's stable id while the
    // looter walks to the container; a mutable UI row index is insufficient.
    public ObjectId? TargetInventoryWorldObject { get; set; }

    // §111.13: КАКУЮ станцию лежащего тела этот персонаж держит. Заявка живёт на
    // актёре, а не на пациентке: занятость выводится обходом держателей с
    // предикатом живости и потому самолечится, тогда как поле-заявка на теле
    // («слот k занят таким-то») пришлось бы чистить во всех выходах из сцены —
    // аборт плана, пробуждение, смерть, подъём на руки, — и один забытый навсегда
    // отнимал бы место. -1 = не держит ничего.
    public EntityId? LyingStationTargetId { get; set; }

    public int LyingStationSlot { get; set; } = -1;

    public int StartTick { get; set; }

    public int EndTick { get; set; }

    public string FailureReason { get; set; } = string.Empty;

    public int LastCompletedTick { get; set; } = -1;

    // Spec 28.15E: the subject of the current Talk, chosen at talk start and
    // mirrored onto both participants so their bubbles agree. Null when not
    // talking; the presentation reads it to pick the overhead emoji.
    public TalkTopic? CurrentTalkTopic { get; set; }

    // §108: О КОМ идёт речь, когда тема — человек (сегодня только Stranger).
    // Без этого поля бабл может показать «мы говорим о чужаке», но не ЛИЦО:
    // портрет берётся по id. Null для всех остальных тем.
    public HexLive.Simulation.Common.EntityId? CurrentTalkTopicPeerId { get; set; }

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
    // Bug #52: optional item picture carried by the same one-shot cue. Kept
    // separate from Kind so content ids remain data, not an unbounded event set.
    public string LastSocialCueItemId { get; set; } = string.Empty;

    // §Wardrobe-anim: the garment currently carried IN HAND during the second
    // beat of an undress (doffed off the body but not yet dropped on the floor).
    // Set at the mid-point of the undress window, cleared when it lands. The
    // view spawns a hand prop from its DefinitionId; dropping preserves its
    // wetness/durability. Null whenever nothing is mid-handoff.
    public ItemInstance HeldGarment { get; set; }

    // §40.6 r2 (laundry-in-hand): pocket items riding inside a ground garment
    // that was picked UP into the hand for washing — restored into the piece
    // when it is laid back down (finish or interrupt), so a jacket's stashed
    // knife survives the wash.
    public System.Collections.Generic.List<ItemInstance> HeldGarmentContents { get; } = new();

    // §77: the carried materials have already gone into the build-site's pile
    // this interaction — set at the mid-animation handoff, so the completion
    // pass neither deposits twice nor emits a second SiteDelivered. Reset when
    // an interaction starts; false simply means "the deposit still owes".
    public bool BuildDeposited { get; set; }

    // §gear-craft v2: ids of the ground objects the staged in-place craft is
    // working over — the laid-out ingredients during the Craft beat, then the
    // finished output item(s) during the take (PickUp) beat. Whatever is still
    // alive at each beat's end gets consumed / taken; an aborted craft simply
    // leaves them lying as ordinary world items.
    public System.Collections.Generic.List<ObjectId> CraftLayout { get; } = new();

    // §119: output object receiving this 24-work-unit cycle. The progress is
    // owned by the object; this link is only the worker's current handle.
    public ObjectId? CraftProjectId { get; set; }

    public int CraftCycleStartWork { get; set; }
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
