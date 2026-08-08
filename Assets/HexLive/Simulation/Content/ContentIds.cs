namespace HexLive.Simulation.Content
{

/// <summary>
/// Идентификаторы контента, на которые ссылается ЛОГИКА — по имени, а не строкой.
///
/// <para>
/// Здесь только то, что код называет ПОИМЁННО: «дай ей бинт», «положи открытый
/// кокос», «эта туша разделывается ножом». Каталогу контента этот класс не нужен
/// — там id и есть ключ; он нужен системам, которые про конкретную вещь знают.
/// </para>
/// <para>
/// Зачем: опечатка в свободной строке компилируется, не падает и просто молча
/// перестаёт находить вещь — а находить её должна была логика выживания. Плюс
/// «где вообще используется этот предмет» превращается из грепа по кавычкам в
/// обычный поиск ссылок.
/// </para>
/// <para>
/// ⚠️ Константа — это НЕ решение проблемы, а её честная запись. Если код
/// перечисляет два и больше id, чтобы получить категорию («это собирается
/// руками», «это спальное место»), правильный ответ — тег на определении
/// (<see cref="ObjectTags"/>), а не список имён здесь. Константы для тех
/// случаев, когда речь действительно об одной конкретной вещи.
/// </para>
/// </summary>
public static class ContentIds
{
    // ── Еда и питьё ──────────────────────────────────────────────────────

    /// <summary>Целый кокос: не еда и не питьё, пока его не вскрыли лезвием.</summary>
    public const string Coconut = "food.coconut";

    /// <summary>Проколотый: из него пьют, пока не опустеет.</summary>
    public const string CoconutPierced = "food.coconut_pierced";

    /// <summary>Вскрытый: мякоть, её едят.</summary>
    public const string CoconutOpen = "food.coconut_open";

    public const string MeatRaw = "food.meat_raw";
    public const string MeatCooked = "food.meat_cooked";

    // ── Ресурсы ──────────────────────────────────────────────────────────
    public const string Log = "resource.log";
    public const string Stick = "resource.stick";
    public const string Stone = "resource.stone";
    public const string Fiber = "resource.fiber";
    public const string Rope = "resource.rope";
    public const string Cloth = "resource.cloth";
    public const string Hide = "resource.hide";
    public const string PalmLeaf = "resource.palm_leaf";
    public const string HerbLeaf = "resource.herb_leaf";
    public const string Arrow = "resource.arrow";
    public const string Board = "resource.board";
    public const string MechanicalPart = "resource.mechanical_part";

    // ── Инструменты и вещи ───────────────────────────────────────────────
    public const string Knife = "tool.knife";
    public const string AxeStone = "tool.axe_stone";
    public const string PickaxeStone = "tool.pickaxe_stone";
    public const string Spear = "tool.spear";
    public const string Bow = "tool.bow";
    public const string Bottle = "tool.bottle";

    public const string Bandage = "bandage.herbal";
    public const string Medkit = "bandage.medkit";
    public const string Splint = "med.splint";
    public const string WoodenArm = "prosthetic.arm.wood";
    public const string WoodenLeg = "prosthetic.leg.wood";
    public const string MechanicalArm = "prosthetic.arm.mechanical";
    public const string MechanicalLeg = "prosthetic.leg.mechanical";

    public const string Coat = "clothing.coat";
    public const string LeatherPants = "clothing.leather_pants";

    // ── Постройки и станции ──────────────────────────────────────────────
    public const string Campfire = "campfire.spot";
    public const string BedBasic = "bed.basic";
    public const string BedLeaf = "bed.leaf";
    public const string Tent = "shelter.tent";
    public const string DryingRack = "station.drying_rack";
    public const string WaterCollector = "station.water_collector";
    public const string Workbench = "station.workbench";
    public const string Hut1Hex = "building.hut_1hex";
    public const string HutBed = "building.hut_bed";

    /// <summary>Заявка на мебель: пустое место, куда носят материалы.</summary>
    public const string BuildSite = "build.site";

    /// <summary>Стройка хижины — отдельная от мебельных площадок.</summary>
    public const string ConstructionSite = "construction.site";

    // ── Мёртвое ──────────────────────────────────────────────────────────
    public const string CorpseNpc = "corpse.npc";
    public const string HumanRemains = "remains.human";
    public const string CarcassAnimal = "carcass.animal";
    public const string GraveNpc = "grave.npc";
    public const string PalmStump = "stump.palm";
}

}
