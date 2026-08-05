using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// ⭐ §111: ОДНО место, где решается «можно ли обыскать это тело и что снять
/// следующим».
///
/// <para>
/// Ниша между двумя работающими механиками, пустовавшая нарочно с обеих сторон:
/// §81 отсеивает беспомощную ЕЩЁ ДО скоринга (сцена требует, чтобы жертва была
/// в сознании), §28.15F умеет только мёртвых. Между ними — живая, но
/// выключенная, с ножом в кармане. Здесь она наконец кому-то интересна.
/// </para>
/// <para>
/// Как и <see cref="CorpseMath"/>, класс существует ради одного инварианта:
/// доступность в аукционе и валидность цели в плане спрашивают ОДНУ функцию
/// (<see cref="IsLootableBy"/>). Разойдись они — цель выигрывает, план падает,
/// и так каждый проход, пока NPC не умрёт от настоящей нужды (см. историю
/// HasUsableCoconut, Jul 2026).
/// </para>
/// </summary>
public static class LootHelplessMath
{
    /// <summary>
    /// Можно ли <paramref name="looter"/> обыскать <paramref name="victim"/>.
    /// ⭐ Единственное определение — его зовут аукцион, планировщик и сцена.
    /// </summary>
    public static bool IsLootableBy(WorldState world, NPCState looter, NPCState victim)
    {
        if (victim is null || looter is null ||
            victim.Id.Equals(looter.Id) ||
            victim.Health <= 0f ||
            // ⭐ Строго IsUnconscious: кома §60, умирание §105, обморок §40.13 —
            // ровно те три состояния, в которых тело точно не ответит. Спящая и
            // плачущая §110 в сознании, и тихий грабёж спящей — кража §40.5.
            !victim.IsUnconscious(world.Tick) ||
            !FactionRelations.AreHostile(looter.Faction, victim.Faction) ||
            !HasLoot(victim))
        {
            return false;
        }

        // §81.12: режем ЗНАНИЕ, а не только дорогу — иначе он чует лежащее тело
        // через полострова и идёт к нему мимо своих дел.
        if (HexSpatialMath.HexDistance(looter.Tile, victim.Tile) >
            Spec111.LootHelplessSightRadiusTiles)
        {
            return false;
        }

        // Занята чужим обыском — не садимся вдвоём на одно тело.
        if (victim.Mind.PendingLootedBy is { } claimed && !claimed.Equals(looter.Id))
        {
            return false;
        }

        // §106: вода — санктуарий. Она туда доплыла бы сама, а вот встать над
        // ней в воде нельзя (CanStrike), и планировать такую сцену незачем.
        if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, victim))
        {
            return false;
        }

        if (looter.CurrentJunction is not { } from ||
            victim.CurrentJunction is not { } to)
        {
            return false;
        }

        if (!from.Equals(to) && !Connectivity.Reachable(world, from, to, looter.Body.CanJump))
        {
            return false;
        }

        // Влезет ли ХОТЬ ПЕРВАЯ вещь. Без этого он доходит до тела и
        // разворачивается ни с чем — каждый проход заново (урок HasSpace в §28.15F).
        var spoil = NextSpoil(world, victim);
        return spoil is not null &&
            InventoryMath.CanMakeRoomFor(world, looter, spoil.DefinitionId);
    }

    /// <summary>
    /// Кого обыскивать. Перебор по РОСТЕРУ, а не по <c>Perception.Agents</c>:
    /// §72 развёл списки, и у чужака девушки лежат в <c>Hostiles</c>, а
    /// <c>PerceivedAgent</c> не носит содержимого рюкзака. Ближайшее тело;
    /// ничью разрывает меньший id, иначе порядок обхода словаря протёк бы в реплей.
    /// </summary>
    public static NPCState BestMark(WorldState world, NPCState looter)
    {
        NPCState best = null;
        var bestDistance = int.MaxValue;

        foreach (var victim in world.Entities.Npcs.Values)
        {
            if (!IsLootableBy(world, looter, victim))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(looter.Tile, victim.Tile);
            if (distance < bestDistance ||
                (distance == bestDistance && best is not null && victim.Id.Value < best.Id.Value))
            {
                bestDistance = distance;
                best = victim;
            }
        }

        return best;
    }

    /// <summary>
    /// Осталось ли что снять. Только карманы: подсумок §52.8 живёт в том же
    /// списке (<c>HolsterSlotIds</c> — производный набор id, а не второе
    /// хранилище), а надетое не трогаем — раздевание тел это §28.15F.
    /// </summary>
    public static bool HasLoot(NPCState victim) =>
        victim is not null && victim.Inventory.Items.Count > 0;

    /// <summary>
    /// Что снимут следующим: сперва оружие по убыванию боевого приоритета,
    /// затем инструменты, затем всё прочее.
    ///
    /// <para>
    /// Оружие идёт первым не ради выгоды, а ради разоружения: берётся ВСЁ, даже
    /// заведомо хуже своего. «А мне-то оно нужно?» — вопрос ставки
    /// (<see cref="HasBetterWeapon"/>), и задаётся он один раз, выше.
    /// </para>
    /// </summary>
    public static ItemInstance NextSpoil(WorldState world, NPCState victim)
    {
        var index = NextSpoilIndex(world, victim);
        return index < 0 ? null : victim.Inventory.Items[index];
    }

    /// <summary>Индекс той же вещи. Вещь снимается ПО ИНДЕКСУ, потому что
    /// равенство <see cref="ItemInstance"/> — по <c>DefinitionId</c>: удаление
    /// «по значению» сняло бы первую одноимённую и потеряло бы заряды и износ
    /// той, что выбрали (ловушка, описанная в AbuseMath).</summary>
    internal static int NextSpoilIndex(WorldState world, NPCState victim)
    {
        if (!HasLoot(victim))
        {
            return -1;
        }

        var items = victim.Inventory.Items;
        var bestWeapon = -1;
        var bestPriority = 0;
        var firstTool = -1;

        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i].DefinitionId;
            var priority = GearCatalog.For(id).MeleePriority;
            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestWeapon = i;
                continue;
            }

            if (firstTool < 0 && priority == 0 && CategoryOf(world, id) == ItemCategory.Tool)
            {
                firstTool = i;
            }
        }

        if (bestWeapon >= 0)
        {
            return bestWeapon;
        }

        return firstTool >= 0 ? firstTool : 0;
    }

    /// <summary>
    /// Снять следующую вещь. Экземпляр ПЕРЕЕЗЖАЕТ целиком — с влажностью,
    /// износом и остатком воды в скорлупе.
    /// </summary>
    public static bool TryTake(WorldState world, NPCState looter, NPCState victim, out string takenId)
    {
        takenId = null;
        var index = NextSpoilIndex(world, victim);
        if (index < 0)
        {
            return false;
        }

        var spoil = victim.Inventory.Items[index];
        if (!InventoryMath.MakeRoomFor(world, looter, spoil.DefinitionId))
        {
            return false;
        }

        victim.Inventory.Items.RemoveAt(index);
        looter.Inventory.Items.Add(spoil);
        takenId = spoil.DefinitionId;
        return true;
    }

    /// <summary>
    /// Есть ли у лежащего оружие МОЩНЕЕ его собственного — прибавка к ставке.
    /// Это и есть «защита» в мотиве: мачете §79 не крафтится, и до §111 оно
    /// попадало в колонию только с трупа.
    /// </summary>
    public static bool HasBetterWeapon(NPCState looter, NPCState victim)
    {
        if (victim is null)
        {
            return false;
        }

        foreach (var item in victim.Inventory.Items)
        {
            if (GearCatalog.For(item.DefinitionId).MeleePriority > 0 &&
                GearCatalog.AddsValueOver(looter.Inventory.Items, item.DefinitionId,
                    looter.Body.IntactHands))
            {
                return true;
            }
        }

        return false;
    }

    private static ItemCategory CategoryOf(WorldState world, string definitionId) =>
        world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def)
            ? ItemCatalog.Classify(def)
            : ItemCatalog.Resolve(definitionId).Category;
}

}
