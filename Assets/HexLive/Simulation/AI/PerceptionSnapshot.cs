using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.AI
{

public sealed class PerceptionSnapshot
{
    public SelfState Self { get; } = new();

    public List<PerceivedObject> Objects { get; } = new();

    // §72: ALLIES ONLY. Every pre-§72 consumer (Socialize, §53 Aid, the ambient
    // companion trickle, the talk/aid target picks) keeps reading this list
    // unchanged — which is the point: a cooperation path physically cannot
    // reach an enemy, instead of having to remember a gate. Note there is no
    // distance filter here and never was: this is the whole roster, so an
    // ungated enemy would be a chat and aid target island-wide from tick 1.
    public List<PerceivedAgent> Agents { get; } = new();

    // §72: agents we are at war with. Read only by the threat layer (the ⚠️
    // sighting, the detour ring) and by the raider's own target assessment.
    public List<PerceivedAgent> Hostiles { get; } = new();

    // §125.7: ПО ПАМЯТИ — те, кого она сейчас не видит, но помнит по последней
    // встрече. ОТДЕЛЬНЫЙ список, а не флаг FromMemory на PerceivedAgent: у
    // объектов флаг работает, потому что кокос не уходит, а у людей каждое
    // живое поле записи — про тело, которое движется. Подмешай их в Agents —
    // и молча откатятся сразу шесть потребителей: Сострадание утечёт от
    // невидимого страдания, «компания» согреет отсутствующей подругой,
    // Socialize позовёт болтать с той, кого нет, выбор собеседницы погонит
    // через остров, счётчик толпы наполнится призраками, а в сейв поедет вера
    // под видом восприятия. Отдельный тип отвечает на это компилятором.
    public List<RememberedAgent> Remembered { get; } = new();

    public PerceivedEnvironment Environment { get; } = new();

    public int LastUpdatedTick { get; set; }

    // §22.7 кэш вида памяти: FromMemory-записи пересобираются только когда
    // сменился любой из ключей ниже (тайл NPC, компонента её джанкшена,
    // топология, состав памяти, умение прыгать). Между пересборками записи
    // переиспользуются как есть, освежается только Distance — оно считается
    // от живой позиции каждый medium-тик, как и раньше. Runtime-only: ни в
    // сейв, ни в провод не ходит.
    public List<PerceivedObject> MemoryView { get; } = new();

    public TileCoord MemoryViewTile { get; set; } = TileCoord.Zero;

    public int MemoryViewComponent { get; set; } = int.MinValue;

    public int MemoryViewTopology { get; set; } = -1;

    public int MemoryViewMemoryVersion { get; set; } = -1;

    public bool MemoryViewCanJump { get; set; }

    public bool MemoryViewBuilt { get; set; }

    // §22.7 NPC×NPC: пул записей PerceivedAgent этого наблюдателя — по одной
    // на соседку, все поля переустанавливаются каждый прогон восприятия.
    // Без пула каждый medium-тик аллоцировал N×(N-1) объектов с вложенным
    // RelationshipSummary. Записи умерших остаются в пуле (единицы, безвредно).
    // Runtime-only: ни в сейв, ни в провод не ходит.
    public Dictionary<EntityId, PerceivedAgent> AgentPool { get; } = new();

    /// <summary>§125.7: тот же пул для записей по памяти.</summary>
    public Dictionary<EntityId, RememberedAgent> RememberedPool { get; } = new();
}

public sealed class SelfState
{
    public float Hunger { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    public float ThermalDiscomfort { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public FragmentId Fragment { get; set; }
}

public sealed class PerceivedObject
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    // True when the entry comes from spatial memory, not current sight
    // (spec 27.14/27.18A): occupancy is then assumed, not observed.
    public bool FromMemory { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public float Distance { get; set; }

    public bool IsReachable { get; set; }

    public bool IsOccupied { get; set; }

    public EntityId? OccupiedBy { get; set; }

    public List<InteractionType> AvailableInteractions { get; } = new();
}

/// <summary>§125.7: «я помню, что она лежала раненая дома». Полей ровно
/// столько, сколько нужно, чтобы ПОЙТИ ПРОВЕРИТЬ: куда идти, что взять и
/// насколько это срочно. Живых флагов здесь нет и быть не может — честного
/// ответа на «занята ли она сейчас» у памяти не существует.</summary>
public sealed class RememberedAgent
{
    public EntityId Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId? Junction { get; set; }

    /// <summary>Сколько тиков назад её видели: возраст веры. Без него ставка
    /// по памяти неотличима от ставки по глазам, и помощь превратилась бы во
    /// всеведение с задержкой.</summary>
    public int Age { get; set; }

    public float Suffering { get; set; }

    public AidKind AidKind { get; set; } = AidKind.None;

    public bool Helpless { get; set; }
}

public sealed class PerceivedAgent
{
    public EntityId Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public float Distance { get; set; }

    public bool CanSee { get; set; }

    public bool CanHear { get; set; }

    public JunctionId? Junction { get; set; }

    public bool IsReachable { get; set; }

    // Busy = mid-interaction other than Talk (talking agents stay approachable).
    public bool IsBusy { get; set; }

    // Walking agents are not talk targets in v1 (no chasing, spec 28.15A).
    public bool IsMoving { get; set; }

    // Spec §60: out cold (faint or coma) — like a sleeper, never a chat
    // partner. Kept separate from IsBusy so §53 Aid can still target her:
    // unlike a sleeper she cannot wake to help herself.
    public bool IsUnconscious { get; set; }

    // Spec §53: how badly this neighbour needs help (0 = fine, 1 = dying) and
    // the single most-urgent HELPABLE kind of aid. Populated by the perception
    // build so the Aid goal can bid on, and route to, the worst-off housemate
    // without re-scanning every agent's full state.
    public float Suffering { get; set; }

    public AidKind AidKind { get; set; } = AidKind.None;

    // §105: она УМИРАЕТ — у неё тикает запас, и помощь ей не «когда освободишь
    // руки», а сейчас. Отдельным флагом, а не выводом из Suffering == 1: по
    // единице срочности нельзя отличить умирающую от просто очень плохой, а
    // надбавку заслуживает только первая.
    public bool IsDying { get; set; }

    // §72: kept on the entry even though the lists are already split — a trace
    // or a future consumer that concatenates must still be able to tell.
    public Agents.Faction Faction { get; set; } = Agents.Faction.Colony;

    public RelationshipSummary Relationship { get; } = new();
}

// Spec §53: the kind of care a suffering neighbour needs, in priority order of
// urgency. The Aid goal picks the neighbour with the highest Suffering and
// performs the matching interaction.
public enum AidKind
{
    None,
    Feed,     // starving — a well-fed girl shares a meal
    Hydrate,  // parched — bring her water (thirst kills faster than hunger)
    Treat,    // wounded / bleeding — dress the wound
    Medicate, // sick or gravely weak — hand over a pill
    Console   // grieving or breaking under stress — sit with her
}

public sealed class PerceivedEnvironment
{
    public float Temperature { get; set; }

    public bool IsCrowded { get; set; }

    public bool IsPrivate { get; set; }

    public int NearbyAgentsCount { get; set; }
}

}
