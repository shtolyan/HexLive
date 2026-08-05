using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// §74: свой цвет волос у каждой колонистки.
//
// Цвет НЕ хранится в симуляции и не едет по проводу: он выводится из id
// колонистки, а id и так сохраняется и передаётся. Поэтому цвет одинаков у
// сервера и клиента, переживает загрузку сейва и не стоит ни поля в снапшоте,
// ни версии формата — за косметику такая цена была бы велика. Плата одна: если
// художник ДОБАВИТ причёске расцветок, уже живущие колонистки перекрасятся.
//
// Правило живёт ЗДЕСЬ, а не в NpcActorView, чтобы проверяющее меню
// (HexLive ▸ Actors ▸ Validate Hair Colours) гоняло тот же код, что и игра, —
// иначе проверка подтверждала бы саму себя.
public static class HairColourApplier
{
    /// <summary>Расцветка для этой причёски и этого колониста; null — если
    /// расцветок нет (у половины причёсок она одна, «как из коробки»).</summary>
    public static ActorAppearanceCatalog.HairColour Choose(string hairstyle, int npcId)
    {
        var catalog = ActorAppearanceCatalog.Instance;
        if (catalog == null || string.IsNullOrEmpty(hairstyle))
        {
            return null;
        }

        var colours = catalog.ColoursFor(hairstyle);
        if (colours.Count == 0)
        {
            return null;
        }

        // Хеш от id, а не Random: два клиента одного мира обязаны сойтись.
        var hash = (uint)npcId * 2654435761u;
        return colours[(int)(hash % (uint)colours.Count)];
    }

    /// <summary>
    /// Ставит материалы расцветки на живую причёску. Возвращает, сколько слотов
    /// поменялось — ноль означает, что имена не сошлись, то есть поломку.
    ///
    /// Материалы ОБЩИЕ (sharedMaterials) и подбираются по ИМЕНИ поверхности:
    /// порядок сабмешей у причёски не гарантирован, а имена — те же, под
    /// которыми материал лежит в папке цвета. Поверхности, которых в папке нет,
    /// остаются прототипными — так и задумано: пресет красит только то, чего
    /// касается (резинка у ChunkyHair своего цвета).
    /// </summary>
    public static int Apply(GameObject hairInstance, ActorAppearanceCatalog.HairColour colour)
    {
        if (hairInstance == null || colour == null || colour.materials.Count == 0)
        {
            return 0;
        }

        var byName = new Dictionary<string, Material>(colour.materials.Count);
        foreach (var material in colour.materials)
        {
            if (material != null)
            {
                byName[material.name] = material;
            }
        }

        var swapped = 0;
        foreach (var renderer in hairInstance.GetComponentsInChildren<Renderer>(true))
        {
            var slots = renderer.sharedMaterials;
            var touched = false;
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    continue;
                }

                var surface = slots[i].name.Replace(" (Instance)", string.Empty);
                if (byName.TryGetValue(surface, out var swap) && swap != slots[i])
                {
                    slots[i] = swap;
                    touched = true;
                    swapped++;
                }
            }

            if (touched)
            {
                renderer.sharedMaterials = slots;
            }
        }

        return swapped;
    }
}

}
