namespace HexLive.Simulation.Content
{

/// <summary>
/// Имена тегов объектов — в одном месте.
///
/// <para>
/// Тег отвечает на вопрос «что это за штука», и по нему ветвится решение: годится
/// ли объект под цель (<c>IsValidTargetFor</c>), можно ли собрать постройку
/// руками, считается ли это едой. То есть тег — часть КОНТРАКТА между контентом
/// и логикой, а не пометка для человека.
/// </para>
/// <para>
/// Пока это были свободные строки в 78 местах, контракт держался на том, что
/// никто не опечатается: <c>Tags.Contains("Campfre")</c> компилируется, не падает
/// и просто навсегда возвращает false. Такую ошибку не видно ни в ревью, ни в
/// логе — видно только по тому, что колония почему-то не готовит.
/// </para>
/// <para>
/// ⭐ Теги ДОПОЛНЯЮТСЯ, а не заменяются: <c>WorldObjectLibrary.ApplyTo</c>
/// подмешивает теги ассета к тегам каталога кода. Значит новый тег можно завести
/// прямо здесь и в <see cref="PrototypeContentCatalog"/>, не трогая Unity и не
/// переснимая экспорт, — ассет о нём просто не знает и ничего не отнимает.
/// </para>
/// </summary>
public static class ObjectTags
{
    // ── Что это по сути ──────────────────────────────────────────────────
    public const string Food = "Food";
    public const string Water = "Water";
    public const string RawWater = "RawWater";
    public const string Medicine = "Medicine";
    public const string Resource = "Resource";
    public const string Tool = "Tool";
    public const string Weapon = "Weapon";

    // ── Конкретные вещи, на которые смотрит логика ───────────────────────
    public const string Coconut = "Coconut";
    public const string CoconutWater = "CoconutWater";
    public const string RawMeat = "RawMeat";
    public const string Herb = "Herb";
    public const string HerbBush = "HerbBush";
    public const string Fiber = "Fiber";
    public const string Cloth = "Cloth";
    public const string Rope = "Rope";
    public const string Hide = "Hide";
    public const string Log = "Log";
    public const string Stick = "Stick";
    public const string Stone = "Stone";
    public const string Wood = "Wood";

    // ── Флора и добыча ───────────────────────────────────────────────────
    public const string Flora = "Flora";
    public const string Palm = "Palm";
    public const string PalmCrown = "PalmCrown";
    public const string PalmLeaf = "PalmLeaf";
    public const string Yucca = "Yucca";
    public const string Boulder = "Boulder";
    public const string Stump = "Stump";

    // ── Мёртвое ──────────────────────────────────────────────────────────
    public const string Corpse = "Corpse";
    public const string Carcass = "Carcass";
    public const string Grave = "Grave";
    public const string Gore = "Gore";

    // ── Постройки и мебель ───────────────────────────────────────────────
    public const string Campfire = "Campfire";
    public const string Bed = "Bed";
    public const string Chair = "Chair";
    public const string Station = "Station";
    public const string Rack = "Rack";
    public const string Shade = "Shade";
    public const string Shelter = "Shelter";
    public const string Raft = "Raft";
    public const string BuildSite = "BuildSite";
    public const string FurnitureSite = "FurnitureSite";

    // ── Свойства ─────────────────────────────────────────────────────────
    public const string Obstacle = "Obstacle";
    public const string Hazard = "Hazard";
    public const string Decays = "Decays";

    // ── Требуемый инструмент ─────────────────────────────────────────────
    public const string Axe = "Axe";
    public const string Knife = "Knife";
    public const string Machete = "Machete";
    public const string Saw = "Saw";
    public const string Pickaxe = "Pickaxe";
    public const string Hammer = "Hammer";

    /// <summary>
    /// §54: собирается РУКАМИ, молоток не нужен.
    /// <para>
    /// Раньше это правило жило в коде списком-отрицанием:
    /// <c>BuildProduct is not ("campfire.spot" or "bed.leaf" or ...)</c> — то
    /// есть свойство КОНТЕНТА («костёр складывают из камней, циновку и сушилку
    /// вяжут руками») было записано перечислением идентификаторов в исполнителе.
    /// Новая постройка молча получала обратное поведение по умолчанию, и узнать
    /// об этом можно было только по тому, что её никто не строит.
    /// </para>
    /// </summary>
    public const string HandBuilt = "HandBuilt";
}

}
