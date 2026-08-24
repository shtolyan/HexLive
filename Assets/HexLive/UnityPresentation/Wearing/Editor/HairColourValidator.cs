#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
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
    /// Для атомарных hair bundles есть вторая молчаливая поломка:
    /// каталог хранит ИМЕНА, а материал приезжает по адресу — и если разметка
    /// не прогонялась, адреса нет, загрузка вернёт null, и итог тот же. Поэтому
    /// меню берёт материалы ПО ПУТИ, собранному из тех же имён, и ругается
    /// отдельно, когда файла нет.
    ///
    /// Menu: HexLive ▸ Actors ▸ Validate Hair Colours
    /// </summary>
    public static class HairColourValidator
    {
        private const string HairRoot = "Assets/ImportedActors/Hair";

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
            foreach (var hairId in catalog.hairstyles)
            {
                if (string.IsNullOrEmpty(hairId))
                {
                    continue;
                }

                var colours = catalog.ColoursFor(hairId);
                if (colours.Count == 0)
                {
                    without.Add(hairId);
                    continue;
                }

                // Разные id — разные цвета: это и есть «случайный при старте».
                var picked = new HashSet<string>();
                for (var npcId = 1; npcId <= 8; npcId++)
                {
                    var colour = HairColourApplier.Choose(hairId, npcId);
                    if (colour != null)
                    {
                        picked.Add(colour.colour);
                    }
                }

                var chosen = HairColourApplier.Choose(hairId, 1);
                var materials = LoadColourByPath(hairId, chosen);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{HairRoot}/{hairId}/{hairId}.prefab");
                if (prefab == null)
                {
                    Debug.LogError($"[§74] {hairId}: нет префаба — причёска в каталоге есть, ассета нет.");
                    broken++;
                    continue;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                int swapped;
                try
                {
                    swapped = HairColourApplier.Apply(instance, materials);
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }

                var line = $"[§74] {hairId}: расцветок {colours.Count}, " +
                           $"на 8 колонистках выпало разных {picked.Count}, " +
                           $"материалов найдено {materials.Count}, подменено слотов {swapped}";
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

        // В редакторе authoring-материал берётся ПО ПУТИ, не через runtime API:
        // проверка должна работать и до того, как контент собран, — иначе она
        // ловила бы «не собрано» вместо «имена разошлись».
        private static Dictionary<string, Material> LoadColourByPath(
            string hairId, ActorAppearanceCatalog.HairColour colour)
        {
            var result = new Dictionary<string, Material>();
            if (colour == null)
            {
                return result;
            }

            foreach (var surface in colour.surfaces)
            {
                var path = $"{HairRoot}/{hairId}/Materials/{colour.colour}/{surface}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null)
                {
                    result[surface] = material;
                }
                else
                {
                    Debug.LogError($"[§74] нет материала {path} — каталог знает поверхность, файла нет.");
                }
            }

            return result;
        }
    }
}
#endif
