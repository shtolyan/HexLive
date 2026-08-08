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
    [MirrorTarget(typeof(Spec85))]
    [MirrorTarget(typeof(Spec94))]
    [MirrorTarget(typeof(Spec105))]
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

        [Header("Обнаружение застоя (§30.15) — только диагностика, поведение не трогает")]
        [Tooltip("Цель есть, дела нет, не идёт — столько тиков, и это застой (подпись §102).")]
        [Range(10, 400)] public int stuckIdleTicks = 40;
        [Tooltip("Одно взаимодействие держится дольше стольких тиков.")]
        [Range(100, 4000)] public int stuckStepTicks = 600;
        [Tooltip("Цели нет вообще, а нужда уже кричит — столько тиков.")]
        [Range(20, 1200)] public int stuckGoallessTicks = 120;
        [Tooltip("«Иду», но с места не сдвинулась — столько тиков.")]
        [Range(20, 600)] public int stuckFrozenTicks = 60;
        [Tooltip("Пока застой длится, повторять жалобу не чаще этого.")]
        [Range(50, 2000)] public int stuckRepeatEmitTicks = 200;

        [Header("Длительности решений (были продублированы числом в 2-3 местах)")]
        [Tooltip("Источник оказался занят по прибытии — столько тиков к нему не возвращается.")]
        [Range(60, 2400)] public int shunTicks = 600;
        [Tooltip("Посидев, не садится снова столько тиков (анти-дребезг §35.4).")]
        [Range(20, 1200)] public int sitCooldownTicks = 240;
        [Tooltip("Одевшись, не переодевается столько тиков.")]
        [Range(20, 1200)] public int dressCooldownTicks = 160;
        [Tooltip("Только что проснулась: столько тиков стоит и приходит в себя (§41.5). Не меньше 18: GetUp-клип (Situp To Idle, 132 кадра @ 30 fps) = 4.4 с = 17.6 тика; меньше — ноги скользят под клип вставания (баг #1).")]
        [Range(0, 120)] public int wakeGraceTicks = 18;
        [Tooltip("Цель, чей план не построился, отдыхает столько тиков.")]
        [Range(10, 400)] public int failureCooldownTicks = 40;

        [Header("Пороги аукциона (одно число значило пять разных вещей)")]
        [Tooltip("Жажда, выше которой идёт пить и идёт ЗА водой.")]
        [Range(0f, 1f)] public float drinkThirstThreshold = 0.35f;
        [Tooltip("Тепловой комфорт, НИЖЕ которого считается, что мёрзнет (открывает розжиг трением).")]
        [Range(-1f, 0f)] public float freezingComfortThreshold = -0.35f;
        [Tooltip("Насколько недостающий материал тянет к стройплощадке (одинаково для всех четырёх).")]
        [Range(0f, 2f)] public float buildSiteMaterialPull = 0.35f;

        [Header("Нужды — скорость (за медленный тик, ~150/день)")]
        [Tooltip("Сколько ГОЛОДА набегает за медленный тик. Больше = быстрее хочет есть. §53.7: вдвое медленнее (было 0.0037).")]
        [Range(0f, 0.05f)] public float hungerRate = 0.00185f;
        [Tooltip("Сколько ЖАЖДЫ набегает за тик. Больше = быстрее хочет пить. §53.7: вдвое медленнее (было 0.008).")]
        [Range(0f, 0.05f)] public float thirstRate = 0.004f;
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
        [Tooltip("Переодевается, только если вещь реально поднимает тепло не меньше этого (с учётом потолка) — не надевает такую же/худшую рубаху. +0.1 тепла ≈ +1°C.")]
        [Range(0f, 1f)] public float dressWarmthGainMin = 0.05f;

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
        [Range(0f, 0.15f)] public float starveDamageBoth = 0.01f;
        [Tooltip("Урон HP за тик, когда лишь одно (голод ИЛИ жажда) на максимуме.")]
        [Range(0f, 0.15f)] public float starveDamageOne = 0.006f;

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
        [Tooltip("§52: постоянная носка сверх рук (пояс/за пазухой). Голышом = руки + это.")]
        [Range(0, 6)] public int baseCarrySlots = 2;

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

        [Header("Сон без задних ног / обморок (§60 r2)")]
        [Tooltip("Энергия, при которой просыпается из «сна без задних ног» (рухнула при 0 энергии; спит до этой отметки, боль будит раньше).")]
        [Range(0f, 0.9f)] public float exhaustedSleepWakeEnergy = 0.45f;
        [Tooltip("Устарело (§60 r2): порог пробуждения старой комы истощения.")]
        [Range(0f, 0.5f)] public float comaWakeThreshold = 0.15f;
        [Tooltip("Кровь, ниже которой падает в кому кровопотери.")]
        [Range(0f, 0.5f)] public float comaBloodEnterThreshold = 0.25f;
        [Tooltip("Кровь, выше которой встаёт из комы кровопотери (гистерезис).")]
        [Range(0f, 0.6f)] public float comaBloodWakeThreshold = 0.35f;
        [Tooltip("§60.7: без сознания в глубокой воде столько тиков подряд — утонула. Очнулась или вытащили на сушу — счётчик сбрасывается.")]
        [Range(0, 12000)] public int drownDeathTicks = 1000;

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
        [Tooltip("Ускоренный сброс дискомфорта у СИЛЬНОГО источника тепла (кольцо костра / в помещении) — оттаивает за пару тиков, а не медленно.")]
        [Range(0f, 0.5f)] public float fireThawRecovery = 0.25f;
        [Tooltip("|Знаковый комфорт| выше этого = урон от гипотермии/теплового удара.")]
        [Range(0.5f, 1f)] public float thermalDamageGate = 0.85f;
        [Tooltip("Урон HP на часть за тик при переохлаждении/перегреве.")]
        [Range(0f, 0.05f)] public float thermalHpHit = 0.012f;
        [Tooltip("§82: ниже этого порога жара и холод не доламывают ВИТАЛЬНУЮ часть. Перегрев бьёт все семь частей каждый медленный тик — для человека в глухой броне без порога это верная смерть.")]
        [Range(0f, 1f)] public float thermalVitalFloor = 0.35f;
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
        [Tooltip("Скорость загара на открытой коже (за (UV−0.5) за часть). Меняет только СКОРОСТЬ набора, не темноту.")]
        [Range(0f, 0.01f)] public float tanRate = 0.00009f;
        [Tooltip("Темнота/сила загара на максимуме: 1 = полный загорелый вид, ниже = светлее/менее тёмный (0 = кожа без загара). Крутит цвет, а не скорость.")]
        [Range(0f, 1f)] public float tanStrength = 1f;
        [Tooltip("Скорость покраснения (быстрее загара).")]
        [Range(0f, 0.02f)] public float sunburnRate = 0.0012f;
        [Tooltip("Скорость набора «экспозиции» до события ожога.")]
        [Range(0f, 1f)] public float sunExposureRate = 0.04f;
        [Tooltip("Урон части тела при событии солнечного ожога.")]
        [Range(0f, 0.3f)] public float sunburnBurnDamage = 0.08f;
        [Tooltip("§82: ниже этого порога солнце не доламывает ВИТАЛЬНУЮ часть (голова, торс). Солнечный удар доводит до беспамятства, но не убивает: без порога забронированный целиком человек сгорал за треть дня — у него открыта ровно одна часть, и все удары шли в неё.")]
        [Range(0f, 1f)] public float sunburnVitalFloor = 0.45f;

        [Header("Гигиена")]
        [Tooltip("Прирост гигиены за тик у воды (умывание).")]
        [Range(0f, 0.2f)] public float hygieneWashGain = 0.05f;
        [Tooltip("Потеря гигиены за тик обычной жизни (~10 дней до грязнули).")]
        [Range(0f, 0.005f)] public float hygieneDriftLoss = 0.0004f;
        [Tooltip("Кровь пачкает: гигиена, теряемая на единицу общего HP, списанного уроном. 3 — треть максимума здоровья обнуляет гигиену (полностью помыться). Через гигиену же прячутся кровяные капли на коже: помылась — капли исчезли.")]
        [Range(0f, 10f)] public float hygieneDamageLoss = 3f;
        [Tooltip("Загрязнение надетой одежды за тик.")]
        [Range(0f, 0.005f)] public float clothingDirtGain = 0.00035f;
        [Tooltip("Потеря комфорта за тик в грязной одежде.")]
        [Range(0f, 0.02f)] public float dirtyClothingComfortLoss = 0.002f;
        [Tooltip("Гигиена, ниже которой хочет искупаться.")]
        [Range(0f, 1f)] public float batheNeedThreshold = 0.4f;
        [Tooltip("Длительность купания в тиках (~1 игровой час).")]
        [Range(10, 400)] public int batheDurationTicks = 100;
        [Tooltip("Длительность стирки одной вещи в тиках.")]
        [Range(10, 200)] public int washClothesDurationTicks = 80;
        [Tooltip("Чистота вещи, ниже которой её несут стирать.")]
        [Range(0f, 1f)] public float washClothesNeedThreshold = 0.2f;
        [Tooltip("Вес худшей НАДЕТОЙ грязи в позыве искупаться (раздевание у воды делает кучу доступной стирке). 0 — грязная одежда не тянет купаться.")]
        [Range(0f, 1f)] public float batheWornDirtWeight = 0.9f;
        [Tooltip("Мокрота одежды, выше которой она идёт сушить (вешать на сушилку / стоять у огня).")]
        [Range(0f, 1f)] public float dryClothesWetThreshold = 0.35f;

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
        [Tooltip("§110: сколько тиков она лежит и рыдает после стресс-краха (утешение укорачивает).")]
        [Range(30, 900)] public int cryingBreakdownTicks = 240;
        [Tooltip("§110: сколько стресса снимают сами слёзы за медленный тик, поверх обычного спада. 0.04 ≈ −0.6 за весь плач: встаёт спокойнее, но не в ноль.")]
        [Range(0f, 0.2f)] public float cryingStressRelief = 0.04f;

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
        [Tooltip("§104: ВСЕ удары по таймлайну замаха (видимые), а не по легаси-фазе. Меняет каденцию и летальность §56 — включать только после A/B-соаков.")]
        public bool timedMeleeEverywhere = true;
        [Tooltip("§26.6A r5: сквозь чужое тело/ствол рука НЕ проходит — взаимодействие Blocked, идёт обход. Цена замерена (86/120→72/120 живых на 30 сидах: время уходит на обходы, против собак колония на грани). Выключить = вернуть визуальную ложь «кокос вскрывают через ствол пальмы».")]
        public bool reachThroughBodiesBlocked = true;
        [Tooltip("Базовый ответный удар NPC голыми руками за попадание (оружие/инструменты — в GearCatalog). Не действует при timedMeleeEverywhere.")]
        [Range(0f, 0.5f)] public float npcStrikePerPass = 0.15f;
        [Tooltip("Сколько тиков после урона держится адреналин: персонаж не может уснуть, эффект виден в панели.")]
        [Range(0, 300)] public int adrenalineTicks = 80;
        [Tooltip("Минимальная энергия при активном адреналине (0.05 = 5%).")]
        [Range(0f, 0.25f)] public float adrenalineEnergyFloor = 0.05f;
        [Tooltip("Множитель скорости движения при активном адреналине. §71: усилен в 1.5 раза (было 1.5). НЕ складывается со спринтом защиты — MovementSystem берёт БОЛЬШИЙ из двух.")]
        [Range(1f, 4f)] public float adrenalineMoveSpeedFactor = 2.25f;
        [Tooltip("§71: ОБЩАЯ скорость ходьбы колонии. Умножается в MovementSystem — это единственная ручка темпа (поле npc.MoveSpeed всегда 1 и никем не задаётся). Прыжок через уступ идёт по реальным секундам и НЕ ускоряется.")]
        [Range(0.25f, 4f)] public float baseMoveSpeedFactor = 1.2f;

        [Header("§71 Повороты")]
        [Tooltip("Множитель скорости поворота (к npc.TurnSpeed = 90°/с). 2.4 = 216°/с = 54° за тик, то есть минимальный изгиб решётки в 60° проходится за ОДИН тик.")]
        [Range(0.5f, 6f)] public float baseTurnSpeedFactor = 2.4f;
        [Tooltip("Выше этого угла она встаёт и разворачивается на месте. Раньше порог был 30°, а минимальный поворот решётки — 60°, поэтому КАЖДЫЙ угол стоил полной остановки на 3 тика.")]
        [Range(45f, 180f)] public float turnFreezeAngle = 100f;
        [Tooltip("Ниже этого угла поворот вообще ничего не стоит — она просто заворачивает на ходу.")]
        [Range(0f, 90f)] public float turnFreeAngle = 40f;
        [Tooltip("Самое сильное замедление на плавном повороте (доля от обычной скорости).")]
        [Range(0.2f, 1f)] public float turnMinSpeedFactor = 0.6f;
        [Tooltip("Пауза после разворота на месте, секунды. Применяется ТОЛЬКО после настоящего разворота.")]
        [Range(0f, 1f)] public float postTurnPauseSeconds = 0.15f;

        [Header("§71 Бег и дыхание")]
        [Tooltip("Скорость бега при побеге (Flee).")]
        [Range(1f, 4f)] public float fleeRunSpeedFactor = 2.25f;
        [Tooltip("Скорость бега, когда она умирает с голоду/жажды и спешит к еде или воде. ЕДИНСТВЕННАЯ мирная причина бежать — поэтому бег видно, но каждая прогулка не превращается в трусцу.")]
        [Range(1f, 3f)] public float needRunSpeedFactor = 1.6f;
        [Tooltip("§71.4 За сколько мировых единиц до СТАТИЧНОЙ цели бегущая переходит на шаг " +
                 "(3.0 ≈ 1.15 гекса). Погоню за человеком и побег не осаживает. 0 = выключено.")]
        [Range(0f, 6f)] public float arrivalWalkDistance = 3f;
        [Tooltip("Расход дыхания за тик бега (0.011 ≈ 23 секунды бега с полного бака).")]
        [Range(0f, 0.05f)] public float breathDrainPerTick = 0.011f;
        [Tooltip("Восстановление дыхания за тик ШАГА — она отдыхает на ходу, а не идёт садиться. " +
                 "0.005 ≈ 27 секунд шага до перевзвода.")]
        [Range(0f, 0.02f)] public float breathWalkRecoverPerTick = 0.005f;
        [Tooltip("Восстановление дыхания за тик СТОЯ — вдвое быстрее, чем в шаге.")]
        [Range(0f, 0.05f)] public float breathIdleRecoverPerTick = 0.010f;
        [Tooltip("До какого уровня надо отдышаться, чтобы снова разрешили бежать (гистерезис — иначе " +
                 "мигает шаг/бег на пороге). 0.55 = повторный рывок ≈ 12 секунд: короче первого, но всё ещё бег.")]
        [Range(0f, 1f)] public float breathReArm = 0.55f;

        [Header("Состояние одежды")]
        [Tooltip("Износ каждой закрывающей вещи за один укус собаки (прочность = HP-полоска в инвентаре). Снижен в 5 раз (было 0.013) — одежда рвалась слишком быстро.")]
        [Range(0f, 0.3f)] public float clothingBiteDurabilityWear = 0.0026f;
        [Tooltip("Естественный износ надетой вещи за 150 медленных тиков (10 реальных минут). Снижен в 5 раз (было 0.005).")]
        [Range(0f, 0.2f)] public float clothingPassiveWearPerDay = 0.001f;

        [Header("§85 Ночь — отдых, а не вторая смена")]
        [Tooltip("Во сколько раз спящее тело медленнее тратит голод и жажду. Было 0.4 — за ночь съедало заметную долю сытости.")]
        [Range(0f, 1f)] public float sleepMetabolismFactor = 0.1f;
        [Tooltip("Во сколько раз медленнее уходит нужда в общении во сне. Раньше она текла полностью: ложился общительным, вставал одиноким.")]
        [Range(0f, 1f)] public float sleepSocialFactor = 0.1f;

        [Header("§94 Отношения — дело десяти дней")]
        [Tooltip("Насколько обида тает к нулю за медленный тик. Быстрее дружбы, но медленнее, чем копится злость от абьюза.")]
        [Range(0f, 0.01f)] public float grudgeDriftPerTick = 0.0006f;
        [Tooltip("Насколько остывает симпатия за медленный тик.")]
        [Range(0f, 0.01f)] public float warmthDriftPerTick = 0.0002f;

        [Header("§105 На грани смерти — окно, когда её ещё можно спасти")]
        [Tooltip("Выключить — смерть снова мгновенная, ПОБИТОВО как до §105. Это выключатель для бисекции соаком, а не режим игры.")]
        public bool dyingEnabled = true;
        [Tooltip("Сколько тиков она лежит и умирает от КРОВОПОТЕРИ, прежде чем запас кончится. Меньше ~600 — помощница физически не успевает дойти, и окно превращается в косметику.")]
        [Range(200, 4000)] public int windowTicksBloodLoss = 900;
        [Tooltip("То же для ПРОБИТОЙ ГРУДИ.")]
        [Range(200, 4000)] public int windowTicksTorso = 1200;
        [Tooltip("То же для ГОЛОДА: тело сдаётся медленнее, чем вытекает кровь.")]
        [Range(200, 6000)] public int windowTicksStarvation = 1800;
        [Tooltip("То же для ЖАЖДЫ.")]
        [Range(200, 6000)] public int windowTicksDehydration = 1500;
        [Tooltip("Насколько характеристика растягивает окно (§76-форма: при разбросе 0.5 и усилении 0.3 это ±15%). Кровь и грудь держит Стойкость, голод и жажду — Неприхотливость.")]
        [Range(0f, 1f)] public float holdGain = 0.3f;
        [Tooltip("Во сколько раз урон по ЛЕЖАЩЕЙ срезает запас смерти. Волк догрызает упавшую: 1.5 значит, что укус в 0.2 HP снимает 0.3 запаса, и стая добивает за три-четыре.")]
        [Range(0f, 5f)] public float damageReserveFactor = 1.5f;
        [Tooltip("Пока помощница РАБОТАЕТ над ней, запас не тает. Выключить — можно умереть на последнем тике перевязки.")]
        public bool aidFreezesReserve = true;
        [Tooltip("На сколько пиннится витальная зона умирающей вместо нуля. ⭐ НЕ поблажка: около сорока мест читают Health<=0 как «труп», и без пола её перестали бы видеть ровно те системы, которые должны над ней склониться.")]
        [Range(0.001f, 0.2f)] public float bodyFloor = 0.02f;
        [Tooltip("Сколько крови надо набрать сверх нуля, чтобы кровопотеря отпустила (при остановленном кровотечении). ⭐ ОБЯЗАН быть выше порога падения: упала на нуле — вставай на десяти, иначе она поднимается через тик.")]
        [Range(0f, 0.5f)] public float bloodExitFloor = 0.1f;
        [Tooltip("До скольки должна зарасти ХУДШАЯ ВИТАЛЬНАЯ ЗОНА (голова/грудь/ТАЗ), чтобы встать. Против 0.02, в которые её запиннило падение: раньше выход спрашивал «выше пола?» и первый же тик регенерации поднимал её на ноги.")]
        [Range(0.02f, 0.6f)] public float vitalExitHealth = 0.15f;
        [Tooltip("Сколько тиков она лежит КАК МИНИМУМ, что бы ни говорили пороги. Без этого помощница закрывает порог на первом же тике, и «рухнула и поднялась» опять читается как сбой. 300 ≈ 75 секунд игрового времени.")]
        [Range(0, 1200)] public int minDyingTicks = 300;
        [Tooltip("Очнувшись ВЫМОТАННОЙ, она не встаёт — переворачивается и спит там же. Выключить = прежнее: подъём, пара секунд стоя, и снова на землю (аукцион у вымотанного тела первым делом выбирает сон).")]
        public bool stayDownIfSpent = true;
        [Tooltip("§105.14: очнувшись, когда рядом враг, она НЕ встаёт — притворяется мёртвой, и враг теряет к ней интерес (не наводится и бросает погоню). Выключить = прежнее поведение побитово.")]
        public bool playDeadEnabled = true;
        [Tooltip("На каком расстоянии в тайлах враг ещё «рядом». Своя ручка: радиус восприятия и радиус агро отмеряны под другое.")]
        [Range(1, 10)] public int playDeadRadiusTiles = 3;
        [Tooltip("Сколько тиков она долёживает после ухода врага. Гистерезис: без запаса она вскакивала бы и падала на каждом шаге зверя туда-сюда.")]
        [Range(16, 2000)] public int playDeadHoldTicks = 240;
        [Tooltip("Предохранитель: дольше не притворяется, даже если враг не уходит. Без потолка волк у лагеря укладывает её навсегда, и это читается как зависание.")]
        [Range(240, 24000)] public int playDeadMaxTicks = 4800;
        [Tooltip("Надбавка к ставке помощи на УМИРАЮЩУЮ, в долях StarvingBoost. 1 — спасение обгоняет любую работу и любую другую помощь. Собственный кризис помощницы (§53.5) сильнее в любом случае.")]
        [Range(0f, 2f)] public float rescueEmergencyBoost = 1f;
        [Tooltip("Сколько тиков после спасения она «едва живая». Сутки визуально 24000 тиков, то есть 3600 ≈ три с половиной часа.")]
        [Range(0, 12000)] public int convalescentTicks = 3600;
        [Tooltip("Во сколько раз МЕДЛЕННЕЕ восстанавливается выносливость, пока действует штраф.")]
        [Range(0.05f, 1f)] public float convalescentStaminaRegenFactor = 0.3333f;
        [Tooltip("Во сколько раз БЫСТРЕЕ тратится выносливость, пока действует штраф.")]
        [Range(1f, 5f)] public float convalescentStaminaDrainFactor = 2f;
        [Tooltip("Во сколько раз медленнее она ходит, пока действует штраф.")]
        [Range(0.1f, 1f)] public float convalescentMoveFactor = 0.5f;
    }
}
