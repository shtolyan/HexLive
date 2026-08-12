using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133: даст ли хозяйка поносить свою вещь. Решение детерминированное (без
/// броска кубика), чтобы трасса и повтор сейва сходились.
///
/// <para>
/// Логика ровно та, что просил игрок: «если и так трусы надеты и есть ещё —
/// почему не дать; а если это последние и она сама голая — не даст». Плюс два
/// очевидных «мне самой нужно»: холодно (вещь греет) и рядом опасность (вещь
/// защищает). Черт характера Щедрая/Жадная в игре нет, поэтому мягкий фактор —
/// отношение к просящей.
/// </para>
/// </summary>
public static class WearPermissionMath
{
    /// <summary>Порог согласия: нейтральная подруга + одна запасная = ровно да.</summary>
    public const float WillingnessThreshold = 0.6f;

    public const float WillingnessBase = 0.35f;
    public const float WillingnessPerSpare = 0.25f;
    public const int WillingnessSpareCap = 2;
    public const float WillingnessAffinityWeight = 0.4f;

    public enum Verdict
    {
        Grant,
        /// <summary>«Самой нужна» — отказ по делу, без обиды.</summary>
        RefuseNeeded,
        /// <summary>«Тебе — нет» — отказ личный, отношения от него портятся.</summary>
        RefuseDislike
    }

    /// <summary>
    /// Решение хозяйки. <paramref name="garment"/> — вещь, которую просят.
    /// </summary>
    public static Verdict Decide(
        WorldState world, NPCState owner, NPCState requester, WorldObjectState garment)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(garment.DefinitionId, out var def) ||
            def.Layer is not { } layer)
        {
            return Verdict.RefuseNeeded;
        }

        // Последнее, что прикрывает эту часть тела, не отдают: иначе хозяйка
        // сама останется голой — прямое требование игрока.
        var spares = int.MaxValue;
        foreach (var part in def.Covers)
        {
            var forPart = CountCovering(world, owner, layer, part, garment.Id);
            if (forPart < spares)
            {
                spares = forPart;
            }
        }

        if (def.Covers.Count == 0)
        {
            spares = CountCovering(world, owner, layer, null, garment.Id);
        }

        if (spares <= 0)
        {
            return Verdict.RefuseNeeded;
        }

        // Мне самой сейчас нужно: мёрзну (вещь реально согрела бы МЕНЯ) или
        // рядом опасность (вещь защищает). Обе причины «по делу», обиды не будет.
        //
        // ⭐ Проверять «вещь вообще греет» здесь нельзя: греет всё, даже лифчик
        // на 0.02, а «слегка прохладно» (0.6 при пороге 0.45) — штатное
        // состояние колонистки. С таким условием не одалживалось бы НИЧЕГО и
        // никогда. Спрашиваем то же, что спрашивает у себя одевающаяся: даст ли
        // эта вещь ей ПРИБАВКУ тепла сверх надетого (§52.7). Запасной лифчик
        // поверх надетого не даёт — значит, не жалко.
        var (_, armor) = EquipmentMath.ItemValues(world, garment.DefinitionId);
        if (owner.Needs.ThermalDiscomfort >= SimBalance.DressThermalThreshold &&
            EquipmentMath.WarmthGainFromWearing(world, owner, garment.DefinitionId) >=
                SimBalance.DressWarmthGainMin)
        {
            return Verdict.RefuseNeeded;
        }

        if (owner.Memory.Dangers.Count > 0 && armor > 0f)
        {
            return Verdict.RefuseNeeded;
        }

        var affinity = owner.Social.GetOrCreate(requester.Id).Affinity;
        var willingness = WillingnessBase +
            WillingnessPerSpare * System.Math.Min(spares, WillingnessSpareCap) +
            WillingnessAffinityWeight * affinity;

        return willingness >= WillingnessThreshold ? Verdict.Grant : Verdict.RefuseDislike;
    }

    /// <summary>
    /// Сколько ЕЩЁ вещей хозяйки того же слоя прикрывают эту часть тела —
    /// надетых, в карманах и лежащих на земле, кроме самой запрошенной.
    /// </summary>
    private static int CountCovering(
        WorldState world, NPCState owner, WearLayer layer, BodyPart? part, ObjectId excluded)
    {
        var count = 0;
        foreach (var worn in owner.WornItems)
        {
            if (Matches(world, worn.DefinitionId, layer, part))
            {
                count++;
            }
        }

        foreach (var carried in owner.Inventory.Items)
        {
            if (carried.OwnerId == owner.Id.Value &&
                Matches(world, carried.DefinitionId, layer, part))
            {
                count++;
            }
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!obj.Id.Equals(excluded) && obj.Owner == owner.Id &&
                Matches(world, obj.DefinitionId, layer, part))
            {
                count++;
            }
        }

        return count;
    }

    private static bool Matches(WorldState world, string definitionId, WearLayer layer, BodyPart? part)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def) ||
            def.Layer != layer)
        {
            return false;
        }

        return part is not { } needed || def.Covers.Contains(needed);
    }
}

}
