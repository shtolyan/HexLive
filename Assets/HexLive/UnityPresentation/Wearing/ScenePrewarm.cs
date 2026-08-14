using System.Collections.Generic;
using HexLive.Simulation.Core;

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
// ⭐ ПРАВИЛО: новая семья addressable-контента, зависящего от состояния мира,
// добавляет сюда свой проход. Контент, который от мира НЕ зависит (иконки —
// один бандл на 0.73 МБ, модели предметов и оружия — папка Resources целиком),
// греется безусловно в SimulationRunnerBehaviour.WarmContent и здесь ему делать
// нечего.
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

        WarmWear(world);
        WarmHair(world);
        WarmProsthetics(world);
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
