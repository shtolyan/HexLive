using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.UnityPresentation.Content;

namespace HexLive.UnityPresentation.Wearing
{

// ⭐ ЕДИНСТВЕННОЕ место, которое отвечает на вопрос «что нужно показать ЭТОМУ
// миру прямо сейчас» — и единственное, куда добавляется новая семья контента.
//
// Зачем один вход вместо трёх вызовов из экрана загрузки. Список семей —
// движущаяся мишень: сегодня это одежда, причёски и протезы, завтра появится
// что-то ещё, и забыть дописать его в загрузчик легко, а заметить трудно:
// пропуск не ломается, он лишь возвращает вещь на ЛЕНИВЫЙ путь, то есть на
// блокирующее чтение с диска в первом же кадре игры. Такую пропажу видно не в
// логе, а как «игра дёрнулась, когда я открыл рюкзак».
//
// ⭐ ПРАВИЛО: новая семья атомарного контента, зависящего от состояния мира,
// добавляет сюда свой проход. Контент, который от мира НЕ зависит (иконки —
// часть bundle владельца) никогда не греется целиком: иначе экран загрузки
// скачал бы весь реестр.
//
// ⚠️ Читает Entities.* — значит, только с главного потока и только когда мир не
// мотается воркером (§41.3). Вызывается ДВАЖДЫ: до намотки (чтобы загрузка шла
// рядом с ней) и после (мир за игровые сутки успевает переодеться, отрастить
// новых колонисток и потерять конечности). Повторный проход дешёвый — каждая
// дверь молчит на том, что уже в кэше или уже едет.
public static class ScenePrewarm
{
    private static readonly string[] ItemOwnerTypes =
        { "wear", "object", "building", "mob" };

    // ForSnapshot повторяется на КАЖДЫЙ живой снапшот (4 Гц) по каждому
    // объекту мира. LINQ Any по GarmentLibrary.Active здесь был линейным
    // сканом 685 вещей с замыканием на вызов: на 9213 объектах большого
    // острова это ~6.3 млн вызовов делегата за тик — замеренные ~460 мс
    // кадра (3 fps у prod-зрителя, 2026-08-27). Один HashSet отвечает за O(1).
    private static HashSet<string> _catalogGarmentIds;
    private static object _catalogGarmentSource;
    private static int _catalogGarmentCount = -1;

    private static bool IsCatalogGarment(string definitionId)
    {
        var active = HexLive.Simulation.Content.GarmentLibrary.Active;
        if (_catalogGarmentIds == null ||
            !ReferenceEquals(_catalogGarmentSource, active) ||
            _catalogGarmentCount != active.Count)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var garment in active)
            {
                if (garment != null)
                {
                    ids.Add(garment.Id);
                }
            }
            _catalogGarmentIds = ids;
            _catalogGarmentSource = active;
            _catalogGarmentCount = active.Count;
        }

