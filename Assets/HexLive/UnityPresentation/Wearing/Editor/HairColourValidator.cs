#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// §74: проверка случайного цвета волос БЕЗ запуска игры.
    ///
    /// Проверять глазами пришлось бы через главное меню (мир создаётся только
    /// после «Новой игры»), а сломаться тут может ровно одно и молча: имена
    /// материалов в папке цвета обязаны совпасть с именами поверхностей на
    /// префабе причёски. Не совпали — подмена не сделает НИЧЕГО, и все девушки
    /// останутся одного цвета, без единой ошибки в консоли.
    ///
    /// Поэтому меню инстанцирует каждую причёску, применяет к ней ту же
    /// <see cref="HairColourApplier"/>, что и игра, и считает, сколько слотов
    /// реально поменялось.
    ///
    /// Menu: HexLive ▸ Actors ▸ Validate Hair Colours
    /// </summary>
    public static class HairColourValidator
    {
        [MenuItem("HexLive/Actors/Validate Hair Colours")]
        public static void Validate()
        {
            var catalog = ActorAppearanceCatalog.Instance;
            if (catalog == null)
            {
                Debug.LogError("[§74] нет ActorAppearanceCatalog — сначала Rebuild Appearance Catalog.");
                return;
            }

            var broken = 0;
            var without = new List<string>();
            foreach (var hair in catalog.hairstyles)
            {
                if (hair == null)
                {
                    continue;
                }

                var colours = catalog.ColoursFor(hair.name);
                if (colours.Count == 0)
                {
                    without.Add(hair.name);
                    continue;
                }

                // Разные id — разные цвета: это и есть «случайный при старте».
                var picked = new HashSet<string>();
                for (var npcId = 1; npcId <= 8; npcId++)
                {
                    var colour = HairColourApplier.Choose(hair.name, npcId);
                    if (colour != null)
                    {
                        picked.Add(colour.colour);
                    }
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(hair.gameObject);
                int swapped;
                try
                {
                    swapped = HairColourApplier.Apply(instance, HairColourApplier.Choose(hair.name, 1));
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }

                var line = $"[§74] {hair.name}: расцветок {colours.Count}, " +
                           $"на 8 колонистках выпало разных {picked.Count}, " +
                           $"подменено слотов материалов {swapped}";
                if (swapped > 0)
                {
                    Debug.Log(line);
                }
                else
                {
                    Debug.LogError(line + " — имена материалов расцветки НЕ совпали с поверхностями причёски");
                    broken++;
                }
            }

            Debug.Log($"[§74] цвет волос: причёсок с расцветками {catalog.hairstyles.Count - without.Count}, " +
                      $"сломанных {broken}; без расцветок (носят прототип): {string.Join(", ", without)}");
        }
    }
}
#endif
