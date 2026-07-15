using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// The single saved home for the values we used to hand-tune as code
    /// constants (hop timing/geometry, swim & water feel). The game applies
    /// this asset at startup (HexTuning.LoadAndApply); the SwimTest scene's
    /// "Сохранить настройки" button writes the live-tuned values back here.
    /// One asset lives in Resources/HexLive so runtime code can load it.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Tuning Config", fileName = "HexTuningConfig")]
    public sealed class HexTuningConfig : ScriptableObject
    {
        [Header("Прыжок — тайминг")]
        [Tooltip("ВСЁ окно прыжка ВВЕРХ: толчок + полёт + приземление. Клип сжимается ровно в это время, сек.")]
        [Range(0.5f, 5f)] public float hopSeconds = 2f;
        [Tooltip("Окно прыжка ВНИЗ (спрыгивание) — обычно меньше, чтобы было быстрее. Тайминги толчка/посадки масштабируются пропорционально. Сек.")]
        [Range(0.2f, 5f)] public float downHopSeconds = 2f;
        [Tooltip("ТОЛЧОК: сколько в начале клипа занимает присед/замах — тело стоит, анимация уже играет, сек.")]
        [Range(0f, 2f)] public float hopTakeoffSeconds = 0.5f;
        [Tooltip("ПРИЗЕМЛЕНИЕ: сколько в конце клипа занимает посадка ног — тело уже в точке, стоит, сек.")]
        [Range(0f, 2f)] public float hopLandingSeconds = 0.5f;

        [Header("Прыжок — геометрия")]
        [Tooltip("ОТСТУП от стены (мировые единицы): взлетает за столько ДО стены и приземляется за столько ПОСЛЕ — симметрично. Больше = длиннее прыжок.")]
        [Range(0.1f, 1.5f)] public float hopEdgePadding = 0.3f;
        [Tooltip("СПРЫГИВАНИЕ: на сколько подпрыгивает ВВЕРХ с края перед падением (клиренс ног над кромкой). 0 = сразу вниз.")]
        [Range(0f, 0.8f)] public float hopDownUp = 0.2f;
        [Tooltip("СПРЫГИВАНИЕ: доля полёта, до которой она летит РОВНО и не падает. 0.5 = падает только перелетев кромку. Меньше = падает раньше (может задеть край).")]
        [Range(0f, 0.95f)] public float hopDownFallStartFrac = 0.5f;
        [Tooltip("НЫРОК: на сколько уходит ПОД уровень плавания в нижней точке плюха, потом выныривает.")]
        [Range(0f, 1.5f)] public float divePlungeDepth = 0.35f;

        [Header("Вода — симуляция")]
        [Tooltip("Пауза после прыжка в воду: сколько секунд барахтается на месте (tread), прежде чем поплыть.")]
        [Range(0f, 15f)] public float swimEntryPauseSeconds = 0.75f;
        [Tooltip("Множитель скорости движения в глубокой воде (1 — как пешком).")]
        [Range(0.1f, 1.5f)] public float swimSpeedFactor = 0.6f;

        [Header("Сидение на краю (ledge)")]
        [Tooltip("Прямой сдвиг попы по Y на краю — применяется ВСЕГДА (минус = ниже к земле; на верхней кромке подъём-на-ступень = 0).")]
        [Range(-1f, 1f)] public float ledgeSeatLift = 0.4f;
        [Tooltip("Сдвиг НАЗАД на кромку (к верхнему тайлу): чтобы подъём приходился на землю, а не висел над обрывом. Больше = глубже на край.")]
        [Range(0f, 1f)] public float ledgeSeatBack = 0.45f;

        [Header("Сон — комфорт за ночь (§49)")]
        [Tooltip("Комфорт за ночь сна на голой траве. Мало — чтобы всё равно хотелось строить кровать.")]
        [Range(0f, 1f)] public float sleepComfortGrass = 0.05f;
        [Tooltip("Комфорт за ночь на листовом коврике (tier-1).")]
        [Range(0f, 1f)] public float sleepComfortLeaf = 0.30f;
        [Tooltip("Комфорт за ночь на нормальной кровати (полная полоска).")]
        [Range(0f, 1f)] public float sleepComfortBed = 1.0f;
        [Tooltip("Добавка к комфорту за ночь, если спит рядом с горящим костром (трава+костёр).")]
        [Range(0f, 0.5f)] public float sleepComfortFireBonus = 0.02f;
        [Tooltip("Штраф к комфорту за ночь, если спит под открытым солнцем днём (не в тени/без крыши).")]
        [Range(0f, 0.5f)] public float sleepComfortSunPenalty = 0.15f;
        [Tooltip("Штраф к комфорту за ночь, если мокнет под дождём (без крыши).")]
        [Range(0f, 0.5f)] public float sleepComfortRainPenalty = 0.15f;

        [Header("Сон — поведение (§49)")]
        [Tooltip("Спит рядом с костром в холод / в тени в жару — маленький вес, не перебивает безопасность/крышу.")]
        public bool smartSleepSpot = true;
        [Tooltip("Множитель скорости нарастания холода/жары ВО СНЕ (0.5 = вдвое медленнее — не замерзает за ночь).")]
        [Range(0.1f, 1f)] public float thermalSleepFactor = 0.5f;

        [Header("Общение (§49)")]
        [Tooltip("Длительность разговора в тиках (дольше = заметнее стоят и болтают).")]
        [Range(20, 150)] public int talkDuration = 90;
        [Tooltip("Прибавка к социалу у инициатора за разговор (меньше = чаще хотят снова пообщаться).")]
        [Range(0.05f, 0.6f)] public float talkInitGain = 0.20f;
        [Tooltip("Прибавка к социалу у слушателя за разговор.")]
        [Range(0.05f, 0.6f)] public float talkListenGain = 0.12f;
        [Tooltip("Пассивный социал за тик рядом с компанией («второе действие», Sims-style).")]
        [Range(0f, 0.05f)] public float ambientSocialGain = 0.012f;
        [Tooltip("Не НАЧИНАЕТ разговор, если голод/жажда выше этого (начатый — доводит).")]
        [Range(0.3f, 0.9f)] public float socializeNeedGate = 0.55f;

        [Header("Вода / тень (§49)")]
        [Tooltip("Насколько тень холоднее на жаре (щит от жары, не ниже комфортной зоны). 7 = 35°→28°.")]
        [Range(0f, 12f)] public float shadeCooling = 7f;
        [Tooltip("Кипятить, пока жажда НЕ срочная: ниже этого порога готовит кипячёную, выше — пьёт сырую.")]
        [Range(0.35f, 0.85f)] public float boilThirstCeiling = 0.6f;
        [Tooltip("Насколько жажда толкает огне-цепочку ради кипячения. 0.1 = безопасно (мало потерь), 0.2 ≈ 13% кипячёной ценой стабильности, 0.3 ≈ 20%. 0 = не кипятят проактивно.")]
        [Range(0f, 0.5f)] public float boilChainWeight = 0.1f;

        [Header("Мокрая одежда (§49.7)")]
        [Tooltip("Множитель скорости за КАЖДУЮ мокрую НАСТОЯЩУЮ вещь (штаны/жилетка; бельё не считается). 0.9 = −10% за вещь.")]
        [Range(0.5f, 1f)] public float wetDragPerGarment = 0.9f;
        [Tooltip("Пол замедления от мокрой одежды (ниже не опускается).")]
        [Range(0.5f, 1f)] public float wetDragFloor = 0.8f;
        [Tooltip("Сколько комфорта снимает за тик сам факт, что промокла (мокрое бельё тоже считается).")]
        [Range(0f, 0.02f)] public float wetComfortPenalty = 0.004f;

        [Header("Вода — визуал")]
        [Tooltip("На сколько корень актёра проваливается НИЖЕ поверхности воды. 0 — ноги на поверхности.")]
        [Range(-0.5f, 1.5f)] public float sinkDepth = 0.6f;
        [Tooltip("Глубина, с которой начинается бредущая походка (wade) перед полноценным плаванием.")]
        [Range(0f, 1f)] public float wadeDepth = 0.2f;
        [Tooltip("Высота тела в воде (для tread и гребков). Мировые единицы, + = вверх.")]
        [Range(-1f, 1f)] public float swimBodyLift = 0.45f;
        [Tooltip("Амплитуда волны — высота гребня. ОДНА на всё: и меш воды, и качание пловца.")]
        [Range(0f, 1f)] public float waveAmplitude = 0.1f;
        [Tooltip("Частота волны (рад/юнит). Меньше — длиннее и плавнее волна.")]
        [Range(0.05f, 3f)] public float waveFrequency = 1.4f;
        [Tooltip("Скорость бега волны по поверхности.")]
        [Range(0f, 6f)] public float waveSpeed = 2f;

        [Header("Потеря конечности (§50)")]
        [Tooltip("Включить ампутацию (потерю руки/ноги). Выкл — механика полностью спит.")]
        public bool limbLossEnabled = true;
        [Tooltip("Порог урона ОДНОГО удара, при котором добитая до 0 конечность отрывается сразу (акула 0.2, будущее оружие). Укус собаки ~0.06 сам по себе не рвёт.")]
        [Range(0.05f, 0.5f)] public float limbSeverThreshold = 0.14f;
        [Tooltip("Шанс (0..1), что мелкий укус, ДОБИВШИЙ измолотую ногу до 0, оторвёт её. Так собаки изредка отрывают конечность.")]
        [Range(0f, 1f)] public float limbGrindSeverChance = 0.25f;
        [Tooltip("Мгновенная кровопотеря (доля шкалы Blood) в момент отрыва конечности. Жёстко: 0.4.")]
        [Range(0f, 1f)] public float limbSeverBloodLoss = 0.4f;
        [Tooltip("Глубина культёвой раны (тяжесть) — держит кровотечение (клоттинг §44 не даёт закрыться сразу).")]
        [Range(0f, 1f)] public float limbSeverWoundSeverity = 0.35f;
        [Tooltip("Множитель силы удара для ОТРУБЛЕННОЙ РУКИ (ниже пола 0.4). Одна рука — ×это, обе — ×это².")]
        [Range(0f, 0.5f)] public float severedLimbMobilityMult = 0.15f;
        [Tooltip("Доля скорости ходьбы при ползании (потеряна одна/обе ноги). 1/3 = втрое медленнее, под анимацию ползания.")]
        [Range(0.1f, 1f)] public float crawlSpeedFactor = 0.3333f;
        [Tooltip("Сколько тиков отрубленная конечность лежит в мире до разложения (16/тик; 4800 ≈ 2 игровых дня).")]
        [Range(600f, 9600f)] public float severedLimbDecayTicks = 4800f;
        [Tooltip("Шанс (0..1) за медленный тик потерять ногу, стоя на тайле-хазарде (риф/капкан). 1 = гарантированно при контакте.")]
        [Range(0f, 1f)] public float hazardSeverChance = 1f;

        // ═══════════════════════════════════════════════════════════════
        // БАЗОВЫЙ БАЛАНС ПЕРСОНАЖА (SimBalance). Всё, что раньше было
        // «магическими числами» в системах тика: скорость нужд, пороги,
        // сколько восстанавливает еда/вода/сон, урон, температура.
        // Значения по умолчанию = как в коде — менять только через слайдеры.
        // ═══════════════════════════════════════════════════════════════

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

        [Header("Солнце / загар / ожог")]
        [Tooltip("Скорость загара на открытой коже (за (UV−0.5) за часть).")]
        [Range(0f, 0.01f)] public float tanRate = 0.0018f;
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
        [Range(0f, 0.02f)] public float woundHealPerSlowTick = 1f / 300f;
        [Tooltip("Максимум записей ран (визуальных отметин).")]
        [Range(1, 64)] public int maxWounds = 36;
        [Tooltip("Сколько порезов даёт один укус (визуальная плотность; урон делится).")]
        [Range(1, 8)] public int gashesPerHit = 3;
        [Tooltip("Удары слабее этого не дробятся на порезы.")]
        [Range(0f, 0.3f)] public float minSplittableDamage = 0.09f;

        [Header("Бой — собаки / акула")]
        [Tooltip("Урон от одного укуса собаки за проход.")]
        [Range(0f, 0.3f)] public float dogBiteDamage = 0.06f;
        [Tooltip("Ответный удар NPC по собаке за проход.")]
        [Range(0f, 0.5f)] public float dogStrikeDamage = 0.15f;
        [Tooltip("Вероятность ночного налёта стаи в день.")]
        [Range(0f, 1f)] public float dogRaidChancePerDay = 0.08f;
        [Tooltip("Сколько собак в ночном налёте.")]
        [Range(1, 8)] public int dogRaidPackSize = 3;
        [Tooltip("Радиус агро собак (тайлы).")]
        [Range(1, 6)] public int dogAggroRadiusTiles = 2;
        [Tooltip("Вероятность блуждания собаки за тик.")]
        [Range(0f, 1f)] public float dogRoamChance = 0.2f;
        [Tooltip("Урон укуса акулы по ноге (также триггер отрыва конечности).")]
        [Range(0f, 0.5f)] public float sharkBiteDamage = 0.2f;

        [Header("Сострадание и взаимопомощь (§53)")]
        [Tooltip("Включить сострадание. Выкл — механика полностью спит (поведение как до §53).")]
        public bool compassionEnabled = true;
        [Tooltip("Скорость расхода сострадания за тик × страдание рядом × черта характера.")]
        [Range(0f, 0.1f)] public float compassionRate = 0.02f;
        [Tooltip("Скорость восстановления сострадания к полному, когда рядом никто не страдает.")]
        [Range(0f, 0.05f)] public float compassionRecoverRate = 0.01f;
        [Tooltip("Вес заявки «помочь» = страдание × черта × это. 0.85 позволяет заботливой бросить стройку ради умирающего.")]
        [Range(0f, 1.5f)] public float compassionAidWeight = 0.85f;
        [Tooltip("Вес накопленного «долга» (1 − сострадание) в заявке помощи.")]
        [Range(0f, 1f)] public float compassionPressureWeight = 0.2f;
        [Tooltip("Свой ГОЛОД, при/выше которого уже не до чужих — сначала спасает себя.")]
        [Range(0f, 1f)] public float compassionSelfHungerGate = 0.6f;
        [Tooltip("Своё ЗДОРОВЬЕ, ниже которого уже не помогает другим.")]
        [Range(0f, 1f)] public float compassionSelfHealthGate = 0.5f;
        [Tooltip("Порог страдания соседа (0..1), ниже которого не стоит идти помогать.")]
        [Range(0f, 1f)] public float compassionSufferingThreshold = 0.3f;
        [Tooltip("На сколько падает ГОЛОД накормленного (помощь без затрат — предмет не тратится).")]
        [Range(0f, 1f)] public float compassionFeedRelief = 0.5f;
        [Tooltip("На сколько заживают раненые части при перевязке соседа.")]
        [Range(0f, 0.5f)] public float compassionTreatHeal = 0.15f;
        [Tooltip("На сколько прибавляется КРОВЬ перевязанного соседа.")]
        [Range(0f, 0.5f)] public float compassionTreatBlood = 0.2f;
        [Tooltip("На сколько прибавляется ЗДОРОВЬЕ больного, которому дали лекарство (и снимается болезнь).")]
        [Range(0f, 0.5f)] public float compassionMedicateHeal = 0.1f;
        [Tooltip("На сколько снижается СТРЕСС утешённого соседа.")]
        [Range(0f, 1f)] public float compassionConsoleStressRelief = 0.3f;
        [Tooltip("Прибавка к отношениям с ОБЕИХ сторон за помощь (у разговора 0.15 — доброта роднит сильнее).")]
        [Range(0f, 0.5f)] public float compassionAidRelationshipGain = 0.36f;
        [Tooltip("Длительность действия помощи (тиков).")]
        [Range(10, 200)] public int compassionAidDuration = 70;
        [Tooltip("Сколько своего сострадания восстанавливает завершённая помощь.")]
        [Range(0f, 1f)] public float compassionAidSelfRestore = 0.4f;
        [Tooltip("Минимум личной черты сострадания при рождении (разброс колонии).")]
        [Range(0f, 1f)] public float compassionTraitMin = 0.35f;
        [Tooltip("Максимум личной черты сострадания при рождении.")]
        [Range(0f, 1f)] public float compassionTraitMax = 1.0f;
    }
}
