using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Мир и дикая природа: суточный цикл и тени, штормы, сушка, топливо
    /// костра, гниение фруктов (WorldBalance) + директор популяций — собаки,
    /// крабы, акулы (WildlifeBalance; БОЕВЫЕ статы мобов остаются в
    /// MobConfig-ассетах). Поле ассета = camelCase одноимённого статика.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/World", fileName = "WorldBalance")]
    [MirrorTarget(typeof(WorldBalance))]
    [MirrorTarget(typeof(WildlifeBalance))]
    public sealed class WorldBalanceConfig : ScriptableObject
    {
        [Header("Суточный цикл и тени")]
        [Tooltip("Сколько девушек на старте. Мест в доме ограниченное число, выше него значение обрезается.")]
        [Range(1, 8)] public int colonistCount = 3;
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

        [Header("Прибой приносит одежду (§63)")]
        [Tooltip("Шанс за один игровой период (eventCycleTicks), что прибой вынесет случайную вещь на берег (0.35 ≈ 2-3 вещи за 7 периодов).")]
        [Range(0f, 1f)] public float surfGiftChancePerDay = 0.35f;
        [Tooltip("В какой момент игрового периода прилив оставляет вещь (тики от начала периода, кратно 16).")]
        [Range(0, 2400)] public int surfGiftOffsetTicks = 800;

        [Header("Собаки — директор стаи (§46)")]
        [Tooltip("Максимум собак на острове одновременно.")]
        [Range(0, 10)] public int maxDogs = 3;
        [Tooltip("Как часто проверяется респаун стаи, в тиках (2400 тиков = 10 реальных минут). ВНИМАНИЕ: этот дефолт (3600) расходится с WildlifeBalance/ассетом (7200) — предсуществующий дрейф, правится отдельно.")]
        [Range(600, 9600)] public int dogRespawnCheckTicks = 3600;
        [Tooltip("В какой момент игрового периода стартует рейд стаи (тики от начала периода; 1800 из 2400 = «на закате» периода).")]
        [Range(0, 2400)] public int raidDuskOffsetTicks = 1800;
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

        [Header("Акулы")]
        [Tooltip("Максимум акул одновременно.")]
        [Range(0, 6)] public int maxSharks = 2;
    }
}
