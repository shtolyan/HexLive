using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// §121: приказы игрока конкретному NPC. Очередь живёт на движке и опустошается
// в НАЧАЛЕ Step(), до всех систем: приказ, отданный между тиками, действует с
// того же тика, и порядок применения не зависит от того, какая система когда
// проснулась. Отдельной системой это не сделано нарочно — тогда бы приказ ждал
// своей очереди внутри слоя и его место в реестре стало бы поведением.
//
// Кладёт сюда только презентация (ISimulationSource.EnqueueCommand); внутри
// симуляции команд не порождает никто.
public sealed class SimulationCommandQueue
{
    private readonly Queue<ISimulationCommand> _commands = new();

    public int Count => _commands.Count;

    public void Enqueue(ISimulationCommand command) => _commands.Enqueue(command);

    public bool TryDequeue(out ISimulationCommand? command)
    {
        if (_commands.Count == 0)
        {
            command = null;
            return false;
        }

        command = _commands.Dequeue();
        return true;
    }
}

public interface ISimulationCommand
{
    EntityId? TargetEntity { get; }
}

/// <summary>
/// §160: generic, idempotent physical Social effect of one completed agent
/// conversation. Personal relationship axes remain outside the world.
/// </summary>
public sealed class RecordAgentSocialCommand : ISimulationCommand
{
    public RecordAgentSocialCommand(EntityId npc, string turnId,
        Agents.CompanionReaction reaction)
    {
        Npc = npc;
        TurnId = turnId ?? string.Empty;
        Reaction = reaction;
    }

    public EntityId Npc { get; }
    public string TurnId { get; }
    public Agents.CompanionReaction Reaction { get; }
    public EntityId? TargetEntity => Npc;
}

/// <summary>
/// §159/§145: one validated LLM result enters authoritative state through the
/// same typed command boundary as every other external mutation.
/// </summary>
public sealed class RecordCompanionTurnCommand : ISimulationCommand
{
    public RecordCompanionTurnCommand(
        EntityId npc,
        string turnId,
        string trigger,
        Agents.CompanionReaction reaction,
        string intentSummary,
        IReadOnlyList<Agents.CompanionMemoryUpsert>? memoryUpserts = null,
        string journalText = "")
    {
        Npc = npc;
        TurnId = turnId ?? string.Empty;
        Trigger = trigger ?? string.Empty;
        Reaction = reaction;
        IntentSummary = intentSummary ?? string.Empty;
        MemoryUpserts = memoryUpserts ?? new List<Agents.CompanionMemoryUpsert>();
        JournalText = journalText ?? string.Empty;
    }

    public EntityId Npc { get; }
    public string TurnId { get; }
    public string Trigger { get; }
    public Agents.CompanionReaction Reaction { get; }
    public string IntentSummary { get; }
    public IReadOnlyList<Agents.CompanionMemoryUpsert> MemoryUpserts { get; }
    public string JournalText { get; }
    public EntityId? TargetEntity => Npc;
}

/// <summary>
/// §121.1: immediate outcome of submitting one command to the manual-control
/// boundary. This says whether the command was admitted; later path failure,
/// self-defence or completion are order lifecycle outcomes, not rejections.
/// </summary>
public enum ManualCommandAdmissionStatus
{
    Accepted,
    Rejected
}

/// <summary>
/// Structured command-boundary result. Callers must consume this result instead
/// of inferring admission by scanning the bounded simulation event ring.
/// </summary>
public readonly struct ManualCommandAdmission
{
    internal ManualCommandAdmission(
        ManualCommandAdmissionStatus status,
        EntityId? actor,
        string order,
        string reason)
    {
        Status = status;
        Actor = actor;
        Order = order ?? string.Empty;
        Reason = reason ?? string.Empty;
    }

    public ManualCommandAdmissionStatus Status { get; }
    public EntityId? Actor { get; }
    public string Order { get; }
    public string Reason { get; }
    public bool Accepted => Status == ManualCommandAdmissionStatus.Accepted;
}

public interface IGroupSimulationCommand : ISimulationCommand
{
    IReadOnlyList<EntityId> Actors { get; }
}

