namespace HexLive.Simulation.Runtime
{

// Wildlife-director knobs: population caps, respawn cadence, raid timing and
// hunt chances that used to be private consts in Mob/Rabbit/SharkSystem.
// Static so the WorldBalance config asset can push tuned values at boot;
// systems read them through shims at the old const names. (Per-mob COMBAT
// stats — bite, HP, windup — stay in MobCatalog/MobConfig assets.)
public static class WildlifeBalance
{
    // §46 dog-pack director.
    public static int MaxDogs = 2;
    public static int DogRespawnCheckTicks = 7200;
    public static int RaidDuskOffsetTicks = 1800;
    public static int DogSpawnMinDistanceFromNpc = 5;

    // §46 v4: сколько ночная стая ГОСТИТ, прежде чем уйти с острова. 2400
    // тиков = один событийный цикл = 10 реальных минут. MaxDogs — потолок
    // ЖИТЕЛЕЙ; рейд приводит гостей сверх него, и уходят они сами.
    public static int RaidLingerTicks = 2400;

    // Уходит стая не на глазах у колонии — гость исчезает первым тиком, когда
    // его никто не видит (§125). Если колония стоит лагерем прямо на нём и он
    // не выходит из виду, этот запас — предохранитель: после него гость
    // уходит независимо от зрителей, иначе «временная» стая стала бы вечной.
    public static int RaidDepartureBackstopTicks = 2400;

    // Spec 29C.3 (stuck-chase give-up): a chase that hasn't moved the dog for
    // this many CONTINUOUS ticks (no walkable route — quarry behind the hut,
    // approach ring occupied) is hopeless; the dog drops the target and roams
    // off instead of standing frozen mid-camp. Melee resets the clock.
    public static int DogChaseStallGiveUpTicks = 200;

    // How long a dog that gave up ignores prey before hunting again — long
    // enough to actually wander away from the camp it was wedged against.
    public static int DogHuntCooldownTicks = 600;

    // Rabbits/crabs (spec 31C).
    public static int MaxRabbits = 4;
    public static int RabbitRespawnCheckTicks = 2400;
    public static int RabbitSpawnMinDistanceFromNpc = 3;
    public static int RabbitFleeRadiusTiles = 2;
    public static float RabbitHopChance = 0.2f;
    // Spec 29F.2: контакт почти всегда = добыча — промах и так стоит спука
    // (краб исчезает из виду) плюс кулдауна Hunt; 0.5 сверху делал охоту
    // лотереей, которую пинг-понг погони почти никогда не доигрывал.
    public static float RabbitKillChance = 0.75f;
    public static int RabbitSpookTicks = 150;
    // Выпад копья в долях HexRadius (метрическая часть RabbitSystem-овой
    // мерки «достаю»; топологическая — соседство узлов, как CanStrike).
    // FleeHop сдвигает краба на ОДИН узел, т.е. сильно меньше этого выпада.
    public static float RabbitSpearReachHexFraction = 1.0f;
    // Bow hunt: chance an arrow connects, and chance the arrow is recovered
    // from the kill (§54: the kill drops a carcass, not instant loot).
    public static float RabbitBowHitChance = 0.6f;
    public static float ArrowRecoverChance = 0.4f;

    public static int MaxSharks = 2;

    // ─────────────────────────────────────────────────────────────
    // §147: виртуальные мобы (слоты-патрули).
    // ─────────────────────────────────────────────────────────────
    // 0 = механика выключена целиком, работает старый амбиентный спавнер.
    // >0 = слотовый спавнер ЗАМЕНЯЕТ амбиентный (они не смешиваются; гости
    // ночного рейда §46 ортогональны и приходят поверх в любом режиме).
    // ⭐ §147.6: включено и в режиме 0 (решение игрока) — прежние популяции
    // (2 волка, 4 краба, 2 акулы) стали слотами. Это ОСОЗНАННЫЙ сдвиг
    // поведения мира: спящий волк не охотится, пока рядом никого нет;
    // эталон golden trace принят заново этим же коммитом.
    public static int WolfSlots = 2;
    public static int CrabSlots = 4;
    public static int SharkSlots = 2;

    // §146.6-парные ручки большого острова (селекторы ниже).
    public static int BigIslandWolfSlots = 12;
    public static int BigIslandCrabSlots = 24;
    public static int BigIslandSharkSlots = 12;

    // §146.9: roughly twice the land/water area of BigIsland.
    public static int HugeIslandWolfSlots = 24;
    public static int HugeIslandCrabSlots = 48;
    public static int HugeIslandSharkSlots = 24;

    // §147.3: радиус материализации — это радиус СУЩЕСТВОВАНИЯ, не агра.
    // 8 ≥ любого реалистичного радиуса восприятия (§125): волк становится
    // реальным до того, как девушка его «заметила бы», и §62-тревога
    // срабатывает как раньше. Настоящий агр (2) применяет обычный RunDog.
    public static int MobMaterializeRadiusTiles = 8;

    // Крабий радиус больше радиуса охотничьего запроса (4): NPC физически
    // не может взять в цель виртуального краба.
    public static int CrabMaterializeRadiusTiles = 5;

    public static int MobPatrolRadiusTiles = 3;

    // Тиков на один отрезок маршрута превью. Отрезки короткие (~1 тайл,
    // MobSlots.BakeRing), так что 12 тиков (3 с) — неторопливый шаг зверя,
    // а не волчок по кольцу (замечание игрока).
    public static int MobPreviewSegmentTicks = 12;

    // Шанс простоять отрезок на месте. Независимые броски по эпохам дают
    // естественные серии: прошёл — постоял — прошёл.
    public static float MobPreviewPauseChance = 0.45f;

    // Селекторы по режиму — в MobSlots (не здесь: гейт «у ручки есть
    // читатель» не считает Balance/ читателем самого себя).
}

}
