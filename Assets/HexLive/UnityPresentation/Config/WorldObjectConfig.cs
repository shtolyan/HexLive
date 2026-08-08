using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// One ScriptableObject asset PER WORLD OBJECT (дерево, бревно, туша,
    /// мясо…): what SKILLS apply to it and what each yields. The character
    /// then finds the right gear for the skill from the gear base by himself
    /// (GearCatalog capability match) — the asset never names a tool.
    ///
    ///   бревно:  рубка (ChopWood) → палки;  распил (Saw) → доски
    ///   туша:    освежевание (Butcher) → мясо + кожа
    ///
    /// Drop under <c>Resources/HexLive/WorldObjects/</c>; ObjectTuning merges
    /// every asset into the sim's object definitions at startup: same-id
    /// actions replace the built-in ones, new actions/objects append — so an
    /// asset can ADD one verb to the log without re-describing the log.
    ///
    /// Что остаётся кодом: ГОЛ-слой (когда персонаж этого ХОЧЕТ) и станции с
    /// расходниками (готовка = RecipeCatalog: входы + «нужен горящий костёр»).
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/World Object Config", fileName = "WorldObjectConfig")]
    public sealed class WorldObjectConfig : ScriptableObject
    {
        public const string ResourceFolder = "HexLive/WorldObjects";

        [Tooltip("Id объекта мира (resource.log, carcass.animal, tree.palm…). Новый id = новый тип объекта.")]
        public string objectId = "";
        [Tooltip("Имя для UI (только для НОВЫХ объектов; у существующих остаётся их имя).")]
        public string displayName = "";
        [Tooltip("Теги (добавляются к существующим): Wood, Resource, Carcass…")]
        public string[] tags;

        [System.Serializable]
        public sealed class ActionRow
        {
            [Tooltip("Id действия (split.log, saw.log, butcher.carcass…). Совпадает с существующим — заменяет его.")]
            public string actionId = "";
            [Tooltip("Тип интеракции (Process/Harvest/Butcher/PickUp…).")]
            public InteractionType type = InteractionType.Process;
            [Tooltip("СКИЛЛЫ (enum, МАССИВ any-of): бревно рубится топором (ChopWood) ИЛИ ножом (Cut), если перечислены оба. Пусто = руки.")]
            public GearCapability[] requiredCapabilities;
            [Tooltip("Длительность в тиках (4 тика = 1 сек).")]
            public int durationTicks = 40;
            [Tooltip("Свои ресурсы у КАЖДОГО действия.")]
            public YieldRow[] yields;
        }

        [System.Serializable]
        public sealed class YieldRow
        {
            [Tooltip("Что выпадает — ссылка на конфиг объекта; id достанется из неё.")]
            public WorldObjectConfig item;
            [Tooltip("Фолбэк: строковый id, если у ресурса ещё нет ассета.")]
            public string itemId = "";
            [Range(1, 20)] public int count = 1;
            [Tooltip("Разбросать по соседним точкам (иначе — к ногам).")]
            public bool scatter = true;

            public string ResolveId() =>
                item != null && !string.IsNullOrEmpty(item.objectId)
                    ? item.objectId
                    : itemId?.Trim() ?? "";
        }

        [Tooltip("Действия-скиллы, применимые к объекту.")]
        public ActionRow[] actions;

        [Header("Склад (начальное содержимое объекта — массив)")]
        [Tooltip("Что лежит внутри при появлении: дырявый кокос — Water × 4. Дальше сюда можно складировать разные ресурсы.")]
        public StorageRow[] storage;

        [System.Serializable]
        public sealed class StorageRow
        {
            [Tooltip("Вид ресурса (enum).")]
            public StoredKind kind = StoredKind.Water;
            [Range(0f, 100f)] public float amount = 1f;
        }

        [Header("Продюсер (спавнит ресурсы рядом с собой)")]
        [Tooltip("Включить: объект сам порождает ресурс в радиусе (пальма роняет кокосы).")]
        public bool isProducer;
        [Tooltip("ЧТО спавнится — ссылка на конфиг объекта (id достанется из неё).")]
        public WorldObjectConfig producedItem;
        [Tooltip("Фолбэк: строковый id, если у ресурса нет ассета.")]
        public string producedItemId = "";
        [Tooltip("СКОРОСТЬ: тиков между спавнами (4 тика = 1 сек; пальма — 300).")]
        [Range(20, 2000)] public int produceIntervalTicks = 300;
        [Tooltip("Кап: больше этого числа непособранных штук вокруг не лежит (пальма — 2).")]
        [Range(1, 10)] public int produceMaxConcurrent = 2;
        [Tooltip("РАДИУС спавна в тайлах (пальма — 1).")]
        [Range(1, 5)] public int produceRadiusTiles = 1;

        [Header("Еда")]
        [Tooltip("Еда — по сути галочка: добавляет объекту тег Food (его видят голодные планы).")]
        public bool isFood;

        [Header("Крафт (пусто/выкл = добывается, не крафтится)")]
        [Tooltip("Крафтится ли предмет (верёвка, ткань, бинт…).")]
        public bool craftable;
        [Tooltip("Место крафта. Anywhere = на месте персонажа, без станции.")]
        public CraftPlace craftStation = CraftPlace.Anywhere;
        [Tooltip("Нужен ли ГОРЯЩИЙ костёр (форсирует станцию Campfire).")]
        public bool craftNeedsLitFire;
        public CraftIngredient[] craftIngredients;

        [System.Serializable]
        public sealed class CraftIngredient
        {
            [Tooltip("Ссылка на конфиг ингредиента — id достанется из неё.")]
            public WorldObjectConfig item;
            [Tooltip("Фолбэк: строковый id, если у ресурса ещё нет ассета.")]
            public string itemId = "";
            [Range(1, 30)] public int count = 1;

            public string ResolveId() =>
                item != null && !string.IsNullOrEmpty(item.objectId)
                    ? item.objectId
                    : itemId?.Trim() ?? "";
        }

        public void ApplyRecipe()
        {
            if (!craftable || craftIngredients == null || craftIngredients.Length == 0)
            {
                return;
            }

            var inputs = new List<RecipeIngredient>();
            foreach (var ing in craftIngredients)
            {
                var id = ing?.ResolveId();
                if (!string.IsNullOrEmpty(id))
                {
                    inputs.Add(new RecipeIngredient(id, ing.count));
                }
            }

            if (inputs.Count > 0)
            {
                RecipeCatalog.Override(objectId, inputs.ToArray(), craftNeedsLitFire,
                    craftStation switch
                    {
                        CraftPlace.Campfire => "Campfire",
                        CraftPlace.Workbench => "Workbench",
                        _ => ""
                    });
            }
        }

        public ObjectDefinition ToDefinition()
        {
            var def = new ObjectDefinition
            {
                Id = objectId ?? string.Empty,
                DisplayName = string.IsNullOrEmpty(displayName) ? objectId : displayName,
            };
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        def.Tags.Add(tag.Trim());
                    }
                }
            }

            if (isFood && !def.Tags.Contains("Food"))
            {
                def.Tags.Add("Food");
            }

            if (storage != null)
            {
                foreach (var row in storage)
                {
                    if (row != null && row.amount > 0f)
                    {
                        def.Storage.Add(new StoredResource { Kind = row.kind, Amount = row.amount });
                    }
                }
            }

            if (isProducer)
            {
                var producedId = producedItem != null && !string.IsNullOrEmpty(producedItem.objectId)
                    ? producedItem.objectId
                    : producedItemId?.Trim() ?? "";
                if (!string.IsNullOrEmpty(producedId))
                {
                    def.Produce = new ProduceDefinition
                    {
                        ProducedDefinitionId = producedId,
                        IntervalTicks = produceIntervalTicks,
                        MaxConcurrent = produceMaxConcurrent,
                        MaxDistanceTiles = produceRadiusTiles,
                    };
                }
            }

            if (actions != null)
            {
                foreach (var action in actions)
                {
                    if (action == null || string.IsNullOrWhiteSpace(action.actionId))
                    {
                        continue;
                    }

                    var interaction = new InteractionDefinition
                    {
                        Id = action.actionId.Trim(),
                        Type = action.type,
                        DurationTicks = Mathf.Max(1, action.durationTicks),
                    };
                    if (action.requiredCapabilities != null)
                    {
                        foreach (var capability in action.requiredCapabilities)
                        {
                            if (capability != GearCapability.None &&
                                !interaction.RequiredCapabilities.Contains(capability))
                            {
                                interaction.RequiredCapabilities.Add(capability);
                            }
                        }
                    }

                    if (action.yields != null)
                    {
                        foreach (var y in action.yields)
                        {
                            var yieldId = y?.ResolveId();
                            if (!string.IsNullOrEmpty(yieldId))
                            {
                                interaction.Yields.Add(new HarvestDrop
                                {
                                    DefinitionId = yieldId,
                                    Count = y.count,
                                    Scatter = y.scatter,
                                });
                            }
                        }
                    }

                    def.Interactions.Add(interaction);
                }
            }

            return def;
        }
    }

    /// <summary>Loads every WorldObjectConfig asset into the sim's
    /// <see cref="WorldObjectLibrary"/> — called from PrototypeRuntimeBootstrap
    /// BEFORE the world (and its content catalog) is built.</summary>
    public static class ObjectTuning
    {
        public static void LoadAndApply()
        {
            WorldObjectLibrary.Clear();
            var configs = Resources.LoadAll<WorldObjectConfig>(WorldObjectConfig.ResourceFolder);
            if (configs == null)
            {
                return;
            }

            foreach (var config in configs)
            {
                if (config != null && !string.IsNullOrEmpty(config.objectId))
                {
                    WorldObjectLibrary.Override(config.ToDefinition());
                    config.ApplyRecipe();
                }
            }
        }
    }
}
