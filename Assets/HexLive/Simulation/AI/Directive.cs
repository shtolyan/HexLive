using HexLive.Simulation.Common;

namespace HexLive.Simulation.AI
{

/// <summary>
/// §167: вид указания — о чём одна колонистка просит другую. Не приказ:
/// принятое указание лишь СМЕЩАЕТ аукцион целей (§23.12), а само согласие
/// решают отношения. Дописывать только в конец: ординалы едут в сейв (v78).
/// </summary>
public enum DirectiveKind
{
    None = 0,

    /// <summary>«Займись стройкой» — тяга на Build/BuildFurniture и цепочку сырья.</summary>
    Build = 1,

    /// <summary>«Запасись едой» — принести еду к очагу, даже не будучи голодной.</summary>
    StockFood = 2,

    /// <summary>«Запасись водой» — сосуды под сборщик, кокосы к очагу.</summary>
    StockWater = 3,

    /// <summary>«Заготовь дров» — заложено в enum сразу; тяга в v1.5.</summary>
    Firewood = 4
}

/// <summary>
/// §167.1: принятое указание. Живёт на <see cref="NPCMind.Directive"/>,
/// ПЕРСИСТИТСЯ (обещание переживает сейв, как §53.9 OrderedAidKind).
/// <see cref="FromId"/> == null — указание игрока своему персонажу.
/// </summary>
public sealed class Directive
{
    public DirectiveKind Kind { get; set; }

    public EntityId? FromId { get; set; }

    public int IssuedTick { get; set; }

    public int UntilTick { get; set; }
}

/// <summary>
/// §167.8: просьба, которую ждёт ВНЕШНИЙ разум (агент MCP под §160.1
/// ExternalControl). Транзит: не сериализуется, по таймауту решает сим.
/// </summary>
public sealed class PendingDirective
{
    public DirectiveKind Kind { get; set; }

    public EntityId FromId { get; set; }

    public int SinceTick { get; set; }
}

}
