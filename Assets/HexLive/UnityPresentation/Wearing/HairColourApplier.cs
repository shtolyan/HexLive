using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

// §74: свой цвет волос у каждой колонистки.
//
// §161: явно выбранный HairColour хранится в NPC и передаётся в save/wire.
// Choose остаётся совместимым выбором для старых сейвов с пустым HairColour.
// Лобби фиксирует конкретный цвет в черновике до старта мира.
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
    /// которыми материал лежит в папке цвета. Поверхности, которых в расцветке
    /// нет, остаются прототипными — так и задумано: пресет красит только то,
    /// чего касается (резинка у ChunkyHair своего цвета).
    /// </summary>
    public static int Apply(GameObject hairInstance, IReadOnlyDictionary<string, Material> bySurface)
    {
        if (hairInstance == null || bySurface == null || bySurface.Count == 0)
        {
            return 0;
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
                if (bySurface.TryGetValue(surface, out var swap) && swap != slots[i])
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
