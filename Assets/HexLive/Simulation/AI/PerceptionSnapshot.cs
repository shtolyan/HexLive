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

    // §72/§146.12: visible NON-HOSTILES. In Feud/BigIsland that still means
    // allies only; in the solo-camp modes it also includes neutral visitors so
    // the ordinary Socialize loop can cross camp borders. §146.12 scales Aid
    // severity by directed friendship and gives a dying neutral a humanitarian
    // floor; Outsiders remain zero across the faction boundary.
    public List<PerceivedAgent> Agents { get; } = new();

    // §72: agents this observer is actually at war with. In §146.12 that is
    // still every Outsider, plus a neighbouring girl she personally hates.
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

    // §125.4 / §144.7: ЗВЕРИ, которых она видит прямо сейчас. Отдельный список и
    // отдельный тип — по той же причине, по которой отдельно живут Hostiles и
    // Remembered: у зверя нет ни отношений, ни страдания, ни разговора, и
    // подмешать его к людям значит выдать шести потребителям человека, которым
    // он не является.
    //
    // ⭐ Почему список вообще появился. Внешнее управление (§144) обещало
    // `mobId` для `attack_mob` «из сводки восприятия» — и обещание было ложным:
    // мобов в восприятии не существовало вовсе, каждая система сканировала
    // world.Mobs сама. Живой прогон показал, чем это оборачивается: на
    // колонистку напал зверь, пришло fighting=true при ПУСТОМ hostiles, и
    // внешний контур не мог ни увидеть напавшего, ни ударить в ответ.
    //
    // Читателей у списка пока ровно один — сборщик контекста управления.
    // ⚠️ ThreatAlertSystem намеренно НЕ переведён на него: его ранний выход и
    // разбор ничьих по bestDistance — это поведение, и его правка обязана быть
    // отдельным осознанным коммитом со своей трассой.
    //
    // Runtime-only: ни в сейв, ни в провод не ходит — как Hostiles и Remembered.
    public List<PerceivedMob> Mobs { get; } = new();

    public PerceivedEnvironment Environment { get; } = new();

    public int LastUpdatedTick { get; set; }

    // §22.7 кэш вида памяти: FromMemory-записи пересобираются только когда
    // сменился любой из ключей ниже (компонента её джанкшена, топология,
    // состав памяти, умение прыгать). Тайла NPC в ключе НЕТ нарочно (PERF,
    // Aug-2026): он менялся каждым шагом идущей девушки и пересобирал ~1100
    // записей каждый medium-тик — 2.4 МБ мусора на вызов; исключение «в поле
    // зрения — берёт живой глаз» переехало в точку потребления, где оно
    // стоит одну гекс-дистанцию. Между пересборками записи переиспользуются
    // как есть, освежается только Distance. Runtime-only: ни в сейв, ни в
    // провод не ходит.
    public List<PerceivedObject> MemoryView { get; } = new();

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

    // PERF (Aug-2026): пулы PerceivedObject — по записи на объект, каждое поле
    // переустанавливается при выдаче, как у AgentPool выше. Пула ДВА нарочно:
    // MemoryView держит свои записи МЕЖДУ тиками, и одна общая запись, выданная
    // живому взгляду, мутировала бы застывший вид памяти (FromMemory,
    // занятость) у себя за спиной. Runtime-only.
    public Dictionary<ObjectId, PerceivedObject> LiveObjectPool { get; } = new();

    public Dictionary<ObjectId, PerceivedObject> MemoryObjectPool { get; } = new();
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

/// <summary>
/// §125.4: зверь в поле зрения. Полей ровно столько, сколько нужно, чтобы
/// РЕШИТЬ — бить, бежать или не заметить: кто, где, далеко ли, жив ли и не по
/// мою ли душу.
/// </summary>
public sealed class PerceivedMob
{
    /// <summary>То самое число, которое ждёт приказ атаки. Не EntityId: у
    /// зверей своё пространство идентификаторов.</summary>
    public int Id { get; set; }

    public string MobId { get; set; } = string.Empty;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    /// <summary>В гексах — той же мерой, которой считается сам радиус обзора.</summary>
    public int Distance { get; set; }

    public float Health { get; set; }

    public Wildlife.MobStatus Status { get; set; } = Wildlife.MobStatus.Roaming;

    /// <summary>Он идёт ИМЕННО ЗА НЕЙ. Без этого стая, бредущая мимо, и волк,
    /// вышедший на неё, выглядят одинаково.</summary>
    public bool TargetsMe { get; set; }
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
