using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

/// <summary>§153.2: насколько подарок понравился получательнице.</summary>
public enum GiftReaction
{
    Disliked,
    Neutral,
    Liked,
    Loved
}

/// <summary>
/// §153.2: приговор по одному подарку — счёт, ступень реакции и сдвиг симпатии.
/// <para>
/// Отдельная структура, а не голое число: и симуляция (сдвиг отношений), и
/// вид (пузырь над головой, строка в истории) читают ОДНУ оценку. Строка
/// <see cref="Driver"/> называет, ЧТО решило: без неё «не понравилось» — это
/// приговор без объяснения, и игроку нечему учиться.
/// </para>
/// </summary>
public readonly struct GiftVerdict
{
    public GiftVerdict(
        float score, GiftReaction reaction, float affinityDelta, string driver)
    {
        Score = score;
        Reaction = reaction;
        AffinityDelta = affinityDelta;
        Driver = driver;
    }

    /// <summary>−1..+1: слева «зачем ты мне это дала», справа «то, что надо».</summary>
    public float Score { get; }

    public GiftReaction Reaction { get; }

    /// <summary>Сдвиг симпатии получательницы К ДАРИТЕЛЬНИЦЕ.</summary>
    public float AffinityDelta { get; }

    /// <summary>Что перевесило: <c>Need</c>, <c>Taste</c> или <c>Value</c>.</summary>
    public string Driver { get; }
}

/// <summary>
/// §153.2: базовые правила оценки подарка — тип/ценность, нужда, вкус, объём.
/// <para>
/// Оценка ДЕТЕРМИНИРОВАНА и не бросает кубик: подарок — это ход игрока, и он
/// обязан читаться. Один и тот же предмет одной и той же девушке в одном и том
/// же состоянии всегда стоит одинаково; меняется он от того, что изменилось в
/// мире — она проголодалась, замёрзла, осталась без оружия.
/// </para>
/// <para>
/// Ни одного нового id и ни одной новой ручки баланса: ценность берётся из
/// <see cref="ItemCatalog.ImportanceById"/> (она уже ранжирует воду выше еды, а
/// еду выше камня по тегам, а не по списку имён), вкус — из §75
/// <see cref="ItemAffinity"/>, а шаг симпатии выражен в уже экспортируемой
/// ручке <see cref="SocialBalance.TalkRelationshipGain"/>. Своя ручка означала
/// бы новый класс в <c>BalanceReflection</c>, переэкспорт <c>simdata.json</c> из
/// Unity и красный <c>BalanceParityGate</c> у всех, кто этого не сделал.
/// </para>
/// </summary>
public static class GiftAppraisal
{
    // Веса слагаемых. Нужда весит больше вкуса и ценности вместе взятых по
    // замыслу: в колонии на грани голода фляга воды жаждущей — это поступок, а
    // не безделушка, и наоборот — мачете сытой и вооружённой лишь приятно.
    private const float NeedWeight = 0.55f;
    private const float ValueWeight = 0.45f;
    private const float TasteWeight = 0.30f;
    private const float QuantityWeight = 0.10f;

    // Пороги ступеней по счёту.
    private const float LovedScore = 0.55f;
    private const float LikedScore = 0.20f;
    private const float DislikedScore = -0.15f;

    // Сдвиг симпатии в единицах TalkRelationshipGain (0.02 = одна беседа).
    // Подарок «в самое сердце» стоит примерно шести бесед, а мусор отнимает
    // две: дарить наугад должно быть чуть-чуть страшно, иначе подарок
    // перестаёт быть выбором.
    private const float LovedTalks = 6f;
    private const float LikedTalks = 3f;
    private const float NeutralTalks = 1f;
    private const float DislikedTalks = -2f;

    // Категория без своей нужды (инструмент, ресурс, мелочь) всё равно чего-то
    // стоит — но её цену целиком назначают ценность и вкус.
    private const float ArmedNeed = 0.20f;

