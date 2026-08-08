using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.AI
{

/// <summary>Как быстро NPC движется К этой цели.</summary>
public enum UrgencyClass
{
    /// <summary>Обычным шагом.</summary>
    Stroll,

    /// <summary>Бежит на подмогу / догоняет (§57, §89).</summary>
    Hurry,

    /// <summary>Спасает жизнь (§29C.4A).</summary>
    Flee,
}

/// <summary>
/// Всё, что СИСТЕМЫ знают про цель — в одной строке таблицы.
///
/// <para>
/// До этого знание о цели было размазано по десятку switch'ей в разных файлах:
/// какое взаимодействие ей соответствует, бежать ли к ней, обходит ли она
/// опасные кольца, что кладёт на землю крафт. Добавление одной цели требовало
/// правок в 10-14 местах 38 файлов, и забытое место не падало — оно просто
/// молча давало цели поведение по умолчанию.
/// </para>
/// <para>
/// ⚠️ Здесь только ДАННЫЕ о цели. Всё, что зависит от состояния NPC («голодна ли
/// она», «доступен ли этот объект ЕЙ»), в таблицу не переезжает: это не свойство
/// цели, а вычисление, и вид `Func` в строке таблицы сделал бы её нечитаемой,
/// ничего не собрав в одно место.
/// </para>
/// </summary>
public sealed class GoalDescriptor
{
    public GoalType Goal { get; set; }

    /// <summary>Какое взаимодействие исполняет эту цель. Null — цель без
    /// взаимодействия с объектом (Idle, Explore, разговорные).</summary>
    public InteractionType? Interaction { get; set; }

    /// <summary>Как спешит. Разрешается в конкретный множитель на месте
    /// чтения — не значением здесь, иначе таблица заморозила бы ручки
    /// баланса на момент своей постройки.</summary>
    public UrgencyClass Urgency { get; set; } = UrgencyClass.Stroll;

    /// <summary>
    /// §72: идёт напролом мимо колец ВРАЖДЕБНОЙ ФРАКЦИИ. Не «храбрая», а «ей
    /// туда и надо»: бегущей нельзя гнуть маршрут вокруг того, от кого она
    /// бежит, а налётчик сам и есть враждебная сторона.
    /// <para>
    /// ⚠️ Кольца ЗВЕРЯ (§62, <c>AvoidsThreatRings</c>) — ОТДЕЛЬНЫЙ набор, и он
    /// уже: налётчика в нём нет. Разница намеренная: волк одинаково страшен
    /// обеим сторонам, охота на человека от этого не защищает. Не сводить в
    /// одну колонку — совпадение двух наборов здесь случайное.
    /// </para>
    /// </summary>
    public bool IgnoresHostileRings { get; set; }

    /// <summary>Ставится системой напрямую, минуя аукцион (§62, §72, §81).</summary>
    public bool IsReactive { get; set; }

    /// <summary>Ординал занят, смысла нет. Удалить нельзя — сейв хранит цели
    /// числом, вырезание середины перемаркировало бы каждую цель в каждом
    /// существующем сейве.</summary>
    public bool IsDead { get; set; }

    /// <summary>Вооружается сразу при получении цели и убирает оружие, когда
    /// цель снята. Это намерение, а не текущий замах: поход к противнику уже
    /// должен читаться как боевой.</summary>
    public bool ReadiesMeleeWeapon { get; set; }

    // ── Крафт ────────────────────────────────────────────────────────────

    /// <summary>Что крафт выкладывает на землю на такте «взять». Null —
    /// выход не предмет (счётчик бинтов, надетые штаны).</summary>
    public string[] CraftGroundOutputs { get; set; }

    /// <summary>Имя события об успешном крафте. Держится стабильным: по нему
    /// считают метрики соаков.</summary>
    public string CraftTraceName { get; set; }

    /// <summary>Требует рабочих рук: со связанными/отнятыми не сделать.</summary>
    public bool CraftNeedsHands { get; set; }
}

/// <summary>
/// Таблица целей. Индексируется ординалом <see cref="GoalType"/> — единственный
/// случай, когда append-only ординалы из ограничения превращаются в удобство.
/// </summary>
public static class GoalCatalog
{
    private static readonly GoalDescriptor[] ByOrdinal;

    public static IReadOnlyList<GoalDescriptor> All => ByOrdinal;

    public static GoalDescriptor For(GoalType goal)
    {
        var index = (int)goal;
        return index >= 0 && index < ByOrdinal.Length ? ByOrdinal[index] : null;
    }

    public static InteractionType? InteractionFor(GoalType goal) => For(goal)?.Interaction;

    public static bool IsReactive(GoalType goal) => For(goal)?.IsReactive ?? false;

    public static bool IsDead(GoalType goal) => For(goal)?.IsDead ?? false;

    public static bool ReadiesMeleeWeapon(GoalType goal) =>
        For(goal)?.ReadiesMeleeWeapon ?? false;

    public static UrgencyClass UrgencyFor(GoalType goal) =>
        For(goal)?.Urgency ?? UrgencyClass.Stroll;

    public static bool IgnoresHostileRings(GoalType goal) =>
        For(goal)?.IgnoresHostileRings ?? false;

    public static string[] CraftGroundOutputs(GoalType goal) => For(goal)?.CraftGroundOutputs;

    public static string CraftTraceName(GoalType goal) =>
        For(goal)?.CraftTraceName ?? "CraftedItem";

    public static bool CraftNeedsHands(GoalType goal) => For(goal)?.CraftNeedsHands ?? false;

    static GoalCatalog()
    {
        var rows = new List<GoalDescriptor>();

        void Add(GoalType goal, InteractionType? interaction = null,
            UrgencyClass urgency = UrgencyClass.Stroll,
            bool ignoresHostileRings = false, bool reactive = false, bool dead = false,
            string[] craftOutputs = null, string craftTrace = null, bool craftNeedsHands = false,
            bool readiesMeleeWeapon = false)
        {
            rows.Add(new GoalDescriptor
            {
                Goal = goal,
                Interaction = interaction,
                Urgency = urgency,
                IgnoresHostileRings = ignoresHostileRings,
                IsReactive = reactive,
                IsDead = dead,
                ReadiesMeleeWeapon = readiesMeleeWeapon,
                CraftGroundOutputs = craftOutputs,
                CraftTraceName = craftTrace,
                CraftNeedsHands = craftNeedsHands,
            });
        }

        // ── Ничего не делает ─────────────────────────────────────────────
        Add(GoalType.None);
        Add(GoalType.Idle);
        Add(GoalType.Explore);

        // ── Еда и питьё ──────────────────────────────────────────────────
        Add(GoalType.Eat);
        Add(GoalType.GetFood, InteractionType.PickUp);
        Add(GoalType.Drink);
        Add(GoalType.GetWater, InteractionType.PickUp); // §55: несёт кокос, чтобы вскрыть
        Add(GoalType.StowBottle, InteractionType.PlaceVessel); // §54.15

        // ── Отдых, одежда, гигиена ───────────────────────────────────────
        Add(GoalType.Sleep, InteractionType.Sleep);
        Add(GoalType.Sit, InteractionType.Sit);
        Add(GoalType.Dress, InteractionType.Dress);
        Add(GoalType.Undress);
        Add(GoalType.Bathe);
        Add(GoalType.WashClothes);
        Add(GoalType.DryClothes, InteractionType.Hang);
        Add(GoalType.CoolOff);
        Add(GoalType.WarmUp, InteractionType.Observe);

        // ── Общение и забота ─────────────────────────────────────────────
        Add(GoalType.Socialize);
        Add(GoalType.Aid);
        Add(GoalType.TreatWounds);
        Add(GoalType.Mourn, InteractionType.Observe);
        // §28.15C v3: похорон больше нет. Тело остаётся лежать там, где упало,
        // до конца игры — некому и незачем закапывать. Ординал остаётся занят:
        // сейв хранит цель числом.
        Add(GoalType.Bury, dead: true);

        // ── Добыча ───────────────────────────────────────────────────────
        Add(GoalType.GatherTools, InteractionType.PickUp);
        Add(GoalType.GatherWood, InteractionType.PickUp);
        Add(GoalType.GatherLeaves, InteractionType.PickUp);
        Add(GoalType.GatherStone, InteractionType.PickUp);
        Add(GoalType.GatherHerb, InteractionType.PickUp);
        Add(GoalType.GatherFiber, InteractionType.PickUp);
        Add(GoalType.HarvestTree, InteractionType.Harvest);
        Add(GoalType.HarvestYucca, InteractionType.Harvest);
        Add(GoalType.MineBoulder, InteractionType.Harvest);
        Add(GoalType.SplitLog, InteractionType.Process);
        Add(GoalType.ChopCrown, InteractionType.Process);
        Add(GoalType.Butcher, InteractionType.Butcher);

        // ── Огонь и стройка ──────────────────────────────────────────────
        Add(GoalType.TendFire, InteractionType.Fuel);
        Add(GoalType.HaulToFire, InteractionType.Observe);
        Add(GoalType.Build, InteractionType.Build);
        Add(GoalType.BuildFurniture, InteractionType.Build);
        Add(GoalType.BuildRaft, InteractionType.BuildRaft);

        // ── Крафт ────────────────────────────────────────────────────────
        Add(GoalType.CraftSpear, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Spear },
            craftTrace: "CraftedSpear", craftNeedsHands: true);
        Add(GoalType.CraftAxe, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.AxeStone },
            craftTrace: "CraftedAxe", craftNeedsHands: true);
        Add(GoalType.CraftPickaxe, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.PickaxeStone },
            craftTrace: "CraftedPickaxe", craftNeedsHands: true);
        Add(GoalType.CraftKnife, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Knife },
            craftTrace: "CraftedKnife", craftNeedsHands: true);
        Add(GoalType.CraftBow, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Bow },
            craftTrace: "CraftedBow", craftNeedsHands: true);
        Add(GoalType.CraftArrows, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Arrow, ContentIds.Arrow, ContentIds.Arrow },
            craftTrace: "CraftedArrows", craftNeedsHands: true);
        Add(GoalType.CraftRope, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Rope },
            craftTrace: "CraftedRope", craftNeedsHands: true);
        Add(GoalType.CraftCloth, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Cloth },
            craftTrace: "CraftedCloth", craftNeedsHands: true);
        Add(GoalType.CraftBandage, InteractionType.Craft);
        Add(GoalType.CraftSplint, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.Splint },
            craftTrace: "CraftedSplint", craftNeedsHands: true);
        Add(GoalType.CraftWoodenArm, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.WoodenArm },
            craftTrace: "CraftedWoodenArm", craftNeedsHands: true);
        Add(GoalType.CraftWoodenLeg, InteractionType.Craft,
            craftOutputs: new[] { ContentIds.WoodenLeg },
            craftTrace: "CraftedWoodenLeg", craftNeedsHands: true);
        Add(GoalType.CraftLeather, InteractionType.Craft);
        Add(GoalType.CookMeat, InteractionType.Craft);
        Add(GoalType.CraftBed, InteractionType.Craft);
        Add(GoalType.CraftTent, InteractionType.Craft, craftNeedsHands: true);
        Add(GoalType.CraftRack, InteractionType.Craft, craftNeedsHands: true, dead: true);

        // ── Охота и бой ──────────────────────────────────────────────────
        // Hunt бежит: краб отпрыгивает на узел каждый Medium-тик, шагом
        // (Stroll) погоня вырождается в вечный пинг-понг «шаг к — шаг от».
        Add(GoalType.Hunt, urgency: UrgencyClass.Hurry, readiesMeleeWeapon: true);
        Add(GoalType.Prey, readiesMeleeWeapon: true);
        Add(GoalType.Flee, urgency: UrgencyClass.Flee,
            ignoresHostileRings: true, reactive: true);
        Add(GoalType.Defend, urgency: UrgencyClass.Hurry,
            ignoresHostileRings: true, reactive: true, readiesMeleeWeapon: true);
        Add(GoalType.Raid, urgency: UrgencyClass.Hurry,
            ignoresHostileRings: true, reactive: true, readiesMeleeWeapon: true);
        Add(GoalType.Abuse, urgency: UrgencyClass.Hurry, reactive: true);
        // §108: сговор раздаёт её сразу троим, минуя аукцион. Кольца чужака
        // игнорирует по той же причине, что и налёт: обходить того, к кому
        // идёшь, — бессмыслица.
        Add(GoalType.GroupHunt, urgency: UrgencyClass.Hurry,
            ignoresHostileRings: true, reactive: true, readiesMeleeWeapon: true);
        Add(GoalType.Expel, urgency: UrgencyClass.Hurry,
            ignoresHostileRings: true, reactive: true, readiesMeleeWeapon: true);
        Add(GoalType.Rescue, urgency: UrgencyClass.Hurry, reactive: true);
        // These two append-only GoalType ordinals reserve the names used by
        // the matching plan steps/interactions. Rescue owns the actual goal;
        // treating the steps as independent reactive goals would advertise
        // behaviours that no system can ever assign.
        Add(GoalType.PickUpPerson, dead: true);
        Add(GoalType.PutInBed, dead: true);
        Add(GoalType.Splint, urgency: UrgencyClass.Hurry, reactive: true);
        Add(GoalType.FitProsthetic, urgency: UrgencyClass.Hurry, reactive: true);

        // §28.15F: обобрать тело. Взаимодействие снимает ОДНУ вещь, поэтому
        // раздеть покойную целиком — это несколько отдельных походов, а не один
        // такт; так же, как никто не уносит поленницу одной ходкой.
        Add(GoalType.LootCorpse, InteractionType.Loot);

        // §111: обыскать беспомощного врага. Колонка взаимодействия НАМЕРЕННО
        // пуста, как у Abuse: жертва — не объект мира, общий путь планировщика
        // (перебор Perception.Objects) ей не годится, и взаимодействием владеет
        // сама сцена — она кладёт InteractionType.Loot в шаг плана. Hurry:
        // обмороки коротки, шагом он не успеет.
        Add(GoalType.LootHelpless, urgency: UrgencyClass.Hurry);

        // ── Мёртвые ординалы (§52: заявка на мебель стала стадийной) ─────
        Add(GoalType.PlaceSite, dead: true);
        Add(GoalType.DeliverToSite, dead: true);

        var size = 0;
        foreach (GoalType goal in Enum.GetValues(typeof(GoalType)))
        {
            if ((int)goal + 1 > size)
            {
                size = (int)goal + 1;
            }
        }

        ByOrdinal = new GoalDescriptor[size];
        foreach (var row in rows)
        {
            ByOrdinal[(int)row.Goal] = row;
        }
    }
}

}
