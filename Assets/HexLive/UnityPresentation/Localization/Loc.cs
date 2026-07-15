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

        /// <summary>True when a string is authored for this key (either language).</summary>
        public static bool Has(string key) => Table.ContainsKey(key);

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
            // Spec §53: compassion — the drive to care for hurting housemates.
            ["need.compassion"] = new[] { "Compassion", "Сострадание" },

            // Spec §48: status effects (buffs/debuffs) — chip tooltips. Each
            // kind has a short ".title" and a one-line ".desc" of what it does.
            ["panel.effects"] = new[] { "Effects", "Эффекты" },

            ["effect.bleeding.title"] = new[] { "Bleeding", "Кровотечение" },
            ["effect.bleeding.desc"] = new[]
            {
                "An open wound is draining her blood — dress it before she runs dry.",
                "Открытая рана теряет кровь — перевяжите, пока она не истекла."
            },
            ["effect.injured.title"] = new[] { "Injured", "Ранена" },
            ["effect.injured.desc"] = new[]
            {
                "Wounds slow her healing and lock part of her HP until they close.",
                "Раны замедляют заживление и удерживают часть здоровья, пока не закроются."
            },
            ["effect.hobbled.title"] = new[] { "Hobbled", "Хромает" },
            ["effect.hobbled.desc"] = new[]
            {
                "Hurt legs — she moves slower than usual.",
                "Повреждены ноги — двигается медленнее обычного."
            },
            ["effect.bandaged.title"] = new[] { "Bandaged", "Перевязана" },
            ["effect.bandaged.desc"] = new[]
            {
                "A dressed wound: the bleeding is stopped and it closes faster.",
                "Рана перевязана: кровотечение остановлено, заживает быстрее."
            },
            ["effect.strongsun.title"] = new[] { "Strong sun", "Палящее солнце" },
            ["effect.strongsun.desc"] = new[]
            {
                "The UV is high right here — bare skin will burn without cover or shade.",
                "Здесь высокий UV — без одежды или тени открытая кожа обгорит."
            },
            ["effect.sunstroke.title"] = new[] { "Sunstroke", "Солнечный удар" },
            ["effect.sunstroke.desc"] = new[]
            {
                "Too long in the sun on bare skin — it burns her and saps comfort.",
                "Долго на солнце с голой кожей — обжигает и снижает комфорт."
            },
            ["effect.sunburnt.title"] = new[] { "Sunburnt", "Обгорела" },
            ["effect.sunburnt.desc"] = new[]
            {
                "Raw red skin from the sun. Cosmetic — it fades and tans over.",
                "Кожа обгорела на солнце. Косметика — сходит и превращается в загар."
            },
            ["effect.hot.title"] = new[] { "Hot", "Жарко" },
            ["effect.hot.desc"] = new[]
            {
                "Uncomfortably warm — and edging toward heatstroke. Shade or water would help.",
                "Жарко и некомфортно — и клонит к перегреву. Не помешает тень или вода."
            },
            ["effect.heatstroke.title"] = new[] { "Heatstroke", "Перегрев" },
            ["effect.heatstroke.desc"] = new[]
            {
                "Far too hot — the heat is draining her health. She needs shade or water.",
                "Слишком жарко — жара отнимает здоровье. Нужна тень или вода."
            },
            ["effect.cold.title"] = new[] { "Cold", "Холодно" },
            ["effect.cold.desc"] = new[]
            {
                "Chilly and uncomfortable — and edging toward freezing. Warmth would help.",
                "Зябко и некомфортно — и клонит к переохлаждению. Не помешало бы согреться."
            },
            ["effect.freezing.title"] = new[] { "Freezing", "Замерзает" },
            ["effect.freezing.desc"] = new[]
            {
                "Far too cold — she's losing health. She needs warmth or clothes.",
                "Слишком холодно — теряет здоровье. Нужно тепло или одежда."
            },
            ["effect.soaked.title"] = new[] { "Soaked", "Промокла" },
            ["effect.soaked.desc"] = new[]
            {
                "Wet clothes give no warmth and weigh her down on the move.",
                "Мокрая одежда не греет и замедляет движение."
            },
            ["effect.starving.title"] = new[] { "Starving", "Голодает" },
            ["effect.starving.desc"] = new[]
            {
                "Hunger past the edge — her health is draining. She needs to eat now.",
                "Голод за гранью — здоровье убывает. Срочно нужно поесть."
            },
            ["effect.dehydrated.title"] = new[] { "Dehydrated", "Обезвожена" },
            ["effect.dehydrated.desc"] = new[]
            {
                "Thirst past the edge — her health is draining. She needs to drink.",
                "Жажда за гранью — здоровье убывает. Нужно пить."
            },
            ["effect.sick.title"] = new[] { "Sick", "Тошнит" },
            ["effect.sick.desc"] = new[]
            {
                "Poisoned by raw water — nauseous and losing health. Boil water before drinking.",
                "Отравилась сырой водой — тошнит, здоровье убывает. Воду нужно кипятить."
            },
            ["effect.exhausted.title"] = new[] { "Exhausted", "Вымотана" },
            ["effect.exhausted.desc"] = new[]
            {
                "Stamina spent to the floor — winded and desperate to sit down.",
                "Выносливость на нуле — запыхалась, хочет присесть."
            },
            ["effect.fainted.title"] = new[] { "Fainted", "Без сознания" },
            ["effect.fainted.desc"] = new[]
            {
                "Knocked out cold — she lies unable to act until she comes to.",
                "Потеряла сознание — лежит и не может действовать, пока не очнётся."
            },
            ["effect.wellfed.title"] = new[] { "Well fed", "Сыта" },
            ["effect.wellfed.desc"] = new[]
            {
                "Freshly full — content and at her best.",
                "Только поела — довольна и полна сил."
            },
            ["effect.rested.title"] = new[] { "Rested", "Отдохнула" },
            ["effect.rested.desc"] = new[]
            {
                "Energy and stamina both high — ready for anything.",
                "Энергия и выносливость высоки — готова ко всему."
            },
            ["effect.snug.title"] = new[] { "Snug", "В кровати" },
            ["effect.snug.desc"] = new[]
            {
                "Asleep in a proper bed — resting deeply and recovering energy faster.",
                "Спит в настоящей кровати — глубокий сон, энергия восстанавливается быстрее."
            },
            ["effect.stressed.title"] = new[] { "Stressed", "В стрессе" },
            ["effect.stressed.desc"] = new[]
            {
                "Stress is climbing toward the breaking point — enough can knock her out.",
                "Стресс растёт к пределу — на грани может свалить с ног."
            },
            ["effect.grieving.title"] = new[] { "Grieving", "Скорбит" },
            ["effect.grieving.desc"] = new[]
            {
                "Mourning a fallen housemate.",
                "Оплакивает погибшую подругу."
            },
            ["effect.lonely.title"] = new[] { "Lonely", "Одинока" },
            ["effect.lonely.desc"] = new[]
            {
                "Starved of company — she needs someone to talk to.",
                "Не хватает общения — нужен кто-то рядом."
            },
            ["effect.miserable.title"] = new[] { "Miserable", "Подавлена" },
            ["effect.miserable.desc"] = new[]
            {
                "Comfort has bottomed out — everything feels wretched.",
                "Комфорт на нуле — всё вокруг тягостно."
            },
            ["effect.content.title"] = new[] { "Content", "Довольна" },
            ["effect.content.desc"] = new[]
            {
                "Comfortable and at ease.",
                "Комфортно и спокойно."
            },
            ["effect.filthy.title"] = new[] { "Filthy", "Грязная" },
            ["effect.filthy.desc"] = new[]
            {
                "Grubby and long overdue a wash.",
                "Замызгана — давно пора помыться."
            },
            ["effect.cozy.title"] = new[] { "Cozy", "Уют" },
            ["effect.cozy.desc"] = new[]
            {
                "Warmed by the campfire — its glow slowly restores comfort.",
                "Греется у костра — его тепло создаёт уют и понемногу восполняет комфорт."
            },

            // Game menu (Escape)
            ["menu.title"] = new[] { "Menu", "Меню" },
            ["menu.continue"] = new[] { "Continue", "Продолжить" },
            ["menu.quit"] = new[] { "Quit game", "Выйти из игры" },

            // End summary (escape victory)
            ["end.eyebrow"] = new[] { "ESCAPE COMPLETE", "ПОБЕГ УДАЛСЯ" },
            ["end.title"] = new[] { "Escaped the island", "Выбрались с острова" },
            ["end.subtitle"] = new[]
            {
                "{0} of {1} survivors reached the raft. The simulation is complete.",
                "До плота добрались {0} из {1}. Симуляция завершена."
            },
            ["end.survivors"] = new[] { "Survivors", "Выжили" },
            ["end.fallen"] = new[] { "Fallen", "Погибли" },
            ["end.day"] = new[] { "Final day", "Финальный день" },
            ["end.time"] = new[] { "Time", "Время" },
            ["end.raft"] = new[] { "Raft", "Плот" },
            ["end.avg_health"] = new[] { "Avg health", "Среднее HP" },
            ["end.survivors_title"] = new[] { "Who survived", "Кто выжил" },
            ["end.fallen_title"] = new[] { "Who did not make it", "Кто не добрался" },
            ["end.no_fallen"] = new[] { "No one died on the way out.", "Никто не погиб по дороге." },
            ["end.close_tooltip"] = new[] { "Close summary and inspect the island", "Закрыть итоги и осмотреть остров" },
            ["end.hp"] = new[] { "HP", "HP" },
            ["end.blood"] = new[] { "Blood", "Кровь" },
            ["end.stamina"] = new[] { "Stamina", "Выносливость" },
            ["end.wounds"] = new[] { "Wounds", "Раны" },
            ["end.on_day"] = new[] { "day", "день" },
            ["end.tile"] = new[] { "tile", "клетка" },
            ["end.unknown"] = new[] { "Unknown survivor", "Неизвестный персонаж" },
            ["end.cause_unknown"] = new[] { "unknown cause", "причина неизвестна" },
            ["end.cause_bled_out"] = new[] { "blood loss", "кровопотеря" },
            ["end.cause_dog"] = new[] { "dog attack", "нападение собак" },
            ["end.cause_heat"] = new[] { "heatstroke", "перегрев" },
            ["end.cause_cold"] = new[] { "hypothermia", "переохлаждение" },
            ["end.cause_limb"] = new[] { "traumatic limb loss", "тяжёлая травма конечности" },
            ["end.cause_self_defense"] = new[] { "victim fought back", "жертва дала отпор" },
            ["end.cause_predation"] = new[] { "killed by a starving survivor", "убита голодным выжившим" },
            ["end.cause_shark"] = new[] { "shark bite", "укус акулы" },
            ["end.cause_starved"] = new[] { "starvation or dehydration", "голод или обезвоживание" },
            ["end.cause_sun"] = new[] { "sun exposure", "солнечный ожог" },
            ["end.cause_vital"] = new[] { "vital injury", "смертельная травма" },

            // Weather widget (top-left)
            ["weather.rain"] = new[] { "Rain", "Дождь" },
            ["weather.clear"] = new[] { "Clear", "Ясно" },
            ["weather.day"] = new[] { "Day", "День" },
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
            ["rel.affinity"] = new[] { "Affinity", "Отношение" },
            ["rel.familiarity"] = new[] { "Familiarity", "Знакомство" },
            ["rel.trust"] = new[] { "Trust", "Доверие" },

            // Sun / UV exposure (character panel)
            ["uv.high"] = new[] { "UV: high", "УФ: высокий" },
            ["uv.mid"] = new[] { "UV: medium", "УФ: средний" },
            ["uv.low"] = new[] { "UV: low", "УФ: низкий" },
            ["uv.none"] = new[] { "UV: none", "УФ: нет" },
            ["uv.shade"] = new[] { "in shade", "в тени" },

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
            ["goal.WarmUp"] = new[] { "Warming by the fire", "Греется у костра" },
            ["goal.CraftRack"] = new[] { "Building a drying rack", "Мастерит сушилку" },
            ["goal.DryClothes"] = new[] { "Drying clothes", "Сушит одежду" },
            ["goal.CraftBow"] = new[] { "Making a bow", "Мастерит лук" },
            ["goal.CraftArrows"] = new[] { "Making arrows", "Мастерит стрелы" },

            // Spec 41.1: loading screen phases.
            ["loading.world"] = new[] { "Shaping the island...", "Создаём остров..." },
            ["loading.time"] = new[] { "Time is passing...", "Идёт время..." },
            ["loading.day"] = new[] { "Day", "День" },
            ["loading.island"] = new[] { "Waking the island...", "Остров просыпается..." },
            ["loading.warmup"] = new[] { "Meeting the girls...", "Знакомимся с девочками..." },
            ["loading.done"] = new[] { "Welcome back", "С возвращением" },

            // Spec 41.4: main menu.
            ["menu.continue"] = new[] { "Continue", "Продолжить" },
            ["menu.newgame"] = new[] { "New game", "Новая игра" },
            ["menu.settings"] = new[] { "Settings", "Настройки" },
            ["menu.characters"] = new[] { "Characters", "Персонажи" },
            ["menu.tagline"] = new[]
            {
                "Explore. Build. Survive.\nYour world. Your rules.",
                "Исследуй. Строй. Выживай.\nТвой мир. Твои правила."
            },

            // ── Spec §51: character inventory (backpack) ──────────────────
            ["panel.inventory"] = new[] { "Inventory", "Инвентарь" },
            ["inv.button"] = new[] { "Backpack", "Рюкзак" },
            ["inv.worn"] = new[] { "Worn", "Надето" },
            ["inv.carried"] = new[] { "Carried", "В рюкзаке" },
            ["inv.empty"] = new[] { "Nothing here yet.", "Пока пусто." },
            ["inv.back"] = new[] { "Back", "Назад" },
            ["inv.slots"] = new[] { "slots", "слотов" },
            ["inv.covers"] = new[] { "Covers", "Закрывает" },
            ["inv.warmth"] = new[] { "Warmth", "Тепло" },
            ["inv.armor"] = new[] { "Armor", "Броня" },
            ["inv.layer"] = new[] { "Layer", "Слой" },
            ["inv.wetness"] = new[] { "Wetness", "Влажность" },
            ["inv.durability"] = new[] { "Durability", "Прочность" },
            ["inv.stack"] = new[] { "Stack", "Стек" },
            ["inv.clothing_hp"] = new[] { "Clothing HP", "HP одежды" },
            ["inv.water_container"] = new[] { "Water container", "Ёмкость для воды" },
            ["inv.water_left"] = new[] { "Water left", "Осталось воды" },
            ["inv.liters"] = new[] { "L", "л" },
            ["inv.condition_ok"] = new[] { "all good", "всё ок" },
            ["inv.condition_good"] = new[] { "light wear", "слегка изношена" },
            ["inv.condition_worn"] = new[] { "worn", "изношена" },
            ["inv.condition_torn"] = new[] { "critical wear", "критический износ" },
            ["inv.restores"] = new[] { "Restores hunger", "Утоляет голод" },
            ["inv.hydrates"] = new[] { "Quenches thirst", "Утоляет жажду" },
            ["inv.dry"] = new[] { "dry", "сухая" },
            ["inv.soaked"] = new[] { "soaked", "промокла" },
            ["inv.definition_id"] = new[] { "Definition", "ID предмета" },
            ["inv.status"] = new[] { "Status", "Статус" },
            ["inv.missing_definition"] = new[] { "Missing definition", "Нет описания" },

            // Wear layers (spec 31A.5B).
            ["layer.underwear"] = new[] { "Underwear", "Бельё" },
            ["layer.wear"] = new[] { "Clothing", "Одежда" },
            ["layer.outerwear"] = new[] { "Outerwear", "Верхняя одежда" },

            // Body zones (spec 19.3C).
            ["part.head"] = new[] { "head", "голова" },
            ["part.torso"] = new[] { "torso", "торс" },
            ["part.pelvis"] = new[] { "hips", "таз" },
            ["part.arml"] = new[] { "left arm", "левая рука" },
            ["part.armr"] = new[] { "right arm", "правая рука" },
            ["part.legl"] = new[] { "left leg", "левая нога" },
            ["part.legr"] = new[] { "right leg", "правая нога" },

            // Item categories — name + one-line generic blurb (fallback desc).
            ["itemcat.weapon.name"] = new[] { "Weapon", "Оружие" },
            ["itemcat.weapon.desc"] = new[]
            {
                "A weapon for hunting and fending off predators.",
                "Оружие для охоты и защиты от хищников."
            },
            ["itemcat.tool.name"] = new[] { "Tool", "Инструмент" },
            ["itemcat.tool.desc"] = new[]
            {
                "A tool for gathering and crafting.",
                "Инструмент для сбора ресурсов и ремесла."
            },
            ["itemcat.clothing.name"] = new[] { "Clothing", "Одежда" },
            ["itemcat.clothing.desc"] = new[]
            {
                "A garment — adds a little warmth and covers bare skin from the sun.",
                "Одежда — немного греет и прикрывает кожу от солнца."
            },
            ["itemcat.armor.name"] = new[] { "Armor", "Броня" },
            ["itemcat.armor.desc"] = new[]
            {
                "Protective gear — soaks up bites on the parts it covers.",
                "Защитное снаряжение — поглощает укусы на закрытых частях тела."
            },
            ["itemcat.food.name"] = new[] { "Food", "Еда" },
            ["itemcat.food.desc"] = new[]
            {
                "Something to eat — staves off hunger.",
                "Что-то съедобное — утоляет голод."
            },
            ["itemcat.water.name"] = new[] { "Water", "Вода" },
            ["itemcat.water.desc"] = new[]
            {
                "Drinking water — quenches thirst.",
                "Питьевая вода — утоляет жажду."
            },
            ["itemcat.medicine.name"] = new[] { "Medicine", "Лекарство" },
            ["itemcat.medicine.desc"] = new[]
            {
                "First aid — dresses wounds and stops bleeding.",
                "Первая помощь — перевязывает раны и останавливает кровь."
            },
            ["itemcat.resource.name"] = new[] { "Resource", "Ресурс" },
            ["itemcat.resource.desc"] = new[]
            {
                "A raw material for building and crafting.",
                "Сырьё для строительства и ремесла."
            },
            ["itemcat.misc.name"] = new[] { "Item", "Предмет" },
            ["itemcat.misc.desc"] = new[] { "A carried item.", "Носимый предмет." },

            ["item.unknown.name"] = new[] { "Unknown item", "Неизвестный предмет" },
            ["item.unknown.category"] = new[] { "Unknown", "Неизвестно" },
            ["item.unknown.desc"] = new[]
            {
                "This item is in the inventory, but its definition is missing: {0}.",
                "Этот предмет есть в инвентаре, но его описание не найдено: {0}."
            },

            // Core item names + descriptions (imported garments fall back to
            // their DisplayName + the clothing blurb above).
            ["item.food_coconut.name"] = new[] { "Coconut", "Кокос" },
            ["item.food_coconut.desc"] = new[]
            {
                "A ripe coconut — crack it open to drink the water, then eat the flesh.",
                "Спелый кокос — расколи, выпей воду, потом съешь мякоть."
            },
            ["item.food_coconut_pierced.name"] = new[] { "Pierced Coconut", "Дырявый кокос" },
            ["item.food_coconut_pierced.desc"] = new[]
            {
                "A pierced coconut holding drinkable water.",
                "Кокос с дыркой, внутри ещё есть питьевая вода."
            },
            ["item.food_coconut_open.name"] = new[] { "Opened Coconut", "Расколотый кокос" },
            ["item.food_coconut_open.desc"] = new[]
            {
                "A cracked coconut — the water is gone, but the flesh is a filling meal.",
                "Расколотый кокос — вода выпита, но мякоть ещё сытная еда."
            },
            ["item.food_meat_cooked.name"] = new[] { "Cooked Meat", "Жареное мясо" },
            ["item.food_meat_cooked.desc"] = new[]
            {
                "Meat roasted over the fire — the most filling food on the island.",
                "Мясо, зажаренное на костре — самая сытная еда на острове."
            },
            ["item.food_meat_raw.name"] = new[] { "Raw Meat", "Сырое мясо" },
            ["item.food_meat_raw.desc"] = new[]
            {
                "Raw game — inedible until it's cooked over the fire.",
                "Сырая дичь — несъедобна, пока не зажарить на костре."
            },
            ["item.tool_bottle.name"] = new[] { "Water Bottle", "Бутылка воды" },
            ["item.tool_bottle.desc"] = new[]
            {
                "A personal bottle — fill it at a spring or boil water in it before drinking.",
                "Личная бутылка — наполняй у источника или кипяти воду перед питьём."
            },
            ["item.tool_pot.name"] = new[] { "Pot", "Котелок" },
            ["item.tool_pot.desc"] = new[]
            {
                "A cooking pot — boils river water safe to drink over the campfire.",
                "Котелок — кипятит речную воду на костре, делая её безопасной."
            },
            ["item.tool_lighter.name"] = new[] { "Lighter", "Зажигалка" },
            ["item.tool_lighter.desc"] = new[]
            {
                "A lighter — sparks a campfire in an instant, no friction needed.",
                "Зажигалка — мгновенно разжигает костёр без возни с трением."
            },
            ["item.tool_axe_stone.name"] = new[] { "Stone Axe", "Каменный топор" },
            ["item.tool_axe_stone.desc"] = new[]
            {
                "A stone-headed axe — fells trees and splits firewood.",
                "Топор с каменным лезвием — валит деревья и колет дрова."
            },
            ["item.tool_pickaxe_stone.name"] = new[] { "Stone Pickaxe", "Каменная кирка" },
            ["item.tool_pickaxe_stone.desc"] = new[]
            {
                "A stone pickaxe — breaks boulders down into usable stone.",
                "Каменная кирка — дробит валуны на пригодный камень."
            },
            ["item.tool_saw.name"] = new[] { "Saw", "Пила" },
            ["item.tool_saw.desc"] = new[]
            {
                "A saw — cuts timber far faster than an axe.",
                "Пила — режет древесину куда быстрее топора."
            },
            ["item.tool_bow.name"] = new[] { "Bow", "Лук" },
            ["item.tool_bow.desc"] = new[]
            {
                "A hunting bow — brings down game from a safe distance.",
                "Охотничий лук — бьёт дичь с безопасного расстояния."
            },
            ["item.tool_spear.name"] = new[] { "Spear", "Копьё" },
            ["item.tool_spear.desc"] = new[]
            {
                "A wooden spear — for hunting up close and standing off dogs.",
                "Деревянное копьё — для охоты вблизи и обороны от собак."
            },
            ["item.resource_arrow.name"] = new[] { "Arrow", "Стрела" },
            ["item.resource_arrow.desc"] = new[]
            {
                "An arrow — ammunition for the bow.",
                "Стрела — боеприпас для лука."
            },
            ["item.resource_stone.name"] = new[] { "Stone", "Камень" },
            ["item.resource_stone.desc"] = new[]
            {
                "A chunk of stone — for tools and building.",
                "Кусок камня — для инструментов и построек."
            },
            // Spec §54: firewood split into log (chop output) and stick (fuel/craft).
            ["item.resource_log.name"] = new[] { "Log", "Бревно" },
            ["item.resource_log.desc"] = new[]
            {
                "A felled log — split it into sticks, build with it, or raft it.",
                "Бревно — расколи на палки, строй из него или сплавь на плот."
            },
            ["item.resource_stick.name"] = new[] { "Stick", "Палка" },
            ["item.resource_stick.desc"] = new[]
            {
                "A wooden stick — feeds the fire and frames tools.",
                "Деревянная палка — топливо для костра и основа инструментов."
            },
            // Spec §54: cordage chain + knife.
            ["item.resource_fiber.name"] = new[] { "Plant fiber", "Растительное волокно" },
            ["item.resource_fiber.desc"] = new[]
            {
                "Loose plant fiber — twisted into rope or woven into cloth.",
                "Растительное волокно — вьётся в верёвку или ткётся в ткань."
            },
            ["item.resource_rope.name"] = new[] { "Rope", "Верёвка" },
            ["item.resource_rope.desc"] = new[]
            {
                "A coil of rope — lashing for bows and builds.",
                "Моток верёвки — обвязка для луков и построек."
            },
            ["item.resource_cloth.name"] = new[] { "Cloth", "Ткань" },
            ["item.resource_cloth.desc"] = new[]
            {
                "A bolt of woven cloth — for shelters and dressings.",
                "Отрез тканого полотна — для укрытий и перевязок."
            },
            ["item.tool_knife.name"] = new[] { "Knife", "Нож" },
            ["item.tool_knife.desc"] = new[]
            {
                "A stone knife — the tool for butchering a carcass.",
                "Каменный нож — инструмент для разделки туши."
            },
            ["item.resource_hide.name"] = new[] { "Rabbit Hide", "Кроличья шкура" },
            ["item.resource_hide.desc"] = new[]
            {
                "A cured hide — the raw stock for leather clothing.",
                "Выделанная шкура — сырьё для кожаной одежды."
            },
            ["item.resource_palm_leaf.name"] = new[] { "Palm Leaf", "Пальмовый лист" },
            ["item.resource_palm_leaf.desc"] = new[]
            {
                "A broad palm leaf — woven into mats, tents and shelter.",
                "Широкий пальмовый лист — плетётся в циновки, тенты и укрытия."
            },
            ["item.resource_herb_leaf.name"] = new[] { "Herb Leaf", "Лист целебной травы" },
            ["item.resource_herb_leaf.desc"] = new[]
            {
                "A healing leaf — two make a herbal bandage at the fire.",
                "Целебный лист — из двух у костра выходит травяная повязка."
            },
            ["item.item_bandage.name"] = new[] { "Bandage", "Бинт" },
            ["item.item_bandage.desc"] = new[]
            {
                "A gauze bandage — dresses the worst wound and stops the bleeding.",
                "Марлевый бинт — перевязывает худшую рану и останавливает кровь."
            },
            ["item.clothing_coat.name"] = new[] { "Coat", "Пальто" },
            ["item.clothing_coat.desc"] = new[]
            {
                "A warm coat over the torso and arms — real cover against the cold.",
                "Тёплое пальто на торс и руки — настоящая защита от холода."
            },
            ["item.clothing_leather_pants.name"] = new[] { "Leather Pants", "Кожаные штаны" },
            ["item.clothing_leather_pants.desc"] = new[]
            {
                "Sturdy leather pants — warm the legs and blunt bites to them.",
                "Прочные кожаные штаны — греют ноги и смягчают укусы по ним."
            },
            ["item.underwear_cloth.name"] = new[] { "Cloth Underwear", "Тканевое бельё" },
            ["item.underwear_cloth.desc"] = new[]
            {
                "Plain cloth underwear — the starting layer, barely any warmth.",
                "Простое тканевое бельё — стартовый слой, тепла почти не даёт."
            },
            ["item.clothing_top_tropic.name"] = new[] { "Tropic Top", "Тропический топ" },
            ["item.clothing_top_tropic.desc"] = new[]
            {
                "A light printed summer top — a whisper of warmth, purely for looks.",
                "Лёгкий летний топ с принтом — чуть греет, в основном для вида."
            },
            ["item.clothing_top_tiedye.name"] = new[] { "Tie-Dye Top", "Топ тай-дай" },
            ["item.clothing_top_tiedye.desc"] = new[]
            {
                "A tie-dye summer top — light and cheerful, barely any warmth.",
                "Летний топ тай-дай — лёгкий и яркий, тепла почти не даёт."
            },
            ["item.underwear_panty_leo.name"] = new[] { "Leopard Panties", "Леопардовые трусики" },
            ["item.underwear_panty_leo.desc"] = new[]
            {
                "Leopard-print panties — decorative underwear.",
                "Трусики с леопардовым принтом — декоративное бельё."
            },
            ["item.underwear_panty_stars.name"] = new[] { "Star Panties", "Трусики со звёздами" },
            ["item.underwear_panty_stars.desc"] = new[]
            {
                "Star-print panties — decorative underwear.",
                "Трусики с принтом-звёздами — декоративное бельё."
            },
            ["item.armor_leather.name"] = new[] { "Leather Armor", "Кожаная броня" },
            ["item.armor_leather.desc"] = new[]
            {
                "Leather chest armor — soaks up bites to the torso at some midday heat.",
                "Кожаная нагрудная броня — гасит укусы в торс ценой дневной жары."
            },
            ["item.armor_heavy.name"] = new[] { "Heavy Armor", "Тяжёлая броня" },
            ["item.armor_heavy.desc"] = new[]
            {
                "Heavy armor over torso and hips — the best protection, hot to wear.",
                "Тяжёлая броня на торс и таз — лучшая защита, но в ней жарко."
            }
        };
    }
}
