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
        [Tooltip("Полный цикл день-ночь в тиках. ЕДИНИЦА мирового времени — от неё считаются рейды, штормы, история.")]
        [Range(600, 9600)] public int dayLengthTicks = 2400;
        [Tooltip("Сколько тайлов марширует теневой луч (§33).")]
        [Range(1, 20)] public int shadowRaySteps = 7;
        [Tooltip("Высота ступени рельефа в мировых единицах (как у рендера).")]
        [Range(0.1f, 2f)] public float elevationWorldStep = 0.55f;
        [Tooltip("Сколько виртуальных ступеней добавляют стены помещения (крыша = тень).")]
        [Range(0f, 6f)] public float canopyVirtualSteps = 2f;

        [Header("Штормы (§40)")]
        [Tooltip("Шанс шторма в день.")]
        [Range(0f, 1f)] public float stormChancePerDay = 0.08f;
        [Tooltip("Сколько брёвен плота смывает один шторм.")]
        [Range(0, 10)] public int stormRaftLogLoss = 2;
        [Tooltip("За сколько тиков до сумерек приходит штормовая волна.")]
        [Range(0, 2400)] public int stormSurgeOffsetTicks = 1600;

        [Header("Влажность / костёр / фрукты")]
        [Tooltip("Базовая скорость сушки промокшей надетой вещи за медленный тик (без костра).")]
        [Range(0f, 0.2f)] public float moistureDryBase = 0.02f;
        [Tooltip("Сколько топлива сжигает горящий костёр за медленный тик (до множителя каменного кольца §54.14).")]
        [Range(1f, 60f)] public float fireBurnPerSlowTick = 16f;
        [Tooltip("Через сколько тиков сгнивает неподобранный фрукт на земле.")]
        [Range(300, 9600)] public int fruitRotTicks = 2400;

        [Header("Прибой приносит одежду (§63)")]
        [Tooltip("Шанс в день, что прибой вынесет случайную вещь на берег (0.35 ≈ 2-3 вещи за 7 дней).")]
        [Range(0f, 1f)] public float surfGiftChancePerDay = 0.35f;
        [Tooltip("В какой момент дня прилив оставляет вещь (тики от начала суток, кратно 16).")]
        [Range(0, 2400)] public int surfGiftOffsetTicks = 800;

        [Header("Собаки — директор стаи (§46)")]
        [Tooltip("Максимум собак на острове одновременно.")]
        [Range(0, 10)] public int maxDogs = 3;
        [Tooltip("Как часто проверяется респаун стаи (3600 = каждые 1.5 игровых дня).")]
        [Range(600, 9600)] public int dogRespawnCheckTicks = 3600;
        [Tooltip("За сколько тиков до сумерек стартует рейд стаи.")]
        [Range(0, 2400)] public int raidDuskOffsetTicks = 1800;
        [Tooltip("Минимальная дистанция спауна собаки от NPC, тайлы.")]
        [Range(1, 15)] public int dogSpawnMinDistanceFromNpc = 5;

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
        [Range(0f, 1f)] public float rabbitKillChance = 0.5f;
        [Tooltip("Сколько тиков краб напуган после промаха.")]
        [Range(0, 600)] public int rabbitSpookTicks = 150;
        [Tooltip("Шанс попадания стрелой из лука.")]
        [Range(0f, 1f)] public float rabbitBowHitChance = 0.6f;
        [Tooltip("Шанс вытащить стрелу из добычи обратно.")]
        [Range(0f, 1f)] public float arrowRecoverChance = 0.4f;

        [Header("Акулы")]
        [Tooltip("Максимум акул одновременно.")]
        [Range(0, 6)] public int maxSharks = 2;
    }
}