        return _catalogGarmentIds.Contains(definitionId);
    }

    public static void ForWorld(WorldState world)
    {
        if (world == null)
        {
            return;
        }

        ResolveWorkingSet(world);
        WarmItems(world);
        WarmWear(world);
        WarmActors(world);
        WarmHair(world);
        WarmProsthetics(world);
        WarmObjects(world);
        WarmMobs(world);
    }

    /// <summary>
    /// The presentation-side equivalent of <see cref="ForWorld"/>. Remote
    /// clients own no <see cref="WorldState"/>, so the live snapshot is their
    /// authoritative current-world working set. Repeating this on every new
    /// snapshot is intentional and cheap: family caches suppress old ids while
    /// a newly crafted, looted or spawned owner immediately opens one bundle
    /// for both its <c>main</c> and <c>icon</c> entries.
    /// </summary>
    public static void ForSnapshot(WorldSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        WarmSnapshotNpcs(snapshot.Npcs);
        WarmSnapshotNpcs(snapshot.Corpses);

        foreach (var worldObject in snapshot.Objects)
        {
            if (TryWorldObjectContentKey(
                    worldObject.DefinitionId, worldObject.Id.Value,
                    out var type, out var contentId))
            {
                WarmOwnerMain(type, contentId);
            }

            foreach (var slot in worldObject.Contents)
            {
                WarmItemOwner(slot.ItemDefinitionId);
            }
            foreach (var ingredientId in worldObject.CraftIngredients)
            {
                WarmItemOwner(ingredientId);
            }
            if (worldObject.RoastingRaw > 0)
            {
                WarmItemOwner("food.meat_raw");
            }
            if (worldObject.RoastingCooked > 0)
            {
                WarmItemOwner("food.meat_cooked");
            }
        }

        foreach (var mob in snapshot.Mobs)
        {
            // A retired mob id (an old server's legacy save) owns no bundle;
            // asking for it would log a false missing-record error.
            if (!Config.MobLibrary.IsRetired(mob.MobId))
            {
                WarmOwnerMain("mob", mob.MobId);
            }
        }
    }

    private static void WarmSnapshotNpcs(IEnumerable<NpcSnapshot> npcs)
    {
        foreach (var npc in npcs)
        {
            WarmOwnerMain("actor", npc.ActorMesh);
            HairContent.Prewarm(npc.Hairstyle);
            WarmActorPaintMaps(npc.ActorMesh);

            foreach (var id in npc.WornItems)
            {
                WarmItemOwner(id);
            }
            foreach (var id in npc.InventoryItems)
            {
                WarmItemOwner(id);
            }
            foreach (var id in npc.HolsteredItems)
            {
                WarmItemOwner(id);
            }
            WarmItemOwner(npc.HeldItemId);
            WarmItemOwner(npc.HeldGarmentId);
            WarmItemOwner(npc.FavoriteWeaponId);

            foreach (var container in npc.InventoryContainers)
            {
                WarmItemOwner(container.OwnerItemDefinitionId);
                foreach (var slot in container.Slots)
                {
                    WarmItemOwner(slot.ItemDefinitionId);
                }
            }

            foreach (var condition in npc.BodyPartConditions)
            {
                if (condition.Prosthetic is { } prosthetic)
                {
                    ProstheticContent.Load(
                        prosthetic.Part,
                        prosthetic.DefinitionId,
                        prosthetic.Mechanical,
                        null);
                }
            }
        }
    }

    /// <summary>Карты покраски кожи этой актрисы. AtomicResources.Load ленивый:
    /// первый же синхронный запрос (первая рана/пот/грязь В ИГРЕ) возвращает
    /// null и роняет покраску в runtime-бейк — замеренный спайк ~170 мс на
    /// тике. Прогретые за шторкой, они превращают тот же момент в чтение
    /// готовой карты.</summary>
    private static void WarmActorPaintMaps(string actorMesh)
    {
        if (string.IsNullOrEmpty(actorMesh))
        {
            return;
        }

        AtomicResources.Prewarm("HexLive/PaintMaps/skin_" + actorMesh);
        AtomicResources.Prewarm("HexLive/PaintMaps/skinpos_" + actorMesh);
    }

    private static void WarmItemOwner(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        // A crafted garment borrowing another garment's bundle must warm as
        // wear — the registry probe below would misroute it to object/<id>.
        if (WearArtAliases.IsAliased(id))
        {
            WarmOwnerMain("wear", id);
            return;
        }

        var service = ContentAssetService.Instance;
        foreach (var type in ItemOwnerTypes)
        {
            if (!service.TryGetRecord(type, id, out _))
            {
                continue;
            }

            WarmOwnerMain(type, id);
            return;
        }

        // The local pre-wind pass can run before the registry callback. Infer
        // only stable simulation families here; LoadMain itself waits for the
        // registry and a later live-snapshot pass confirms the exact type.
        if (IsCatalogGarment(id))
        {
            WarmOwnerMain("wear", id);
        }
        else if (id.StartsWith("building.", System.StringComparison.Ordinal) ||
                 id.StartsWith("architecture.", System.StringComparison.Ordinal))
        {
            WarmOwnerMain("building", id);
        }
        else
        {
            WarmOwnerMain("object", id);
        }
    }

    private static void WarmOwnerMain(string type, string id)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (type == "wear")
        {
            ActorWardrobe.PrewarmAsync(id);
            return;
        }

        ContentPrefabCache.Prewarm(type, id);
    }

    /// <summary>
    /// Owner payloads for everything the opening world can put in an inventory,
    /// on a body or inside a world container. Each actual item opens one bundle:
    /// its main prefab and icon are retained together behind the loading curtain.
    /// This is intentionally a working set, not the whole item registry.
    /// </summary>
    private static void WarmItems(WorldState world)
    {
        var ids = new HashSet<string>();
        void Add(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var item in npc.WornItems)
            {
                Add(item.DefinitionId);
            }
            foreach (var item in npc.Inventory.Items)
            {
                Add(item.DefinitionId);
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            foreach (var item in corpse.WornItems)
            {
                Add(item.DefinitionId);
            }
        }

        foreach (var worldObject in world.Entities.Objects.Values)
        {
            foreach (var item in worldObject.Contents)
            {
                Add(item.DefinitionId);
            }
        }

        foreach (var id in ids)
        {
            WarmItemOwner(id);
        }
    }


    private static void ResolveWorkingSet(WorldState world)
    {
        var keys = new Dictionary<string, ContentObjectKey>();
        void Add(string type, string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                keys[type + "/" + id] = new ContentObjectKey { type = type, id = id };
            }
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            Add("actor", npc.ActorMesh);
            Add("hair", npc.Hairstyle);
            foreach (var garment in npc.WornItems)
            {
                Add("wear", garment.DefinitionId);
            }
            foreach (var part in npc.Body.Parts)
            {
                if (npc.Body.Condition(part.Key)?.Prosthetic is not { } prosthetic)
                {
                    continue;
                }
                var address = ProstheticContent.Address(
                    prosthetic.Part, prosthetic.DefinitionId, prosthetic.Mechanical);
                if (address.StartsWith("prosthetic/", System.StringComparison.Ordinal))
                {
                    Add("prosthetic", address[11..]);
                }
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            Add("actor", corpse.ActorMesh);
            Add("hair", corpse.Hairstyle);
            foreach (var garment in corpse.WornItems)
            {
                Add("wear", garment.DefinitionId);
            }
        }

        foreach (var value in world.Entities.Objects.Values)
        {
            if (TryWorldObjectContentKey(
                    value.DefinitionId, value.Id.Value, out var type, out var contentId))
            {
                Add(type, contentId);
            }
        }
        foreach (var mob in world.Mobs)
        {
            if (!Config.MobLibrary.IsRetired(mob.MobId))
            {
                Add("mob", mob.MobId);
            }
        }

        ContentAssetService.Instance.Resolve(keys.Values, missing =>
        {
            if (missing.Count > 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[AtomicContent] сервер не разрешил: " +
                    string.Join(", ", missing.Select(value => value.type + "/" + value.id)));
            }
        });
    }

    /// <summary>Одежда: надетая на живых и мёртвых плюс валяющаяся на земле.
    /// Спрашивать каталог целиком нельзя — это 712 бандлов и 1.99 ГБ.</summary>
    private static void WarmWear(WorldState world)
    {
        var ids = new HashSet<string>();

        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var item in npc.WornItems)
            {
                ids.Add(item.DefinitionId);
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            foreach (var item in corpse.WornItems)
            {
                ids.Add(item.DefinitionId);
            }
        }

        var garments = new HashSet<string>();
        foreach (var garment in HexLive.Simulation.Content.GarmentLibrary.Active)
        {
            garments.Add(garment.Id);
        }

        foreach (var worldObject in world.Entities.Objects.Values)
        {
            if (garments.Contains(worldObject.DefinitionId))
            {
                ids.Add(worldObject.DefinitionId);
            }
        }

        foreach (var id in ids)
        {
            GarmentDropFactory.Prewarm(id);
        }
    }

    /// <summary>Причёски всех, кто есть в мире, — новая колонистка (§46 гости
    /// рейда, приход населения) приезжает со своей. Цвет выводится из id и
    /// греется той же дверью.</summary>
    private static void WarmHair(WorldState world)
    {
        var styles = new HashSet<string>();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!string.IsNullOrEmpty(npc.Hairstyle))
            {
                styles.Add(npc.Hairstyle);
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            if (!string.IsNullOrEmpty(corpse.Hairstyle))
            {
                styles.Add(corpse.Hairstyle);
            }
        }

        foreach (var style in styles)
        {
            HairContent.Prewarm(style);
        }
    }

    private static void WarmActors(WorldState world)
    {
        var actors = new HashSet<string>();
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!string.IsNullOrEmpty(npc.ActorMesh))
            {
                actors.Add(npc.ActorMesh);
            }
        }

        foreach (var corpse in world.Entities.Corpses.Values)
        {
            if (!string.IsNullOrEmpty(corpse.ActorMesh))
            {
                actors.Add(corpse.ActorMesh);
            }
        }

        foreach (var actor in actors)
        {
            ContentPrefabCache.Prewarm("actor", actor);
            WarmActorPaintMaps(actor);
        }
    }

    private static void WarmObjects(WorldState world)
    {
        foreach (var worldObject in world.Entities.Objects.Values)
        {
            if (string.IsNullOrEmpty(worldObject.DefinitionId))
            {
                continue;
            }

            if (TryWorldObjectContentKey(
                    worldObject.DefinitionId, worldObject.Id.Value,
                    out var type, out var contentId))
            {
                ContentPrefabCache.Prewarm(type, contentId);
            }
        }
    }

    private static void WarmMobs(WorldState world)
    {
        var ids = new HashSet<string>();
        foreach (var mob in world.Mobs)
        {
            if (!string.IsNullOrEmpty(mob.MobId) &&
                !Config.MobLibrary.IsRetired(mob.MobId))
            {
                ids.Add(mob.MobId);
            }
        }

        foreach (var id in ids)
        {
            ContentPrefabCache.Prewarm("mob", id);
        }
    }

    /// <summary>Протезы: за игровые сутки офлайна колонистка может лишиться
    /// конечности (§50) и получить протез, которого в мире не было.</summary>
    private static void WarmProsthetics(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var part in npc.Body.Parts)
            {
                if (npc.Body.Condition(part.Key)?.Prosthetic is not { } prosthetic)
                {
                    continue;
                }

                ProstheticContent.Load(
                    prosthetic.Part,
                    prosthetic.DefinitionId,
                    prosthetic.Mechanical,
                    null);
            }
        }
    }

    private static bool TryWorldObjectContentKey(
        string definitionId, int objectId, out string type, out string contentId)
    {
        type = string.Empty;
        contentId = string.Empty;
        if (string.IsNullOrWhiteSpace(definitionId))
        {
            return false;
        }

        // A dropped garment is still the same atomic wear object as the fitted
        // garment. Asking for object/clothing.* produced a false missing-record
        // warning and left the real wear bundle on the lazy path.
        if (IsCatalogGarment(definitionId))
        {
            type = "wear";
            contentId = definitionId;
            return true;
        }

        // A crafted garment with no bundle of its own resolves through its
        // donor's wear record (WearArtAliases) — object/<id> does not exist.
        if (WearArtAliases.IsAliased(definitionId))
        {
            type = "wear";
            contentId = WearArtAliases.ArtId(definitionId);
            return true;
        }

        // Simulation uses one id per prosthetic kind, but presentation objects
        // are independently authored for left/right. Match the world-drop view's
        // deterministic side selection before resolve/prewarm.
        if (ProstheticContent.TryWorldDropObjectId(definitionId, objectId, out contentId))
        {
            type = "prosthetic";
            return true;
        }

        // These are simulation/presentation anchors assembled from already
        // loaded actors or procedural geometry. They deliberately own no
        // ContentObject and therefore must not be reported as missing bundles.
        if (IsPayloadFreeWorldAnchor(definitionId))
        {
            return false;
        }

        type = definitionId.StartsWith("building.", System.StringComparison.Ordinal) ||
               definitionId.StartsWith("architecture.", System.StringComparison.Ordinal)
            ? "building"
            : "object";
        contentId = definitionId;
        return true;
    }

    private static bool IsPayloadFreeWorldAnchor(string definitionId) =>
        definitionId.StartsWith("water.", System.StringComparison.Ordinal) ||
        definitionId is "corpse.npc" or "grave.npc" or "body.limb_severed" ||
        definitionId == HexLive.Simulation.Content.ContentIds.BuildSite ||
        definitionId == HexLive.Simulation.Content.ContentIds.HutPlan ||
        definitionId == HexLive.Simulation.Content.ContentIds.HumanRemains;
}

}
