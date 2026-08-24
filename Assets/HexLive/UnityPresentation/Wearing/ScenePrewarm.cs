using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Core;
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
    public static void ForWorld(WorldState world)
    {
        if (world == null)
        {
            return;
        }

        ResolveWorkingSet(world);
        WarmWear(world);
        WarmActors(world);
        WarmHair(world);
        WarmProsthetics(world);
        WarmObjects(world);
        WarmMobs(world);
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
            Add(value.DefinitionId.StartsWith("building.", System.StringComparison.Ordinal)
                ? "building" : "object", value.DefinitionId);
        }
        foreach (var mob in world.Mobs)
        {
            Add("mob", mob.MobId);
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

            var type = worldObject.DefinitionId.StartsWith("building.",
                System.StringComparison.Ordinal) ? "building" : "object";
            ContentPrefabCache.Prewarm(type, worldObject.DefinitionId);
        }
    }

    private static void WarmMobs(WorldState world)
    {
        var ids = new HashSet<string>();
        foreach (var mob in world.Mobs)
        {
            if (!string.IsNullOrEmpty(mob.MobId))
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
}

}