public abstract class GroupSimulationCommand : IGroupSimulationCommand
{
    protected GroupSimulationCommand(IEnumerable<EntityId> actors)
    {
        var unique = new HashSet<EntityId>();
        var ordered = new List<EntityId>();
        foreach (var actor in actors)
        {
            if (unique.Add(actor)) ordered.Add(actor);
        }
        Actors = ordered;
    }

    public IReadOnlyList<EntityId> Actors { get; }
    public EntityId? TargetEntity => null;
}

/// <summary>§121: взять персонажа под ручное управление или вернуть ИИ.</summary>
public sealed class SetManualControlCommand : ISimulationCommand
{
    public SetManualControlCommand(EntityId npc, bool enabled)
    {
        Npc = npc;
        Enabled = enabled;
    }

    public EntityId Npc { get; }

    public bool Enabled { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.11 (bug #294): постоянный темп ручных приказов персонажа —
/// бегом (истина) или шагом. Состояние, а не приказ: текущий поход не
/// трогается, тумблер управления не переключается.</summary>
public sealed class SetRunByDefaultCommand : ISimulationCommand
{
    public SetRunByDefaultCommand(EntityId npc, bool run)
    {
        Npc = npc;
        Run = run;
    }

    public EntityId Npc { get; }

    public bool Run { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§133.9: freeze or release one NPC's current outfit.</summary>
public sealed class SetOutfitLockCommand : ISimulationCommand
{
    public SetOutfitLockCommand(EntityId npc, bool enabled)
    {
        Npc = npc;
        Enabled = enabled;
    }

    public EntityId Npc { get; }

    public bool Enabled { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: идти в точку. Точка, а не узел: клик игрока приходит по
/// поверхности мира, а ближайший узел — уже дело симуляции.
/// <para>§121.11 (bug #294): <c>Run = null</c> — «темп не задан», то есть взять
/// постоянную настройку персонажа (<c>Mind.RunByDefault</c>). Именно это шлёт
/// клик игрока: жеста-темпа больше нет. Явное значение остаётся для тех, кто
/// действительно знает темп — MCP-инструмент и сценарии.</para></summary>
public sealed class MoveToCommand : ISimulationCommand
{
    public MoveToCommand(EntityId npc, Float2 worldPosition, bool? run = null)
    {
        Npc = npc;
        WorldPosition = worldPosition;
        Run = run;
    }

    public EntityId Npc { get; }

    public Float2 WorldPosition { get; }

    public bool? Run { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: подойти к объекту и сделать с ним ровно это. Тип
/// взаимодействия приходит ИЗ МЕНЮ, то есть из каталога объекта, — поэтому
/// одной цели PlayerOrder хватает на все глаголы сразу.</summary>
public sealed class InteractCommand : ISimulationCommand
{
    public InteractCommand(
        EntityId npc, ObjectId target, InteractionType interaction,
        string interactionId = "")
    {
        Npc = npc;
        Target = target;
        Interaction = interaction;
        InteractionId = interactionId ?? string.Empty;
    }

    public EntityId Npc { get; }

    public ObjectId Target { get; }

    public InteractionType Interaction { get; }

    /// <summary>Exact catalog action selected by the player. Empty keeps the
    /// legacy type-only behaviour used by autonomous and LLM orders.</summary>
    public string InteractionId { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>
/// §121.10 (баг #270): «собрать всё на гексе». Приказ НЕ мгновенный сбор пачкой
/// и не отдельная механика — это ОЧЕРЕДЬ обычных ручных задач: одна и та же
/// <see cref="InteractCommand"/>-логика повторяется по одному подходящему
/// предмету за раз, пока на гексе есть однотипные.
/// <para>
/// Цель — тот самый предмет, по которому кликнул игрок: из него симуляция
/// берёт и гекс, и <c>DefinitionId</c> «однотипности». Считать количество
/// намеренно никто не просил (и UI его не показывает): игрок говорит «все»,
/// а сколько их — знает мир, а не меню.
/// </para>
/// </summary>
public sealed class GatherAllOnHexCommand : ISimulationCommand
{
    public GatherAllOnHexCommand(
        EntityId npc, ObjectId target, InteractionType interaction,
        string interactionId = "")
    {
        Npc = npc;
        Target = target;
        Interaction = interaction;
        InteractionId = interactionId ?? string.Empty;
    }

    public EntityId Npc { get; }

    /// <summary>Кликнутый предмет: якорь гекса и образец однотипности.</summary>
    public ObjectId Target { get; }

    public InteractionType Interaction { get; }

    /// <summary>Точное действие каталога, выбранное игроком.</summary>
    public string InteractionId { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: бить человека.</summary>
public sealed class AttackNpcCommand : ISimulationCommand
{
    public AttackNpcCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§124: вручную поднять лежащего живого человека или свежее тело.</summary>
public sealed class CarryPersonCommand : ISimulationCommand
{
    public CarryPersonCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§124: положить переносимого человека у ног носильщика.</summary>
public sealed class PutDownPersonCommand : ISimulationCommand
{
    public PutDownPersonCommand(EntityId npc) => Npc = npc;

    public EntityId Npc { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§124.1: донести переносимого человека до кровати и уложить в неё.</summary>
public sealed class PutPersonInBedCommand : ISimulationCommand
{
    public PutPersonInBedCommand(EntityId npc, ObjectId bed)
    {
        Npc = npc;
        Bed = bed;
    }

    public EntityId Npc { get; }

    public ObjectId Bed { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: бить зверя. Мобы живут отдельным списком со своей
/// нумерацией, поэтому цель — int, а не EntityId.</summary>
public sealed class AttackMobCommand : ISimulationCommand
{
    public AttackMobCommand(EntityId npc, int mobId)
    {
        Npc = npc;
        MobId = mobId;
    }

    public EntityId Npc { get; }

    public int MobId { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: отставить. Отдельный глагол, а не «выключить ручной режим»:
/// в Kenshi это разные кнопки, и путаница между ними — известный источник
/// «персонаж не слушается» (тумблер не отменяет уже начатое действие).</summary>
public sealed class StopCommand : ISimulationCommand
{
    public StopCommand(EntityId npc)
    {
        Npc = npc;
    }

    public EntityId Npc { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§138: craft one authored item recipe through the normal plan and
/// execution pipeline. The goal is revalidated against RecipeCatalog when the
/// queue is drained; presentation cannot smuggle an arbitrary output.</summary>
public sealed class CraftItemCommand : ISimulationCommand
{
    public CraftItemCommand(EntityId npc, GoalType recipeGoal)
    {
        Npc = npc;
        RecipeGoal = recipeGoal;
    }

    public EntityId Npc { get; }

    public GoalType RecipeGoal { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9: подойти и поговорить с конкретной колонисткой. Носит
/// родную цель Socialize — рукопожатие, темы и исходы играет штатный RunTalk;
/// занятая или несклонная цель откажет ПО ПРИБЫТИИ, ровно как своей.</summary>
public sealed class TalkToCommand : ISimulationCommand
{
    public TalkToCommand(EntityId npc, EntityId target,
        Social.TalkTopic? requestedTopic = null)
    {
        Npc = npc;
        Target = target;
        RequestedTopic = requestedTopic;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public Social.TalkTopic? RequestedTopic { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§127: initiate a paired romantic interaction. Forced is an
/// explicit dark order; the simulation still revalidates sex, faction, trait,
/// health, reach and interruption conditions.</summary>
public sealed class RomancePersonCommand : ISimulationCommand
{
    public RomancePersonCommand(EntityId npc, EntityId target, bool forced)
    {
        Npc = npc;
        Target = target;
        Forced = forced;
    }

    public EntityId Npc { get; }
    public EntityId Target { get; }
    public bool Forced { get; }
    public EntityId? TargetEntity => Npc;
}

/// <summary>
/// §146.12: merge the player's camp with a neighbouring girl camp after both
/// sides have more than 50% affinity. <c>UseTargetCamp</c> chooses whether the
/// neighbour's camp is occupied as the shared home; false invites her home.
/// </summary>
/// <summary>§146.14 (bug #291): сделать СВОЙ очаг домом лагеря. Меняет только
/// якорь FactionHomes своей фракции (+DoorStateVersion); чужой очаг
/// отклоняется — присоединение к чужому лагерю остаётся дипломатией §146.12.
/// Актор — NPC: фракция дома берётся из неё, отказ приходит тостом.</summary>
public sealed class SetCampHomeCommand : ISimulationCommand
{
    public SetCampHomeCommand(EntityId npc, ObjectId hearth)
    {
        Npc = npc;
        Hearth = hearth;
    }

    public EntityId Npc { get; }

    public ObjectId Hearth { get; }

    public EntityId? TargetEntity => Npc;
}

public sealed class MergeCampsCommand : ISimulationCommand
{
    public MergeCampsCommand(EntityId npc, EntityId target, bool useTargetCamp)
    {
        Npc = npc;
        Target = target;
        UseTargetCamp = useTargetCamp;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public bool UseTargetCamp { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9: помочь конкретной колонистке ЯВНЫМ видом помощи (§53).
/// Вид выбирает игрок — в этом смысл ручного режима; расход припаса и сама
/// помощь идут тем же RunAid, что у автономной помощницы.</summary>
public sealed class AidPersonCommand : ISimulationCommand
{
    public AidPersonCommand(EntityId npc, EntityId target, AidKind kind)
    {
        Npc = npc;
        Target = target;
        Kind = kind;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public AidKind Kind { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9: приладить или починить протез (§116/§118). Медицинская
/// помощь (перевязка, лекарство, шина) идёт отдельным единым приказом.</summary>
public sealed class TreatLimbsCommand : ISimulationCommand
{
    public TreatLimbsCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9: единая медицинская помощь. Исполнительница сама выбирает
/// актуальное доступное действие: остановить кровь/обработать рану (включая
/// свежую культю), дать лекарство или наложить шину. Протез остаётся отдельным
/// осознанным приказом.</summary>
public sealed class MedicalAidCommand : ISimulationCommand
{
    public MedicalAidCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9 (тёмная фаза, за Spec121.ManualDarkOrdersEnabled):
/// выследить и убить СОСЕДКУ ради мяса (§56). Родная цель Prey: удары ведёт
/// та же PredationSystem, что у автономного каннибализма, — по смежной
/// союзнице с наименьшим здоровьем.</summary>
public sealed class PreyPersonCommand : ISimulationCommand
{
    public PreyPersonCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9 (тёмная фаза): затеять сцену травли §81 против чужака —
/// отжать припас или «контакт». Родная цель Abuse: сцену ведёт штатный
/// RunAbuse со всеми последствиями (свидетельницы, защитницы, урон доверию).</summary>
public sealed class AbusePersonCommand : ISimulationCommand
{
    public AbusePersonCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121.9: виды самодействий — то, что колонистка делает сама с собой
/// или на месте. Enum живёт только в командном слое: в сейв не пишется,
/// в снапшот не едет.</summary>
public enum SelfActionKind
{
    CallForHelp,   // крик о помощи §57.9 — не сносит текущий план
    TreatSelf,     // перевязать себя §68
    GroundSit,     // присесть на землю/уступ §137
    GroundSleep,   // лечь спать на землю §29G
    Bathe,         // искупаться §40.5
    WashClothes,   // постирать §40.6
    EatFromPack,   // поесть из рюкзака (§121.6, но по явному приказу)
    DrinkFromPack, // попить из рюкзака
    GoHome         // бежать в домашний лагерь §121.9/§140.2
}

/// <summary>§121.9: самодействие. Одна команда на все виды — один кейс в
/// исполнителе, в UI, в MCP и в LLM-переводчике; сами планы ставят те же
/// билдеры, что у автономной (§138: родная цель + белый список).</summary>
public sealed class SelfActionCommand : ISimulationCommand
{
    public SelfActionCommand(EntityId npc, SelfActionKind kind)
    {
        Npc = npc;
        Kind = kind;
    }

    public EntityId Npc { get; }

    public SelfActionKind Kind { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§123: one target point, many independently placed actors.</summary>
public sealed class GroupMoveCommand : GroupSimulationCommand
{
    public GroupMoveCommand(
        IEnumerable<EntityId> actors, Float2 worldPosition, bool? run = null)
        : base(actors)
    {
        WorldPosition = worldPosition;
        Run = run;
    }

    public Float2 WorldPosition { get; }

    /// <summary>§121.11: <c>null</c> — у каждой участницы свой постоянный темп.
    /// Один общий флаг на группу был бы ровно тем враньём, от которого настройка
    /// и заводится: у девушек она разная.</summary>
    public bool? Run { get; }
}

public sealed class GroupStopCommand : GroupSimulationCommand
{
    public GroupStopCommand(IEnumerable<EntityId> actors) : base(actors) { }
}

public sealed class GroupAttackNpcCommand : GroupSimulationCommand
{
    public GroupAttackNpcCommand(IEnumerable<EntityId> actors, EntityId target)
        : base(actors) => Target = target;

    public EntityId Target { get; }
}

public sealed class GroupAttackMobCommand : GroupSimulationCommand
{
    public GroupAttackMobCommand(IEnumerable<EntityId> actors, int mobId)
        : base(actors) => MobId = mobId;

    public int MobId { get; }
}

public sealed class SetGroupManualControlCommand : GroupSimulationCommand
{
    public SetGroupManualControlCommand(IEnumerable<EntityId> actors, bool enabled)
        : base(actors) => Enabled = enabled;

    public bool Enabled { get; }
}

/// <summary>§120.7: разметить дом по утверждённому плану игрока на выбранном
/// гексе. Команда МИРОВАЯ (актор — колония, не NPC): она создаёт обычную
/// стройплощадку плана, а дальше девушки носят и строят штатным §120-циклом.
/// Правду о пригодности места решает исполнитель тем же
/// <c>BuildingBootstrap.CreateHutPlanSite</c>, что и bootstrap-мир.</summary>
public sealed class PlaceBuildingPlanCommand : ISimulationCommand
{
    public PlaceBuildingPlanCommand(TileCoord tile, float rotationDegrees)
    {
        Tile = tile;
        RotationDegrees = rotationDegrees;
    }

    public TileCoord Tile { get; }

    /// <summary>Поворот здания; исполнитель квантует к шести симметриям.</summary>
    public float RotationDegrees { get; }

    public EntityId? TargetEntity => null;
}

/// <summary>§120.7: разметить одиночное изделие каталога (кровать, гардероб,
/// верстак, костёр, сушилку, сборник воды) стройплощадкой на выбранном гексе —
/// §66: одна постройка на гекс, всегда в его центре. Идёт та же цепочка, что у
/// мечт §64: билль, доставка, стройка, подъём готового предмета.</summary>
public sealed class PlaceFurnitureSiteCommand : ISimulationCommand
{
    public PlaceFurnitureSiteCommand(string catalogId, TileCoord tile, float rotationDegrees)
    {
        CatalogId = catalogId ?? string.Empty;
        Tile = tile;
        RotationDegrees = rotationDegrees;
    }

    /// <summary>Строка Build/Buy-каталога (§120), не произвольный id объекта:
    /// исполнитель отклоняет всё, чего нет в <c>BuildCatalogDefinition</c>.</summary>
    public string CatalogId { get; }

    public TileCoord Tile { get; }

    public float RotationDegrees { get; }

    public EntityId? TargetEntity => null;
}

/// <summary>§120.8: разметить ПРОИЗВОЛЬНЫЙ чертёж игрока (свободная
/// архитектура из конструктора). Чертёж едет своим же JSON-форматом
/// (<c>BuildingBlueprintJson</c>) — исполнитель парсит, валидирует
/// (<c>BlueprintValidator</c> + пол + дверь + футпринт) и только тогда
/// регистрирует его в мире и ставит площадку. Tile — куда ложится
/// <c>BlueprintBuildingPlan.AnchorTile</c> чертежа.</summary>
public sealed class PlaceBuildingBlueprintCommand : ISimulationCommand
{
    public PlaceBuildingBlueprintCommand(string blueprintJson, TileCoord tile, float rotationDegrees)
    {
        BlueprintJson = blueprintJson ?? string.Empty;
        Tile = tile;
        RotationDegrees = rotationDegrees;
    }

    public string BlueprintJson { get; }

    public TileCoord Tile { get; }

    public float RotationDegrees { get; }

    public EntityId? TargetEntity => null;
}

/// <summary>§120.9: replace the composition of an existing modular building.
/// Owner is the footprint aggregate, never an individual wall object. The
/// executor validates placement and reconciles the draft by stable SlotKey.</summary>
public sealed class UpdateBuildingBlueprintCommand : ISimulationCommand
{
    public UpdateBuildingBlueprintCommand(ObjectId owner, string blueprintJson)
    {
        Owner = owner;
        BlueprintJson = blueprintJson ?? string.Empty;
    }

    public ObjectId Owner { get; }

    public string BlueprintJson { get; }

    public EntityId? TargetEntity => null;
}

/// <summary>
/// §120.10: one canonical, absolute architecture LEGO placement in the live
/// world. This is deliberately not a BuildingBlueprintDraft: the tile is only
/// the physical anchor used to render/build this ONE piece, while the geometry
/// key is already in the world's global build lattice.
/// </summary>
public sealed class FreeArchitecturePlacementData
{
    public FreeArchitecturePlacementData(
        BlueprintElementKind kind,
        TileCoord anchorTile,
        HexBuildNodeKey node,
        BuildSegmentKey segment,
        FloorSectorKey floorSector,
        RoofSectorKey roofSector)
    {
        Kind = kind;
        AnchorTile = anchorTile;
        Node = node;
        Segment = segment;
        FloorSector = floorSector;
        RoofSector = roofSector;
    }

    public BlueprintElementKind Kind { get; }
    public TileCoord AnchorTile { get; }
    public HexBuildNodeKey Node { get; }
    public BuildSegmentKey Segment { get; }
    public FloorSectorKey FloorSector { get; }
    public RoofSectorKey RoofSector { get; }

    public BlueprintElementData ToElement(string id) => new BlueprintElementData
    {
        Id = id ?? string.Empty,
        Kind = Kind,
        Origin = BlueprintElementOrigin.Manual,
        Node = Node,
        Segment = Segment,
        FloorSector = FloorSector,
        RoofSector = RoofSector
    };

    public string SlotKey => BlueprintBuildingPlan.SlotKey(ToElement("free"));

    public static FreeArchitecturePlacementData FromElement(BlueprintElementData element)
    {
        var anchor = element.Kind switch
        {
            BlueprintElementKind.FloorSector => element.FloorSector.Hex,
            BlueprintElementKind.RoofSector => element.RoofSector.Hex,
            BlueprintElementKind.Support => HexSpatialMath.WorldToTile(
                BlueprintGeometry.ToWorld(element.Node)),
            _ => HexSpatialMath.WorldToTile(new Float2(
                (BlueprintGeometry.ToWorld(element.Segment.A).X +
                 BlueprintGeometry.ToWorld(element.Segment.B).X) * 0.5f,
                (BlueprintGeometry.ToWorld(element.Segment.A).Y +
                 BlueprintGeometry.ToWorld(element.Segment.B).Y) * 0.5f))
        };
        return new FreeArchitecturePlacementData(
            element.Kind, anchor, element.Node, element.Segment,
            element.FloorSector, element.RoofSector);
    }
}

/// <summary>
/// §120.10: atomically apply one direct world-building gesture. Placements are
/// independent construction objects; removed slot keys are direct demolition,
/// not deletion of a plan. A wall drag or room drag therefore crosses the wire
/// as one user transaction without ever creating a blueprint registry entry.
/// </summary>
public sealed class ApplyFreeArchitectureCommand : ISimulationCommand
{
    public ApplyFreeArchitectureCommand(
        IReadOnlyList<FreeArchitecturePlacementData> placements,
        IReadOnlyList<string> removedSlotKeys)
    {
        Placements = placements is null
            ? new List<FreeArchitecturePlacementData>()
            : new List<FreeArchitecturePlacementData>(placements);
        RemovedSlotKeys = removedSlotKeys is null
            ? new List<string>()
            : new List<string>(removedSlotKeys);
    }

    public IReadOnlyList<FreeArchitecturePlacementData> Placements { get; }
    public IReadOnlyList<string> RemovedSlotKeys { get; }

    public EntityId? TargetEntity => null;
}

/// <summary>§120.7: повернуть ПУСТУЮ размеченную площадку (ни одного
/// доставленного материала). Абсолютный угол; исполнитель квантует и для
/// дома перепроверяет футпринт новой ориентации.</summary>
public sealed class RotateBuildSiteCommand : ISimulationCommand
{
    public RotateBuildSiteCommand(ObjectId site, float rotationDegrees)
    {
        Site = site;
        RotationDegrees = rotationDegrees;
    }

    public ObjectId Site { get; }

    public float RotationDegrees { get; }

    public EntityId? TargetEntity => null;
}

/// <summary>§120.7: снять ПУСТУЮ размеченную площадку. Бесплатно, как и
/// колышки; площадка с доставленным материалом отклоняется — разбор
/// начатой стройки — отдельная механика, не тихий despawn ресурсов.</summary>
public sealed class CancelBuildSiteCommand : ISimulationCommand
{
    public CancelBuildSiteCommand(ObjectId site) => Site = site;

    public ObjectId Site { get; }

    public EntityId? TargetEntity => null;
}

public enum InventoryItemSource
{
    Carried,
    Worn
}

public readonly struct InventoryItemRef
{
    public InventoryItemRef(InventoryItemSource source, int index, string expectedDefinitionId)
    {
        Source = source;
        Index = index;
        ExpectedDefinitionId = expectedDefinitionId ?? string.Empty;
    }

    public InventoryItemSource Source { get; }
    public int Index { get; }
    public string ExpectedDefinitionId { get; }
}

public enum InventoryAction
{
    Wear,
    Stow,
    Drop
}

public sealed class ManageInventoryCommand : ISimulationCommand
{
    public ManageInventoryCommand(
        EntityId npc, InventoryItemRef item, InventoryAction action, int count = 1)
    {
        Npc = npc;
        Item = item;
        Action = action;
        Count = count;
    }

    public EntityId Npc { get; }
    public InventoryItemRef Item { get; }
    public InventoryAction Action { get; }
    public int Count { get; }
    public EntityId? TargetEntity => Npc;
}

/// <summary>
/// §55.4 (bug #317): «Наполнить» — перелить воду прочих ёмкостей инвентаря в
/// выбранную (сегодня приёмник — только личная бутылка; кокос — источник,
/// у его инстанса нет вида воды). Ячейка называется как в
/// <see cref="ManageInventoryCommand"/> — индекс + ожидаемый id: панель
/// рисует прошлый тик, устаревший приказ честно отклоняется. Сам перелив —
/// небыстрый процесс на месте (FillVesselDurationTicks) через штатный план.
/// </summary>
public sealed class FillVesselCommand : ISimulationCommand
{
    public FillVesselCommand(EntityId npc, InventoryItemRef item)
    {
        Npc = npc;
        Item = item;
    }

    public EntityId Npc { get; }

    public InventoryItemRef Item { get; }

    public EntityId? TargetEntity => Npc;
}

public enum InventoryTransferDirection
{
    Take,
    Give,
    TakeAndWear
}

/// <summary>
/// §128: move one visible inventory cell between the selected colonist and an
/// unconscious person. The simulation resolves the physical instances again
/// after the approach, so a stale UI can never duplicate or delete an item.
/// </summary>
public sealed class TransferInventoryCommand : ISimulationCommand
{
    public TransferInventoryCommand(
        EntityId looter,
        EntityId other,
        InventoryItemRef item,
        int count,
        InventoryTransferDirection direction)
    {
        Looter = looter;
        Other = other;
        Item = item;
        Count = count > 0 ? count : 1;
        Direction = direction;
    }

    public EntityId Looter { get; }
    public EntityId Other { get; }
    public InventoryItemRef Item { get; }
    public int Count { get; }
    public InventoryTransferDirection Direction { get; }
    public EntityId? TargetEntity => Looter;
}

/// <summary>
/// §128.5: то же самое, но вторая сторона — ВЕЩЬ: истлевшее тело, снятый
/// рюкзак, аптечка. Ячейка называется индексом в раскладке содержимого плюс
/// ожидаемым id: панель рисует прошлый тик, и к моменту приказа содержимое
/// могло измениться — тогда приказ честно отклоняется, а не берёт «что попало
/// под этим номером».
/// </summary>
public sealed class TransferContainerCommand : ISimulationCommand
{
    public TransferContainerCommand(
        EntityId looter,
        ObjectId container,
        int slotIndex,
        string expectedDefinitionId,
        int count,
        InventoryTransferDirection direction)
    {
        Looter = looter;
        Container = container;
        SlotIndex = slotIndex;
        ExpectedDefinitionId = expectedDefinitionId ?? string.Empty;
        Count = count > 0 ? count : 1;
        Direction = direction;
    }

    public EntityId Looter { get; }
    public ObjectId Container { get; }
    public int SlotIndex { get; }
    public string ExpectedDefinitionId { get; }
    public int Count { get; }
    public InventoryTransferDirection Direction { get; }
    public EntityId? TargetEntity => Looter;
}

}