    public static GiftVerdict Evaluate(
        NPCState receiver, string definitionId, int count, float resourceAmount)
    {
        if (receiver is null || string.IsNullOrEmpty(definitionId))
        {
            return new GiftVerdict(0f, GiftReaction.Neutral, 0f, "None");
        }

        var category = CategoryOf(definitionId, resourceAmount);
        var value = ValueOf(definitionId, category);
        var need = NeedOf(receiver, category);
        var taste = (ItemAffinity.For(receiver.Id.Value, definitionId) - 0.5f) * 2f;
        var quantity = MathUtil.Clamp01((count - 1) / 4f);

        var needTerm = NeedWeight * need;
        var valueTerm = ValueWeight * value;
        var tasteTerm = TasteWeight * taste;
        var score = MathUtil.Clamp(
            needTerm + valueTerm + tasteTerm + QuantityWeight * quantity, -1f, 1f);

        var reaction = score >= LovedScore
            ? GiftReaction.Loved
            : score >= LikedScore
                ? GiftReaction.Liked
                : score > DislikedScore
                    ? GiftReaction.Neutral
                    : GiftReaction.Disliked;

        return new GiftVerdict(
            score, reaction, AffinityDelta(reaction), Driver(needTerm, valueTerm, tasteTerm));
    }

    /// <summary>Сдвиг симпатии одной ступени — в единицах беседы (§28.15C).</summary>
    public static float AffinityDelta(GiftReaction reaction) =>
        SocialBalance.TalkRelationshipGain * reaction switch
        {
            GiftReaction.Loved => LovedTalks,
            GiftReaction.Liked => LikedTalks,
            GiftReaction.Disliked => DislikedTalks,
            _ => NeutralTalks
        };

    /// <summary>
    /// Пустая пробитая скорлупа — не вода, а мусор (та же ловушка §52, из-за
    /// которой рюкзаки заклинивало). Всё остальное классифицирует каталог по
    /// ТЕГАМ, поэтому новый предмет оценивается в день своего добавления.
    /// </summary>
    private static ItemCategory CategoryOf(string definitionId, float resourceAmount)
    {
        if (ItemCatalog.IsDrainedWaterShell(definitionId, resourceAmount))
        {
            return ItemCategory.Misc;
        }

        return ItemCatalog.IsWaterSourceId(definitionId)
            ? ItemCategory.Water
            : ItemCatalog.Resolve(definitionId).Category;
    }

    // Importance уже ранжирует вещи по выживанию: вода 100, еда 95, оружие 93,
    // лекарство 80, инструмент 60, броня 45, одежда 40, ресурс 20, мелочь 10.
    // Ноль ставим на одежде (40): подарить рубаху — вежливо и не более того.
    private static float ValueOf(string definitionId, ItemCategory category)
    {
        var importance = category == ItemCategory.Misc
            ? ItemCatalog.Importance(ItemCategory.Misc)
            : ItemCatalog.ImportanceById(definitionId);
        return MathUtil.Clamp((importance - 40f) / 60f, -1f, 1f);
    }

    private static float NeedOf(NPCState receiver, ItemCategory category) => category switch
    {
        ItemCategory.Food => MathUtil.Clamp01(receiver.Needs.Hunger),
        ItemCategory.Water => MathUtil.Clamp01(receiver.Needs.Thirst),
        ItemCategory.Medicine => MathUtil.Clamp01(1f - receiver.Health),
        ItemCategory.Clothing or ItemCategory.Armor =>
            MathUtil.Clamp01(receiver.Needs.ThermalDiscomfort),
        ItemCategory.Weapon => IsArmed(receiver) ? ArmedNeed : 1f,
        _ => 0f
    };

    /// <summary>Есть ли у неё уже чем отбиться — тот же признак, что у §75A.</summary>
    private static bool IsArmed(NPCState receiver) =>
        CarriesWeapon(receiver.Inventory.Items) || CarriesWeapon(receiver.WornItems);

    private static bool CarriesWeapon(IReadOnlyList<ItemInstance> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (GearCatalog.For(items[i].DefinitionId).MeleePriority > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Driver(float needTerm, float valueTerm, float tasteTerm)
    {
        var need = needTerm < 0f ? -needTerm : needTerm;
        var value = valueTerm < 0f ? -valueTerm : valueTerm;
        var taste = tasteTerm < 0f ? -tasteTerm : tasteTerm;
        if (need >= value && need >= taste)
        {
            return "Need";
        }

        return value >= taste ? "Value" : "Taste";
    }
}

}
