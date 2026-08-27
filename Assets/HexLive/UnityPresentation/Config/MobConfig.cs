using HexLive.Simulation.Content;
using UnityEngine;
using UnityEngine.Serialization;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// One ScriptableObject asset PER MOB — the tunable combat/behaviour sheet
    /// for a wolf or any future creature. Drop the asset under
    /// <c>Resources/HexLive/Mobs/</c>; <see cref="MobTuning"/> loads every one
    /// at startup and overrides the matching <see cref="MobCatalog"/> entry the
    /// simulation reads.
    ///
    /// Adding a mob = create one asset (HexLive ▸ Mob Config), set its Mob Id to
    /// match a <see cref="MobIds"/> constant, fill the numbers. No new fields on
    /// any shared config file.
    /// </summary>
    /// <summary>Виды мобов (enum-обёртка над MobIds).</summary>
    public enum MobKind
    {
        Dog = 0,
        Crab = 2,
    }

    [CreateAssetMenu(menuName = "HexLive/Mob Config", fileName = "MobConfig")]
    public sealed class MobConfig : ScriptableObject
    {
        // The Resources folder scanned by MobTuning.LoadAndApply.
        public const string ResourceFolder = "HexLive/Mobs";

        [Tooltip("Вид моба (enum — id разрешается кодом, строки не пишутся).")]
        public MobKind kind = MobKind.Dog;

        /// <summary>Sim id, derived from the enum — the serializer takes the
        /// id itself; nobody types "dog" by hand.</summary>
        public string MobId => kind switch
        {
            MobKind.Crab => MobIds.Crab,
            _ => MobIds.Dog,
        };

        [Header("Модель")]
        [Tooltip("Префаб моба — прямой ссылкой.")]
        public GameObject prefab;
        [Tooltip("ЛЕГАСИ-фолбэк: Resources-путь, если ссылка пуста.")]
        public string prefabResourcePath = "";

        // ── Вид (презентация): читается MobView/MobWoundPainter, в симуляцию
        // НЕ попадает (ToStats её не трогает). Дефолты = волк.
        [Header("Вид / анимация (презентация)")]
        [Tooltip("Длина тела на хексе как доля радиуса хекса (0.84 = волк, краб мелкий ~0.24).")]
        [Range(0.05f, 2f)] public float footprintFraction = 0.84f;
        [Tooltip("Визуальный поворот тела вокруг Y относительно направления движения. 90° = животное движется боком.")]
        [Range(-180f, 180f)] public float visualYawOffsetDegrees;
        [Tooltip("Скорость земли, которую отражает клип ходьбы, в длинах тела/сек.")]
        [Range(0.2f, 5f)] public float walkStrideLengthsPerSecond = 1.1f;
        [Tooltip("Скорость земли, которую отражает клип бега, в длинах тела/сек.")]
        [Range(0.5f, 8f)] public float runStrideLengthsPerSecond = 2.6f;
        [Tooltip("Порог включения бега (длины тела/сек, с гистерезисом).")]
        [Range(0.5f, 6f)] public float runEnterLengthsPerSecond = 1.8f;
        [Tooltip("Порог выключения бега (ниже порога входа — чтобы не мигало).")]
        [Range(0.3f, 5f)] public float runExitLengthsPerSecond = 1.4f;
        [Tooltip("Брызги крови при полученном уроне.")]
        public bool bloodSplash = true;
        [Tooltip("Перманентные раны-декали на шкуре (стамп-технология NPC).")]
        public bool woundStamps = true;
        [Tooltip("Сколько ран при почти нулевом HP (раны = ceil(потерянное HP × N)).")]
        [Range(1, 24)] public int maxWoundStamps = 8;

        [Header("Бой")]
        [Tooltip("Здоровье моба на спавне.")]
        [Range(0.3f, 6f)] public float maxHealth = 1.8f;
        [Tooltip("Урон одной атаки — укус, клевок, удар лапой (до брони цели).")]
        [FormerlySerializedAs("biteDamage")]
        [Range(0f, 0.5f)] public float attackDamage = 0.09f;
        [Tooltip("Профиль cut/blunt задан на этом ассете. Выкл = профиль MobCatalog.")]
        public bool damageProfileAuthored;
        [Range(0f, 1f)] public float cutFraction;
        [Range(0f, 2f)] public float bloodLossMultiplier;
        [Tooltip("Замах атаки, сек — урон падает в конце (начатая атака всегда попадает).")]
        [Range(0.05f, 2f)] public float attackWindupSeconds = 0.1f;
        [Tooltip("Перезарядка между атаками, сек (считается ОТ момента удара).")]
        [Range(0.2f, 4f)] public float attackCooldownSeconds = 0.8f;

        [Header("Поведение / движение")]
        [Tooltip("Радиус агро (тайлы).")]
        [Range(1, 8)] public int aggroRadiusTiles = 2;
        [Tooltip("Вероятность блуждания за медиум-тик.")]
        [Range(0f, 1f)] public float roamChance = 0.2f;
        [Tooltip("Шагов-джанкшенов за медиум-тик в погоне (1 = как блуждание; больше = реально бежит).")]
        [Range(1, 6)] public int chaseStepsPerTick = 3;
        [Tooltip("За сколько сим-секунд рендер-позиция доезжает до логической (сглаживание рывков).")]
        [Range(0.25f, 3f)] public float glideSegmentSeconds = 1.0f;
        [Tooltip("Дальше этой дистанции — телепорт (спавн/загрузка), не езда.")]
        [Range(1f, 20f)] public float glideSnapDistance = 6.0f;
        [Tooltip("Боевая стойка: рендер-глайд не подвозит моба к цели ближе этой дистанции (wu) — пара стоит друг напротив друга, а не друг в друге. 0 = выкл.")]
        [Range(0f, 3f)] public float meleeHoldDistance = 0.9f;
        [Tooltip("§106: среда, в которой работает атака. Land — зверь дремлет против пловца; Water — только вода; Amphibious — обе.")]
        public AttackMedium attackMediums = AttackMedium.Land;

        [Header("Стайный налёт (0 = одиночка)")]
        [Tooltip("Вероятность ночного налёта стаи в день.")]
        [Range(0f, 1f)] public float raidChancePerDay = 0f;
        [Tooltip("Сколько мобов в ночном налёте.")]
        [Range(0, 8)] public int raidPackSize = 0;

        public MobStats ToStats()
        {
            var defaults = MobCatalog.Defaults.TryGetValue(MobId, out var builtIn)
                ? builtIn
                : MobStats.NeutralDefault(MobId);
            return new MobStats
            {
            Id = MobId,
            MaxHealth = maxHealth,
            AttackDamage = attackDamage,
            CutFraction = damageProfileAuthored ? cutFraction : defaults.CutFraction,
            BloodLossMultiplier = damageProfileAuthored
                ? bloodLossMultiplier
                : defaults.BloodLossMultiplier,
            AttackWindupSeconds = attackWindupSeconds,
            AttackCooldownSeconds = attackCooldownSeconds,
            AggroRadiusTiles = aggroRadiusTiles,
            RoamChance = roamChance,
            ChaseStepsPerTick = chaseStepsPerTick,
            GlideSegmentSeconds = glideSegmentSeconds,
            GlideSnapDistance = glideSnapDistance,
            MeleeHoldDistance = meleeHoldDistance,
            AttackMediums = attackMediums,
            RaidChancePerDay = raidChancePerDay,
            RaidPackSize = raidPackSize,
            };
        }

        // Bake the current catalog defaults back onto this asset (editor helper).
        public void PullFromDefaults()
        {
            var d = MobCatalog.Defaults;
            if (!d.TryGetValue(MobId, out var s))
            {
                return;
            }

            maxHealth = s.MaxHealth;
            attackDamage = s.AttackDamage;
            cutFraction = s.CutFraction;
            bloodLossMultiplier = s.BloodLossMultiplier;
            damageProfileAuthored = true;
            attackWindupSeconds = s.AttackWindupSeconds;
            attackCooldownSeconds = s.AttackCooldownSeconds;
            aggroRadiusTiles = s.AggroRadiusTiles;
            roamChance = s.RoamChance;
            chaseStepsPerTick = s.ChaseStepsPerTick;
            glideSegmentSeconds = s.GlideSegmentSeconds;
            glideSnapDistance = s.GlideSnapDistance;
            meleeHoldDistance = s.MeleeHoldDistance;
            attackMediums = s.AttackMediums;
            raidChancePerDay = s.RaidChancePerDay;
            raidPackSize = s.RaidPackSize;
        }
    }
}
