using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// §72 Враг-человек: чужак живёт на острове своим лагерем и охотится на
    /// девушек. Оппортунист — идёт на убийство, только когда расклад его:
    /// жертва одна, слабая или спит, а сам он цел и вооружён. Девушки первыми
    /// не нападают: видят ⚠️, обходят его гекс, а как ударил — сбегаются все.
    /// Поле ассета = camelCase одноимённого статика Spec72.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/Outsider", fileName = "OutsiderBalance")]
    [MirrorTarget(typeof(Spec72))]
    public sealed class OutsiderBalanceConfig : ScriptableObject
    {
        [Header("Общее (§72)")]
        [Tooltip("Выключить — фракций как будто нет: все снова свои, поведение возвращается к до-§72 байт-в-байт.")]
        public bool enabled = false;
        [Tooltip("Селить чужака в мире. Отдельный флаг от «включено», чтобы соак мог развести влияние правил и влияние лишнего тела.")]
        public bool spawnOutsider = false;

        [Header("Его лагерь")]
        [Tooltip("Минимум гексов от очага колонии до его стоянки — «другой конец острова».")]
        [Range(4, 16)] public int outsiderCampMinDistanceTiles = 8;
        [Tooltip("Радиус, в котором выживший стартует со знанием построек СВОЕГО лагеря. Чужие лагеря он не знает.")]
        [Range(1, 10)] public int campKnowledgeRadiusTiles = 4;
        [Tooltip("Насколько широк лагерь: в этом радиусе от якоря очаг и мебель считаются «нашими».")]
        [Range(2, 12)] public int maxCampRadiusTiles = 6;
        [Tooltip("Нижняя граница черты сострадания чужака (у колонии диапазон §53 начинается с 0.35).")]
        [Range(0f, 1f)] public float outsiderCompassionMin = 0f;
        [Tooltip("Верхняя граница черты сострадания чужака. Сострадательный налётчик не налетал бы.")]
        [Range(0f, 1f)] public float outsiderCompassionMax = 0.25f;
        [Tooltip("Он сходит на берег с ножом. У одиночки нет разделения труда колонии, а охота вообще гейтится на настоящем оружии.")]
        public bool outsiderStartsArmed = true;

        [Header("Охота")]
        [Tooltip("Радиус, в котором он вообще высматривает жертву, тайлы.")]
        [Range(2, 20)] public int raidScanRadiusTiles = 8;
        [Tooltip("Сколько подруг рядом с жертвой он ещё терпит. 0 — только полные одиночки.")]
        [Range(0, 4)] public int raidMaxVictimAllies = 1;
        [Tooltip("Радиус, в котором считаются подруги жертвы.")]
        [Range(1, 8)] public int raidIsolationRadiusTiles = 3;
        [Tooltip("Сколько подруг полностью обнуляют «одиночество» жертвы.")]
        [Range(1, 6)] public int raidCrowdCount = 2;
        [Tooltip("Вес одиночества жертвы в оценке добычи.")]
        [Range(0f, 1f)] public float raidWeightIsolation = 0.4f;
        [Tooltip("Вес слабости жертвы (здоровье, худшая часть тела).")]
        [Range(0f, 1f)] public float raidWeightWeakness = 0.3f;
        [Tooltip("Вес беспомощности: спит, без сознания, лежит.")]
        [Range(0f, 1f)] public float raidWeightHelpless = 0.2f;
        [Tooltip("Вес близости жертвы.")]
        [Range(0f, 1f)] public float raidWeightProximity = 0.1f;
        [Tooltip("Минимальная оценка добычи, ниже которой он даже не заявляется. 0.45 пропускает ОДИНОЧКУ рядом, даже целую — это и есть «выбивает отставших».")]
        [Range(0f, 1f)] public float raidOpportunityFloor = 0.45f;
        [Tooltip("Оценка, ниже которой он бросает уже начатую охоту (подруги подошли, жертва оправилась).")]
        [Range(0f, 1f)] public float raidAbandonOpportunity = 0.3f;
        [Tooltip("Идти искать, когда рядом никого. Без этого лагерь слишком далеко и охота не срабатывает ни разу.")]
        public bool prowlEnabled = true;
        [Tooltip("На таком удалении от их лагеря он останавливается и высматривает добычу.")]
        [Range(2, 10)] public int prowlArrivedTiles = 5;
        [Tooltip("Здоровье, при котором «слабость» жертвы засчитывается полностью. Это ВЕС, а не запрет: одиночку он берёт и целой.")]
        [Range(0f, 1f)] public float raidVictimHealthCeiling = 0.75f;
        [Tooltip("Своё здоровье, ниже которого он не охотится, а зализывает раны.")]
        [Range(0f, 1f)] public float raidSelfHealthFloor = 0.7f;
        [Tooltip("Худшая часть тела, ниже которой не охотится. Отдельно от общего здоровья: после одной встречи с собакой конечность держится ниже 0.7 сутками и глушила охоту напрочь.")]
        [Range(0f, 1f)] public float raidSelfWorstPartFloor = 0.7f;
        [Tooltip("Голод/жажда выше этого — сначала выживание. Это и держит нужды выше охоты.")]
        [Range(0f, 1f)] public float raidSelfNeedCeiling = 0.6f;
        [Tooltip("Энергия ниже этой — слишком устал охотиться.")]
        [Range(0f, 1f)] public float raidSelfEnergyFloor = 0.3f;
        [Tooltip("Базовый вес цели в аукционе. Выше, чем кажется нужным: у одиночки Sit доходит до 0.85, и на 0.15 охота не выигрывала НИ РАЗУ за 10 дней.")]
        [Range(0f, 1f)] public float raidBaseScore = 0.7f;
        [Tooltip("Прибавка к весу за качество добычи: итог = база + это × оценка.")]
        [Range(0f, 1f)] public float raidOpportunityGain = 0.5f;
        [Tooltip("Урон за удар. Множитель поверх урона оружия — единственная ручка, которой можно смягчить ЕГО, не трогая оружие девушек.")]
        [Range(0.1f, 2f)] public float raidStrikeDamageMult = 1.35f;
        [Tooltip("Он даёт бой только с этого дня — колония успевает завести огонь, хижину и нож.")]
        [Range(0, 20)] public int raidGraceDays = 5;
        [Tooltip("Пауза между налётами, тиков. Главная ручка против мясорубки.")]
        [Range(0, 9600)] public int raidCooldownTicks = 1200;
        [Tooltip("Держать цель не дольше этого, тиков.")]
        [Range(120f, 2400f)] public float raidPursuitMaxTicks = 600f;
        [Tooltip("Стоит на месте столько тиков — погоня заглохла, бросает.")]
        [Range(20f, 600f)] public float raidStallGiveUpTicks = 60f;
        [Tooltip("Гол-лок на охоту, чтобы он не переторговывал цель каждый средний тик.")]
        [Range(0, 1200)] public int raidLockTicks = 240;
        [Tooltip("Бросает погоню у двери — как собака перед хижиной (§29C.4A).")]
        public bool raidRespectsSanctuary = true;
        [Tooltip("Своё здоровье, ниже которого он отступает.")]
        [Range(0f, 1f)] public float raidFleeHealth = 0.55f;
        [Tooltip("Здоровье жертвы, ниже которого она бежит.")]
        [Range(0f, 1f)] public float raidVictimFleeHealth = 0.6f;
        [Tooltip("Столько защитниц вокруг — и он отступает.")]
        [Range(1, 5)] public int raidBreakOffDefenders = 3;

        [Header("Оборона колонии")]
        [Tooltip("Против ЧУЖАКА поднимать всю фракцию в радиусе без порога симпатии — в первые дни её ещё нет, а «дать отпор сплочённо» нужно именно тогда.")]
        public bool rallyIgnoresAffinityVsOutsider = true;
        [Tooltip("С какой дистанции девушки замечают чужака, тайлы.")]
        [Range(2, 15)] public int spotStrangerRadiusTiles = 6;
        [Tooltip("Как часто повторять ⚠️ на одного и того же чужака, тиков.")]
        [Range(60, 2400)] public int strangerCueCooldownTicks = 300;
        [Tooltip("Радиус кольца обхода вокруг чужака, тайлы (у волка §62 — 2).")]
        [Range(1, 6)] public int dangerRingTiles = 3;
        [Tooltip("Прибавка к комфорту свидетелям, когда чужак погиб. Победа должна читаться как победа, а не как траур.")]
        [Range(0f, 1f)] public float enemyDeathRelief = 0.15f;
    }
}
