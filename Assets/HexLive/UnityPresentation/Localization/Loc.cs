using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Localization
{
    public enum Language
    {
        English = 0,
        Russian = 1
    }

    /// <summary>
    /// Lightweight in-code localization. Two languages for now (English,
    /// Russian); extend by adding another column to each table entry and a
    /// value to <see cref="Language"/>. UI subscribes to
    /// <see cref="LanguageChanged"/> and re-reads strings via <see cref="Get"/>.
    /// </summary>
    public static class Loc
    {
        public static event Action LanguageChanged;

        private static Language _language = DetectDefault();

        public static Language Current
        {
            get => _language;
            set
            {
                if (_language == value)
                {
                    return;
                }

                _language = value;
                LanguageChanged?.Invoke();
            }
        }

        public static void Toggle()
        {
            Current = _language == Language.English ? Language.Russian : Language.English;
        }

        /// <summary>Short two-letter code for the current language (UI badge).</summary>
        public static string Code => _language == Language.Russian ? "RU" : "EN";

        public static string Get(string key)
        {
            if (Table.TryGetValue(key, out var values))
            {
                var index = (int)_language;
                if (index < values.Length && !string.IsNullOrEmpty(values[index]))
                {
                    return values[index];
                }

                return values[0]; // English fallback
            }

            return key;
        }

        /// <summary>Localized short "what they want" phrase for a goal enum name.</summary>
        public static string Goal(string goalName)
        {
            var key = "goal." + goalName;
            return Table.ContainsKey(key) ? Get(key) : goalName;
        }

        private static Language DetectDefault()
        {
            return Application.systemLanguage == SystemLanguage.Russian
                ? Language.Russian
                : Language.English;
        }

        // key -> [English, Russian]
        private static readonly Dictionary<string, string[]> Table = new()
        {
            // Section titles / labels
            ["panel.needs"] = new[] { "Needs", "Потребности" },
            ["panel.relations"] = new[] { "Relationships", "Отношения" },
            ["panel.wants"] = new[] { "Now wants", "Сейчас хочет" },
            ["panel.health"] = new[] { "Health", "Здоровье" },
            ["panel.role"] = new[] { "islander", "островитянка" },
            ["panel.live"] = new[] { "live", "прямой эфир" },
            ["panel.none"] = new[] { "—", "—" },

            // Needs (shown as well-being: full = good)
            ["need.hunger"] = new[] { "Fed", "Сытость" },
            ["need.thirst"] = new[] { "Hydration", "Жажда" },
            ["need.energy"] = new[] { "Energy", "Энергия" },
            ["need.comfort"] = new[] { "Comfort", "Комфорт" },
            ["need.social"] = new[] { "Social", "Общение" },
            ["need.thermal"] = new[] { "Warmth", "Тепло" },
            // Bipolar dial: 0 comfy, + hot, − cold (spec 29C.10).
            ["need.temperature"] = new[] { "Temperature", "Температура" },
            // Spec 40: survivor params on the character panel.
            ["need.stamina"] = new[] { "Stamina", "Выносливость" },
            ["need.blood"] = new[] { "Blood", "Кровь" },
            ["need.hygiene"] = new[] { "Hygiene", "Гигиена" },
            ["need.stress"] = new[] { "Stress", "Стресс" },

            // Game menu (Escape)
            ["menu.title"] = new[] { "Menu", "Меню" },
            ["menu.continue"] = new[] { "Continue", "Продолжить" },
            ["menu.quit"] = new[] { "Quit game", "Выйти из игры" },

            // Weather widget (top-left)
            ["weather.rain"] = new[] { "Rain", "Дождь" },
            ["weather.clear"] = new[] { "Clear", "Ясно" },
            ["phase.Morning"] = new[] { "morning", "утро" },
            ["phase.Day"] = new[] { "day", "день" },
            ["phase.Evening"] = new[] { "evening", "вечер" },
            ["phase.Night"] = new[] { "night", "ночь" },

            // Relationship kinds (by affinity)
            ["rel.close"] = new[] { "close", "близкие" },
            ["rel.friend"] = new[] { "friend", "друг" },
            ["rel.acquaint"] = new[] { "acquaintance", "знакомый" },
            ["rel.neutral"] = new[] { "neutral", "нейтрально" },
            ["rel.tense"] = new[] { "tense", "напряжённо" },
            ["rel.hostile"] = new[] { "hostile", "враждебно" },

            // Status badges
            ["badge.starving"] = new[] { "STARVING", "ГОЛОДАЕТ" },
            ["badge.fighting"] = new[] { "FIGHTING", "ДЕРЁТСЯ" },

            // Goals -> thought phrases
            ["goal.None"] = new[] { "Just being", "Просто существует" },
            ["goal.Idle"] = new[] { "Taking a break", "Отдыхает" },
            ["goal.Eat"] = new[] { "Wants to eat", "Хочет поесть" },
            ["goal.GetFood"] = new[] { "Looking for food", "Ищет еду" },
            ["goal.Sleep"] = new[] { "Wants to sleep", "Хочет спать" },
            ["goal.Sit"] = new[] { "Wants to rest", "Хочет присесть" },
            ["goal.Dress"] = new[] { "Wants to dress", "Хочет одеться" },
            ["goal.Undress"] = new[] { "Too warm to wear that", "Раздевается — жарко" },
            ["goal.Socialize"] = new[] { "Wants company", "Хочет пообщаться" },
            ["goal.Explore"] = new[] { "Exploring", "Исследует остров" },
            ["goal.Flee"] = new[] { "Running for cover", "Бежит в укрытие" },
            ["goal.Drink"] = new[] { "Wants to drink", "Хочет пить" },
            ["goal.GatherTools"] = new[] { "Fetching tools", "Ищет инструменты" },
            ["goal.GatherWood"] = new[] { "Gathering wood", "Собирает дрова" },
            ["goal.GatherStone"] = new[] { "Gathering stone", "Собирает камни" },
            ["goal.TendFire"] = new[] { "Tending the fire", "Поддерживает костёр" },
            ["goal.Hunt"] = new[] { "Hunting", "Охотится" },
            ["goal.CraftSpear"] = new[] { "Making a spear", "Мастерит копьё" },
            ["goal.CookMeat"] = new[] { "Cooking meat", "Готовит мясо" },
            ["goal.CraftLeather"] = new[] { "Sewing clothes", "Шьёт одежду" },
            ["goal.Mourn"] = new[] { "Mourning", "Скорбит" },
            ["goal.Bury"] = new[] { "Burying the dead", "Хоронит" },
            ["goal.CraftAxe"] = new[] { "Making an axe", "Мастерит топор" },
            ["goal.CraftPickaxe"] = new[] { "Making a pickaxe", "Мастерит кирку" },
            ["goal.HarvestTree"] = new[] { "Felling a tree", "Рубит дерево" },
            ["goal.MineBoulder"] = new[] { "Breaking a boulder", "Дробит камень" },
            ["goal.Build"] = new[] { "Building the hut", "Строит хижину" },
            ["goal.CoolOff"] = new[] { "Cooling off", "Остывает" },
            ["goal.CraftRack"] = new[] { "Building a drying rack", "Мастерит сушилку" },
            ["goal.DryClothes"] = new[] { "Drying clothes", "Сушит одежду" },
            ["goal.CraftBow"] = new[] { "Making a bow", "Мастерит лук" },
            ["goal.CraftArrows"] = new[] { "Making arrows", "Мастерит стрелы" },

            // Spec 41.1: loading screen phases.
            ["loading.world"] = new[] { "Shaping the island...", "Создаём остров..." },
            ["loading.time"] = new[] { "Time is passing...", "Идёт время..." },
            ["loading.island"] = new[] { "Waking the island...", "Остров просыпается..." },
            ["loading.warmup"] = new[] { "Meeting the girls...", "Знакомимся с девочками..." },
            ["loading.done"] = new[] { "Welcome back", "С возвращением" },

            // Spec 41.4: main menu.
            ["menu.continue"] = new[] { "Continue", "Продолжить" },
            ["menu.newgame"] = new[] { "New game", "Новая игра" }
        };
    }
}
