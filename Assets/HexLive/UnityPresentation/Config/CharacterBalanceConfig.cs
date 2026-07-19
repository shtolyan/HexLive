using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Персонаж: нужды, пороги, здоровье/кровь/болезнь, еда/питьё, отдых/сон,
    /// температура/солнце, гигиена, выносливость/стресс, климат, раны, кома,
    /// cool-off и износ одежды — весь «человеческий» блок SimBalance.
    /// Поле ассета = camelCase одноимённого статика SimBalance; зеркалит
    /// <see cref="SimConfigMirror"/>, покрытие сторожит Validate Tuning Coverage.
    /// Значения по умолчанию = как в коде; тюнить только через ассет.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/Character", fileName = "CharacterBalance")]
    [MirrorTarget(typeof(SimBalance))]
    [MirrorTarget(typeof(AiBalance))]
    public sealed class CharacterBalanceConfig : ScriptableObject
    {
        [Header("Восприятие и решения (AiBalance)")]
        [Tooltip("Радиус восприятия в тайлах.")]
        [Range(1, 6)] public int perceptionRadiusTiles = 2;
        [Tooltip("Сколько тиков живёт пространственная память (виденные объекты/опасности).")]
        [Range(300, 9600)] public int memoryTtlTicks = 2400;
        [Tooltip("Свежевыигранная цель заперта столько тиков (анти-дребезг аукциона).")]
        [Range(0, 200)] public int goalLockTicks = 24;
        [Tooltip("Насколько лучше должна быть заявка, чтобы сломать замок цели досрочно.")]
        [Range(0f, 2f)] public float lockOverrideDelta = 0.5f;
        [Tooltip("Запас, с которым претендент перебивает текущую цель после замка.")]
        [Range(0f, 1f)] public float switchDelta = 0.15f;
        [Tooltip("Сколько тиков приглашённая ждёт начала разговора (§28.15).")]
        [Range(20, 600)] public int talkWaitTimeoutTicks = 120;

        [Header("Нужды — скорость (за медленный тик, ~150/день)")]
        [Tooltip("Сколько ГОЛОДА набегает за медленный тик. Больше = быстрее хочет есть.")]
        [Range(0f, 0.05f)] public float hungerRate = 0.0055f;
        [Tooltip("Сколько ЖАЖДЫ набегает за тик. Больше = быстрее хочет пить.")]
        [Range(0f, 0.05f)] public float thirstRate = 0.010f;
        [Tooltip("Сколько ЭНЕРГИИ тратится за тик бодрствования (~1 полоска/день).")]
        [Range(0f, 0.03f)] public float energyRate = 0.007f;
        [Tooltip("Сколько КОМФОРТА теряется за тик бодрствования (у костра — наоборот растёт).")]
        [Range(0f, 0.05f)] public float comfortRate = 0.01f;
        [Tooltip("Сколько СОЦИАЛА убывает за тик (одиночество).")]
        [Range(0f, 0.03f)] public float socialRate = 0.008f;
        [Tooltip("Добавка к жажде за единицу перегрева (пот). 0 = жара не сушит.")]
        [Range(0f, 1f)] public float sweatThirstFactor = 0.25f;

        [Header("Нужды — пороги действий")]
        [Tooltip("Голод, выше которого идёт собирать еду из мира (сдержанность: не тащит впрок).")]
        [Range(0f, 1f)] public float getFoodHungerThreshold = 0.35f;
        [Tooltip("Энергия, ниже которой можно спать днём (ночью спит и так).")]
        [Range(0f, 1f)] public float sleepEnergyThreshold = 0.45f;
        [Tooltip("Комфорт, ниже которого хочет присесть.")]
        [Range(0f, 1f)] public float sitComfortThreshold = 0.6f;
        [Tooltip("Голод/жажда, ВЫШЕ которых уже не рассиживается (дела важнее).")]
        [Range(0f, 1f)] public float sitNeedGate = 0.6f;
        [Tooltip("Голод, будящий спящую (просыпается поесть).")]
        [Range(0f, 1f)] public float sleepInterruptHunger = 0.6f;
        [Tooltip("Жажда, будящая спящую (просыпается попить).")]
        [Range(0f, 1f)] public float sleepInterruptThirst = 0.6f;
        [Tooltip("Холодовой дискомфорт, выше которого одевается.")]
        [Range(0f, 1f)] public float dressThermalThreshold = 0.45f;
        [Tooltip("Одевается только когда эффективная температура НИЖЕ этой (°C).")]
        [Range(0f, 30f)] public float dressColdTemp = 14f;
        [Tooltip("Не одевается дальше, если уже утеплена выше этого (тогда спасает костёр, а не тряпки).")]
        [Range(0f, 1f)] public float dressWarmthCeiling = 0.5f;

        [Header("Голод/жажда — истощение и смерть")]
        [Tooltip("Порог появления оценки «Голодает/Обезвожена» (аварийный буст цели).")]
        [Range(0.5f, 1f)] public float starvingEnterThreshold = 0.85f;
        [Tooltip("Порог снятия этой оценки (гистерезис).")]
        [Range(0.3f, 0.9f)] public float starvingClearThreshold = 0.60f;
        [Tooltip("Величина аварийного буста цели при истощении.")]
        [Range(0f, 2f)] public float starvingBoost = 1f;
        [Tooltip("Выше этого голода/жажды начинает течь HP (смертельный канал застрявшего).")]
        [Range(0.7f, 1f)] public float starveDeathThreshold = 0.95f;
        [Tooltip("Урон HP за тик, когда И голод И жажда на максимуме.")]
        [Range(0f, 0.15f)] public float starveDamageBoth = 0.05f;
        [Tooltip("Урон HP за тик, когда лишь одно (голод ИЛИ жажда) на максимуме.")]
        [Range(0f, 0.15f)] public float starveDamageOne = 0.03f;

        [Header("Лечение / регенерация")]
        [Tooltip("Естественное лечение и восполнение крови идут только пока голод ниже этого.")]
        [Range(0f, 1f)] public float healHungerGate = 0.6f;
        [Tooltip("Сколько HP восстанавливается на часть тела за тик (сытой и не раненой).")]
        [Range(0f, 0.02f)] public float healthRegenPerTick = 0.0030f;

        [Header("Кровь / первая помощь")]
        [Tooltip("Скорость кровопотери = (0.4 − худшая часть) × это. Больше = быстрее истекает.")]
        [Range(0f, 0.3f)] public float bleedRateFactor = 0.09f;
        [Tooltip("Сколько крови возвращается за тик (сытой; ×3 во сне, ×2 у костра).")]
        [Range(0f, 0.03f)] public float bloodRefillPerTick = 0.005f;
        [Tooltip("Кровь, ниже которой авто-бинт из пакета перевязывает худшую рану.")]
        [Range(0f, 1f)] public float bandageBloodThreshold = 0.35f;

        [Header("Болезнь (сырая вода)")]
        [Tooltip("Шанс заболеть с одного глотка сырой воды.")]
        [Range(0f, 1f)] public float rawWaterSickChance = 0.15f;
        [Tooltip("Длительность недомогания (🤢) в тиках (~6 игр. часов).")]
        [Range(0, 2400)] public int sicknessDurationTicks = 600;
        [Tooltip("Урон торсу в «бюджет» за один приступ.")]
        [Range(0f, 0.3f)] public float sicknessDamagePerBout = 0.08f;
        [Tooltip("Потолок бюджета урона от болезни (чтобы не измолотить торс в ноль).")]
        [Range(0f, 0.5f)] public float sicknessDamageBudgetCap = 0.16f;
        [Tooltip("Скорость выплаты бюджета урона (урон торсу за тик).")]
        [Range(0f, 0.02f)] public float sickTorsoPerSlowTick = 0.002f;
        [Tooltip("Ниже этого торс болезнь не грызёт.")]
        [Range(0f, 1f)] public float sickTorsoFloor = 0.15f;
        [Tooltip("Сколько комфорта отнимает недомогание за тик.")]
        [Range(0f, 0.03f)] public float sickComfortPerSlowTick = 0.006f;

        [Header("Питьё — сколько восстанавливает")]
        [Tooltip("Длительность питья бутылки в тиках (глоток за глотком).")]
        [Range(1, 60)] public int drinkBottleDurationTicks = 16;
        [Tooltip("Сколько жажды снимает СЫРАЯ бутылка (но риск болезни).")]
        [Range(0f, 1f)] public float drinkThirstRaw = 0.7f;
        [Tooltip("Сколько жажды снимает КИПЯЧЁНАЯ бутылка (безопасно).")]
        [Range(0f, 1f)] public float drinkThirstBoiled = 0.85f;
        [Tooltip("Небольшой комфорт от кипячёной воды.")]
        [Range(0f, 0.3f)] public float drinkComfortBoiled = 0.05f;
        [Tooltip("§52: сколько глотков в одной полной бутылке (наполняют, когда пустая).")]
        [Range(1, 8)] public int bottleCapacity = 3;
        [Tooltip("Сколько маленьких глотков воды в дырявом кокосе.")]
        [Range(1, 8)] public int coconutWaterCapacity = 4;

        [Header("Инвентарь (§52)")]
        [Tooltip("§52: сколько предметов держат голые руки. Общий инвентарь = руки + карманы всей надетой одежды.")]
        [Range(1, 6)] public int handSlots = 2;

        [Header("Отдых / сон — сколько восстанавливает")]
        [Tooltip("Энергия за ночь сна на голой земле.")]
        [Range(0f, 0.5f)] public float groundSleepEnergy = 0.12f;
        [Tooltip("Энергия за один присест на земле.")]
        [Range(0f, 0.3f)] public float groundSitEnergy = 0.05f;
        [Tooltip("Комфорт за присест на земле.")]
        [Range(0f, 0.5f)] public float groundSitComfort = 0.15f;
        [Tooltip("Комфорт за присест на кромке-уступе (с видом — больше).")]
        [Range(0f, 0.5f)] public float groundSitComfortLedge = 0.25f;
        [Tooltip("Энергия за ночь в настоящей кровати.")]
        [Range(0f, 0.5f)] public float bedEnergy = 0.18f;
        [Tooltip("Энергия за ночь на листовом коврике.")]
        [Range(0f, 0.5f)] public float leafBedEnergy = 0.15f;
        [Tooltip("Комфорт за сидение на стуле.")]
        [Range(0f, 1f)] public float chairComfort = 0.4f;
        [Tooltip("Энергия за сидение на стуле.")]
        [Range(0f, 0.3f)] public float chairEnergy = 0.1f;

        [Header("Сон — ускоренное восстановление (§54.11)")]
        [Tooltip("Базовая добавка энергии за медленный тик ВО СНЕ (ночи короче по умолчанию).")]
        [Range(0f, 0.05f)] public float sleepEnergyBaseBonus = 0.010f;
        [Tooltip("Добавка за сон у горящего костра.")]
        [Range(0f, 0.05f)] public float sleepEnergyFireBonus = 0.005f;
        [Tooltip("Добавка за сон на листовом коврике.")]
        [Range(0f, 0.05f)] public float sleepEnergyLeafBedBonus = 0.006f;
        [Tooltip("Добавка за сон на премиум-лежанке (bed.basic).")]
        [Range(0f, 0.05f)] public float sleepEnergyBasicBedBonus = 0.010f;

        [Header("Кома (§60)")]
        [Tooltip("Энергия, при которой просыпается из комы истощения.")]
        [Range(0f, 0.5f)] public float comaWakeThreshold = 0.15f;
        [Tooltip("Кровь, ниже которой падает в кому кровопотери.")]
        [Range(0f, 0.5f)] public float comaBloodEnterThreshold = 0.25f;
        [Tooltip("Кровь, выше которой встаёт из комы кровопотери (гистерезис).")]
        [Range(0f, 0.6f)] public float comaBloodWakeThreshold = 0.35f;

        [Header("Еда — сколько снимает голода")]
        [Tooltip("Кокос (базовая еда).")]
        [Range(0f, 1f)] public float coconutHunger = 0.6f;
        [Tooltip("Сколько жажды снимает один глоток из дырявого кокоса.")]
        [Range(0f, 1f)] public float coconutThirst = 0.25f;
        [Tooltip("Жареное мясо (сытнее).")]
        [Range(0f, 1f)] public float cookedMeatHunger = 0.9f;

        [Header("Температура — модель")]
        [Tooltip("Ниже этой (°C, эффективной) набегает холодовой дискомфорт.")]
        [Range(0f, 25f)] public float coldBandTemp = 14f;
        [Tooltip("Выше этой (°C) набегает перегрев.")]
        [Range(15f, 40f)] public float hotBandTemp = 22f;
        [Tooltip("Дискомфорт за тик на каждый °C ниже комфортной зоны.")]
        [Range(0f, 0.1f)] public float coldPressureSlope = 0.02f;
        [Tooltip("Дискомфорт за тик на каждый °C выше зоны.")]
        [Range(0f, 0.1f)] public float heatPressureSlope = 0.025f;
        [Tooltip("Максимум прироста дискомфорта за один тик.")]
        [Range(0f, 0.3f)] public float thermalPressureCap = 0.12f;
        [Tooltip("Сколько дискомфорта СБРАСЫВАЕТСЯ за тик в комфортной зоне.")]
        [Range(0f, 0.1f)] public float thermalComfyRecovery = 0.03f;
        [Tooltip("|Знаковый комфорт| выше этого = урон от гипотермии/теплового удара.")]
        [Range(0.5f, 1f)] public float thermalDamageGate = 0.85f;
        [Tooltip("Урон HP на часть за тик при переохлаждении/перегреве.")]
        [Range(0f, 0.05f)] public float thermalHpHit = 0.012f;
        [Tooltip("Прибавка к эффективной температуре в помещении (°C).")]
        [Range(0f, 12f)] public float indoorWarmthBonus = 4f;
        [Tooltip("Насколько холоднее в воде (°C, вычитается).")]
        [Range(0f, 12f)] public float waterCoolBonus = 3f;
        [Tooltip("Тепло от костра в 1 тайле (°C).")]
        [Range(0f, 20f)] public float fireWarmthRange1 = 8f;
        [Tooltip("Тепло от костра в 2 тайлах (°C).")]
        [Range(0f, 20f)] public float fireWarmthRange2 = 4f;

        [Header("Перегрев — cool-off (§35.4)")]
        [Tooltip("Перегрев, при котором защёлкивается цель «остыть».")]
        [Range(0f, 1f)] public float coolOffEnterThreshold = 0.35f;
        [Tooltip("Перегрев, ниже которого цель отпускает (гистерезис).")]
        [Range(0f, 1f)] public float coolOffClearThreshold = 0.20f;
        [Tooltip("Солнечная экспозиция тоже должна упасть ниже этого, чтобы закончить остывание.")]
        [Range(0f, 1f)] public float coolOffSunClear = 0.45f;
        [Tooltip("Тики «передышки» после завершённого остывания (не перевыигрывает мгновенно).")]
        [Range(0, 600)] public int coolOffSettleTicks = 120;

        [Header("Солнце / загар / ожог")]
        [Tooltip("Скорость загара на открытой коже (за (UV−0.5) за часть).")]
        [Range(0f, 0.01f)] public float tanRate = 0.0009f;
        [Tooltip("Скорость покраснения (быстрее загара).")]
        [Range(0f, 0.02f)] public float sunburnRate = 0.004f;
        [Tooltip("Скорость набора «экспозиции» до события ожога.")]
        [Range(0f, 1f)] public float sunExposureRate = 0.3f;
        [Tooltip("Урон части тела при событии солнечного ожога.")]
        [Range(0f, 0.3f)] public float sunburnBurnDamage = 0.08f;

        [Header("Гигиена")]
        [Tooltip("Прирост гигиены за тик у воды (умывание).")]
        [Range(0f, 0.2f)] public float hygieneWashGain = 0.05f;
        [Tooltip("Потеря гигиены за тик обычной жизни (~10 дней до грязнули).")]
        [Range(0f, 0.005f)] public float hygieneDriftLoss = 0.0004f;
        [Tooltip("Загрязнение надетой одежды за тик.")]
        [Range(0f, 0.005f)] public float clothingDirtGain = 0.00035f;
        [Tooltip("Потеря комфорта за тик в грязной одежде.")]
        [Range(0f, 0.02f)] public float dirtyClothingComfortLoss = 0.002f;
        [Tooltip("Гигиена, ниже которой хочет искупаться.")]
        [Range(0f, 1f)] public float batheNeedThreshold = 0.4f;
        [Tooltip("Длительность купания в тиках (~1 игровой час).")]
        [Range(10, 400)] public int batheDurationTicks = 100;
        [Tooltip("Длительность стирки одной вещи в тиках.")]
        [Range(10, 200)] public int washClothesDurationTicks = 40;
        [Tooltip("Чистота вещи, ниже которой её несут стирать.")]
        [Range(0f, 1f)] public float washClothesNeedThreshold = 0.2f;

        [Header("Выносливость / стресс (мягкие)")]
        [Tooltip("Прирост выносливости за тик отдыха.")]
        [Range(0f, 0.2f)] public float staminaRestGain = 0.06f;
        [Tooltip("Расход выносливости за тик работы.")]
        [Range(0f, 0.2f)] public float staminaWorkDrain = 0.05f;
        [Tooltip("Прирост выносливости за тик простоя.")]
        [Range(0f, 0.1f)] public float staminaIdleGain = 0.015f;
        [Tooltip("Прирост стресса за тик в опасности/бою/боли/голоде.")]
        [Range(0f, 0.2f)] public float stressUpRate = 0.05f;
        [Tooltip("Спад стресса за тик в покое.")]
        [Range(0f, 0.2f)] public float stressDownRate = 0.03f;

        [Header("Климат")]
        [Tooltip("Средняя температура острова (°C).")]
        [Range(0f, 35f)] public float baseTemperature = 15.5f;
        [Tooltip("Размах суточного колебания (± °C): 25° в 15:00, 6° в 03:00 при 9.5.")]
        [Range(0f, 20f)] public float temperatureAmplitude = 9.5f;
        [Tooltip("На сколько дождь охлаждает воздух (°C).")]
        [Range(0f, 10f)] public float rainTempDrop = 3f;

        [Header("Раны")]
        [Tooltip("Скорость закрытия раны за тик (~2 дня; ×2 во сне, ×0.5 на ходу).")]
        [Range(0f, 0.02f)] public float healPerSlowTick = 1f / 300f;
        [Tooltip("Максимум записей ран (визуальных отметин).")]
        [Range(1, 64)] public int maxWounds = 36;
        [Tooltip("Сколько порезов даёт один укус (визуальная плотность; урон делится).")]
        [Range(1, 8)] public int gashesPerHit = 3;
        [Tooltip("Удары слабее этого не дробятся на порезы.")]
        [Range(0f, 0.3f)] public float minSplittableDamage = 0.09f;

        [Header("Бой — ближний бой NPC")]
        [Tooltip("Базовый ответный удар NPC голыми руками за попадание (оружие/инструменты — в GearCatalog).")]
        [Range(0f, 0.5f)] public float npcStrikePerPass = 0.15f;
        [Tooltip("Сколько тиков после урона держится адреналин: персонаж не может уснуть, эффект виден в панели.")]
        [Range(0, 300)] public int adrenalineTicks = 80;
        [Tooltip("Минимальная энергия при активном адреналине (0.05 = 5%).")]
        [Range(0f, 0.25f)] public float adrenalineEnergyFloor = 0.05f;
        [Tooltip("Множитель скорости движения при активном адреналине.")]
        [Range(1f, 3f)] public float adrenalineMoveSpeedFactor = 1.5f;

        [Header("Состояние одежды")]
        [Tooltip("Износ каждой закрывающей вещи за один укус собаки (прочность = HP-полоска в инвентаре).")]
        [Range(0f, 0.3f)] public float clothingBiteDurabilityWear = 0.065f;
        [Tooltip("Естественный износ надетой вещи за игровой день.")]
        [Range(0f, 0.2f)] public float clothingPassiveWearPerDay = 0.025f;
    }
}
