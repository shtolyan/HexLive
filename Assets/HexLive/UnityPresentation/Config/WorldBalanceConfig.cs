using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Мир и дикая природа: суточный цикл и тени, штормы, сушка, топливо
    /// костра, гниение фруктов (WorldBalance) + директор популяций — собаки,
    /// крабы (WildlifeBalance; БОЕВЫЕ статы мобов остаются в
    /// MobConfig-ассетах). Поле ассета = camelCase одноимённого статика.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/World", fileName = "WorldBalance")]
    [MirrorTarget(typeof(WorldBalance))]
    [MirrorTarget(typeof(WildlifeBalance))]
    public sealed class WorldBalanceConfig : ScriptableObject
    {
        [Header("Стартовый состав")]
        [Tooltip("Сколько девушек на старте. Мест в доме ограниченное число, выше него значение обрезается.")]
        [Range(0, 10)] public int colonistCount = 3;

        [Header("Люди и пополнение (§132)")]
        [Tooltip("Жёсткий потолок всех ЖИВЫХ NPC обеих сторон. Трупы не считаются. 10 = два лагеря по 5 при квотах ниже.")]
        [Range(0, 30)] public int maxLivingNpcs = 10;
        [Tooltip("Максимум живых девушек в лагере игрока. Потолок не убирает уже живущих, а только запрещает новых.")]
        [Range(0, 15)] public int maxColonyNpcs = 5;
        [Tooltip("Максимум живых людей в лагере дикарей. Стартовый чужак входит в эту квоту.")]
        [Range(0, 15)] public int maxOutsiderNpcs = 5;
        [Tooltip("Раз во сколько календарных дней в лагерь игрока приходит одна новая девушка (7 = дни 7, 14, 21…). 0 = выключить.")]
        [Range(0, 60)] public int colonyArrivalIntervalDays = 7;

        [Header("Суточный цикл и тени")]
        [Tooltip("Полный цикл день-ночь в тиках — ВИЗУАЛЬНЫЕ ЧАСЫ: солнце, тени, небо, фазы суток, температура, UV, счётчик «День N». 24000 = 100 реальных минут при 0.25 с/тик.")]
        [Range(600, 48000)] public int dayLengthTicks = 24000;
        [Tooltip("ИГРОВОЙ ПЕРИОД сеяных бросков «раз в день» (дождь, шторм, подарок прибоя, рейд стаи). Раньше совпадал с сутками; часы растянули, а частота этих событий по реальному времени должна остаться прежней — поэтому они считаются от него, а не от суток.")]
        [Range(600, 9600)] public int eventCycleTicks = 2400;
        [Tooltip("Сколько тайлов марширует теневой луч (§33).")]
        [Range(1, 20)] public int shadowRaySteps = 7;
        [Tooltip("Высота ступени рельефа в мировых единицах (как у рендера).")]
        [Range(0.1f, 2f)] public float elevationWorldStep = 0.55f;
        [Tooltip("Сколько виртуальных ступеней добавляют стены помещения (крыша = тень).")]
        [Range(0f, 6f)] public float canopyVirtualSteps = 2f;

        [Header("Штормы (§40)")]
        [Tooltip("Шанс шторма за один игровой период (eventCycleTicks).")]
        [Range(0f, 1f)] public float stormChancePerDay = 0.08f;
        [Tooltip("Сколько брёвен плота смывает один шторм.")]
        [Range(0, 10)] public int stormRaftLogLoss = 2;
        [Tooltip("В какой момент игрового периода приходит штормовая волна (тики от начала периода).")]
        [Range(0, 2400)] public int stormSurgeOffsetTicks = 1600;

        [Header("Влажность / костёр / фрукты")]
        [Tooltip("Базовая скорость сушки промокшей надетой вещи за медленный тик (без костра).")]
        [Range(0f, 0.2f)] public float moistureDryBase = 0.02f;
        [Tooltip("Сколько топлива сжигает горящий костёр за медленный тик (до множителя каменного кольца §54.14).")]
        [Range(1f, 60f)] public float fireBurnPerSlowTick = 16f;
        [Tooltip("Через сколько тиков сгнивает неподобранный фрукт на земле.")]
        [Range(300, 9600)] public int fruitRotTicks = 2400;
        [Tooltip("§29A r2: фрукт падает не ближе стольких радиусов ствола (ObstacleRadius) от дерева. Один радиус — непроходимое кольцо, второй — отступ падения. 0 = без отступа.")]
        [Range(0f, 4f)] public float fruitDropClearanceFactor = 2f;

        [Header("Прибой приносит одежду (§63)")]
        [Tooltip("Раз в сколько полных игровых суток в 06:00 прибой приносит по одной случайной женской вещи на каждую живую девушку колонии.")]
        [Range(1, 30)] public int surfGiftIntervalDays = 5;

        [Header("Собаки — директор стаи (§46)")]
        [Tooltip("Максимум собак на острове одновременно.")]
        [Range(0, 10)] public int maxDogs = 3;
        [Tooltip("Как часто проверяется респаун стаи, в тиках (2400 тиков = 10 реальных минут). ВНИМАНИЕ: этот дефолт (3600) расходится с WildlifeBalance/ассетом (7200) — предсуществующий дрейф, правится отдельно.")]
        [Range(600, 9600)] public int dogRespawnCheckTicks = 3600;
        [Tooltip("В какой момент игрового периода стартует рейд стаи (тики от начала периода; 1800 из 2400 = «на закате» периода).")]
        [Range(0, 2400)] public int raidDuskOffsetTicks = 1800;
        [Tooltip("§46 v4: сколько тиков рейдовые «гостьи» сверх потолка стаи слоняются у лагеря, прежде чем уйти сами.")]
        [Range(300, 9600)] public int raidLingerTicks = 2400;
        [Tooltip("§46 v4: предохранитель — рейдовая собака уходит через столько тиков НЕЗАВИСИМО от зрителей, иначе «временная» стая стала бы вечной.")]
        [Range(300, 9600)] public int raidDepartureBackstopTicks = 2400;
        [Tooltip("Минимальная дистанция спауна собаки от NPC, тайлы.")]
        [Range(1, 15)] public int dogSpawnMinDistanceFromNpc = 5;
        [Tooltip("Сколько тиков ПОДРЯД погоня не сдвигает собаку (нет проходимого пути к жертве), прежде чем она бросает цель и уходит бродить. Мелек сбрасывает счётчик.")]
        [Range(40, 1200)] public int dogChaseStallGiveUpTicks = 200;
        [Tooltip("Сколько тиков после брошенной безнадёжной погони собака игнорирует добычу (успевает реально уйти от лагеря).")]
        [Range(0, 4800)] public int dogHuntCooldownTicks = 600;

        [Header("Крабы/кролики (31C)")]
        [Tooltip("Максимум крабов одновременно.")]
        [Range(0, 12)] public int maxRabbits = 4;
        [Tooltip("Как часто проверяется респаун (плодятся быстро).")]
        [Range(300, 9600)] public int rabbitRespawnCheckTicks = 2400;
        [Tooltip("Минимальная дистанция спауна от NPC, тайлы.")]
        [Range(1, 10)] public int rabbitSpawnMinDistanceFromNpc = 3;
        [Tooltip("Радиус, в котором краб пугается и удирает, тайлы.")]
        [Range(1, 6)] public int rabbitFleeRadiusTiles = 2;
        [Tooltip("Шанс скачка за тик (снуют, а не бегут).")]
        [Range(0f, 1f)] public float rabbitHopChance = 0.2f;
        [Tooltip("Шанс добить краба вручную за подход.")]
        [Range(0f, 1f)] public float rabbitKillChance = 0.75f;
        [Tooltip("Сколько тиков краб напуган после промаха.")]
        [Range(0, 600)] public int rabbitSpookTicks = 150;
        [Tooltip("Выпад копья по крабу, в долях радиуса гекса (сверх соседства узлов).")]
        [Range(0f, 3f)] public float rabbitSpearReachHexFraction = 1f;
        [Tooltip("Шанс попадания стрелой из лука.")]
        [Range(0f, 1f)] public float rabbitBowHitChance = 0.6f;
        [Tooltip("Шанс вытащить стрелу из добычи обратно.")]
        [Range(0f, 1f)] public float arrowRecoverChance = 0.4f;

        // §146/§147: ручки, добавленные кодом раньше зеркала, — долг покрытия,
        // который блокировал экспорт SimData (Aug-2026). Дефолты = значения
        // из кода на момент добавления.
        [Header("Режимы мира (§146)")]
        [Tooltip("§146.6: потолок всех живых NPC на большом острове.")]
        [Range(0, 60)] public int bigIslandMaxLivingNpcs = 24;
        [Tooltip("§146.6: потолок живых в одном лагере на большом острове.")]
        [Range(0, 20)] public int bigIslandMaxCampNpcs = 6;
        [Tooltip("§146.9: потолок всех живых NPC на огромном острове.")]
        [Range(0, 80)] public int hugeIslandMaxLivingNpcs = 42;
        [Tooltip("§146.9: потолок живых в одном из шести лагерей.")]
        [Range(0, 20)] public int hugeIslandMaxCampNpcs = 6;
        [Tooltip("§35.5: множитель естественной сушки ОДЕЖДЫ (мир без костра/солнца).")]
        [Range(0f, 1f)] public float clothingNaturalDryMultiplier = 0.1f;

        [Header("Виртуальные мобы — слоты-патрули (§147)")]
        [Tooltip("§147: слоты волков в обычном режиме (Feud). 0 = слоты выключены, работает старый амбиентный спавнер.")]
        [Range(0, 30)] public int wolfSlots = 2;
        [Tooltip("§147: слоты крабов в обычном режиме.")]
        [Range(0, 60)] public int crabSlots = 4;
        [Tooltip("§146.7: слоты волков на большом острове.")]
        [Range(0, 30)] public int bigIslandWolfSlots = 12;
        [Tooltip("§146.7: слоты крабов на большом острове.")]
        [Range(0, 60)] public int bigIslandCrabSlots = 24;
        [Tooltip("§146.9: слоты волков на огромном острове.")]
        [Range(0, 60)] public int hugeIslandWolfSlots = 24;
        [Tooltip("§146.9: слоты крабов на огромном острове.")]
        [Range(0, 100)] public int hugeIslandCrabSlots = 48;
        [Tooltip("§147.3: радиус материализации зверя из превью, тайлы от ближайшей девушки.")]
        [Range(1, 20)] public int mobMaterializeRadiusTiles = 8;
        [Tooltip("§147.3: радиус материализации краба, тайлы.")]
        [Range(1, 20)] public int crabMaterializeRadiusTiles = 5;
        [Tooltip("§147.1: радиус патрульного кольца слота от его дома, тайлы.")]
        [Range(1, 12)] public int mobPatrolRadiusTiles = 3;
        [Tooltip("§147.2: длительность одного сегмента прогулки превью, тики.")]
        [Range(2, 60)] public int mobPreviewSegmentTicks = 12;
        [Tooltip("§147.2: шанс паузы (стоит, нюхает) вместо шага на сегменте превью.")]
        [Range(0f, 1f)] public float mobPreviewPauseChance = 0.45f;
    }
}
