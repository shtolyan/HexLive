using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// ⭐ §28.15F: ОДНО место, где решается «что ещё можно забрать с тела или из мешка».
///
/// <para>
/// Смерть больше не вываливает гардероб под ноги — вещи остаются на покойной, и
/// за ними надо прийти. Значит появляется пара «доступность в аукционе» и
/// «что реально забрать при исполнении», а это ровно тот стык, на котором
/// колония уже один раз залипала насмерть: доступность говорила «есть», план
/// отвечал «нечего», цель выигрывала снова — и так до смерти от той самой
/// нужды (см. HasUsableCoconut, Jul 2026). Поэтому обе стороны спрашивают
/// ЭТИ функции, а не каждая свою копию условия.
/// </para>
/// <para>
/// Порядок «сначала карманы, потом одежда» не косметика. Ёмкость карманов
/// покойной даётся её же одеждой: сними куртку раньше, чем вынешь из неё нож, —
/// и на теле останется вещь, которую хранить уже негде. Мародёр и в жизни
/// начинает с карманов.
/// </para>
/// </summary>
public static class CorpseMath
{
    public enum SpoilSource
    {
        None,
        Pockets,
        Worn,
        Bag
    }

    /// <summary>Тело или останки с мешком: оба якоря можно оплакать и обыскать.</summary>
    public static bool IsHumanDead(ObjectDefinition definition) =>
        definition is not null &&
        (definition.Tags.Contains(ObjectTags.Corpse) ||
         definition.Tags.Contains(ObjectTags.Remains));

    /// <summary>Тело, на которое указывает объект-якорь <c>corpse.npc</c>, или
    /// null, если это не труп человека (звериная туша) либо тело уже забрали
    /// (разделали).</summary>
    public static NPCState BodyOf(WorldState world, WorldObjectState anchor)
    {
        if (anchor?.CurrentUser is not { } deadId)
        {
            return null;
        }

        return world.Entities.Corpses.TryGetValue(deadId, out var body) ? body : null;
    }

    /// <summary>Осталось ли на теле хоть что-нибудь.</summary>
    public static bool HasSpoils(NPCState body) =>
        body is not null && (body.Inventory.Items.Count > 0 || body.WornItems.Count > 0);

    /// <summary>Добыча есть либо на свежем теле, либо в единственном мешке у скелета.</summary>
    public static bool HasSpoils(WorldState world, WorldObjectState anchor)
    {
        var body = BodyOf(world, anchor);
        return body is not null
            ? HasSpoils(body)
            : anchor is not null &&
              anchor.DefinitionId == ContentIds.HumanRemains &&
              anchor.Contents.Count > 0;
    }

    /// <summary>
    /// Следующая вещь, которую снимут: сперва из карманов, затем с тела.
    /// <paramref name="fromPockets"/> говорит, из какого списка её потом
    /// удалять — вызывающему не нужно повторять правило порядка.
    /// </summary>
    public static ItemInstance NextSpoil(NPCState body, out bool fromPockets)
    {
        fromPockets = false;
        if (body is null)
        {
            return null;
        }

        if (body.Inventory.Items.Count > 0)
        {
            fromPockets = true;
            return body.Inventory.Items[0];
        }

        return body.WornItems.Count > 0 ? body.WornItems[0] : null;
    }

    /// <summary>Снять вещь с тела. Возвращает false, если её там уже нет.</summary>
    public static bool TakeSpoil(NPCState body, ItemInstance item, bool fromPockets) =>
        fromPockets ? body.Inventory.Items.Remove(item) : body.WornItems.Remove(item);

    public static ItemInstance NextSpoil(
        WorldState world, WorldObjectState anchor, out SpoilSource source)
    {
        source = SpoilSource.None;
        var body = BodyOf(world, anchor);
        if (body is not null)
        {
            var item = NextSpoil(body, out var fromPockets);
            if (item is not null)
            {
                source = fromPockets ? SpoilSource.Pockets : SpoilSource.Worn;
            }

            return item;
        }

        if (anchor is not null &&
            anchor.DefinitionId == ContentIds.HumanRemains &&
            anchor.Contents.Count > 0)
        {
            source = SpoilSource.Bag;
            return anchor.Contents[0];
        }

        return null;
    }

    public static bool TakeSpoil(
        WorldState world, WorldObjectState anchor, ItemInstance item, SpoilSource source)
    {
        var body = BodyOf(world, anchor);
        return source switch
        {
            SpoilSource.Pockets => body is not null && body.Inventory.Items.Remove(item),
            SpoilSource.Worn => body is not null && body.WornItems.Remove(item),
            SpoilSource.Bag => anchor is not null && anchor.Contents.Remove(item),
            _ => false
        };
    }

    /// <summary>
    /// Есть ли в поле зрения тело, с которого ещё есть что снять. Спрашивается
    /// аукционом; ровно то же условие проверяет план при выборе цели.
    /// </summary>
    public static bool HasLootableCorpse(NPCState npc, WorldState world)
    {
        foreach (var perceived in npc.Perception.Objects)
        {
            if (!perceived.IsReachable ||
                npc.Memory.IsShunned(perceived.Id, world.Tick) ||
                !world.Entities.Objects.TryGetValue(perceived.Id, out var anchor) ||
                !world.Content.ObjectDefinitions.TryGetValue(anchor.DefinitionId, out var definition) ||
                !IsHumanDead(definition))
            {
                continue;
            }

            if (HasSpoils(world, anchor))
            {
                return true;
            }
        }

        return false;
    }
}

}
