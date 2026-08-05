using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Социальный/бытовой баланс: §49 (сон-комфорт, re-arm, общение, тень/
    /// кипячение, мокрая одежда) целиком — вместе с тумблерами фич — и §53
    /// (сострадание и взаимопомощь). Поле ассета = camelCase одноимённого
    /// статика Spec49/Spec53; исключения помечены [MirrorField].
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/Social", fileName = "SocialBalance")]
    [MirrorTarget(typeof(Spec49))]
    [MirrorTarget(typeof(Spec53))]
    [MirrorTarget(typeof(SocialBalance))]
    public sealed class SocialBalanceConfig : ScriptableObject
    {
        [Header("Разговор/ссора — дельты отношений (§28.15)")]
        [Tooltip("Прибавка к отношениям обеим сторонам за завершённый разговор.")]
        [Range(0f, 0.5f)] public float talkRelationshipGain = 0.075f;
        [Tooltip("Социал инициатору за ссору (выговорилась).")]
        [Range(0f, 0.5f)] public float quarrelInitiatorSocialGain = 0.15f;
        [Tooltip("Социал слушателю за ссору.")]
        [Range(0f, 0.5f)] public float quarrelListenerSocialGain = 0.10f;
        [Tooltip("Потеря симпатии из-за ссоры.")]
        [Range(0f, 0.5f)] public float quarrelAffinityLoss = 0.18f;
        [Tooltip("Смущение начавшей ссору.")]
        [Range(0f, 1f)] public float quarrelEmbarrassment = 0.30f;
        [Tooltip("Ниже этой симпатии приглашённая отказывается говорить…")]
        [Range(-1f, 1f)] public float refusalAffinityThreshold = -0.25f;
        [Tooltip("…если только сама не одинока сильнее этого (социал выше порога).")]
        [Range(0f, 1f)] public float lonelinessOverrideThreshold = 0.25f;
        [Tooltip("Насколько отказ портит отношение пригласившей к отказавшей.")]
        [Range(0f, 0.5f)] public float rejectionAffinityPenalty = 0.075f;

        [Header("Нормировщики (§49)")]
        [Tooltip("Пассивный социал растёт только до этого потолка — выше нужен настоящий разговор.")]
        [Range(0f, 1f)] public float ambientSocialCap = 0.6f;
        [Tooltip("Вечер+ночь ≈ столько медленных тиков (нормировка ночного комфорта сна).")]
        [Range(30f, 200f)] public float sleepComfortNightSlowTicks = 75f;

        [Header("§49 — тумблеры фич")]
        [Tooltip("Sleep re-arm: убить пустые вставания (churn −47%).")]
        public bool rearm = true;
        [Tooltip("Cool-off dwell re-arm (§35.4): убить спам None→CoolOff.")]
        public bool coolRearm = true;
        [Tooltip("Единая формула комфорта сна.")]
        public bool sleepComfort = true;
        [Tooltip("Пассивный социал рядом с компанией.")]
        public bool ambientSocial = true;
        [Tooltip("Отложенная болезнь от сырой воды (DoT-бюджет).")]
        public bool sickDoT = true;
        [Tooltip("Проактивное кипячение воды при некритичной жажде.")]
        public bool proactiveBoil = true;
        [Tooltip("Выбирать место сна умно (костёр в холод / тень в жару).")]
        public bool smartSleepSpot = true;
        [Tooltip("§29G r3 спальные места: на одном гексе спят РЯДОМ (шеренга с общим углом), а не одна в другой.")]
        public bool sleepBerths = true;
        [Tooltip("Зазор между соседними местами шеренги, доля HexRadius (0.5 = 0.75 wu).")]
        [Range(0.2f, 1f)] public float sleepBerthSpacingFactor = 0.5f;
        [Tooltip("Мест по каждую сторону от центрального (1 = шеренга из трёх); дальше — снова центр, чтобы не свесить тело за кромку гекса.")]
        [Range(0, 2)] public int sleepBerthHalfSpan = 1;
        [Tooltip("§113: место в шеренге занимает и КРУПНАЯ ВЕЩЬ — костёр, валун, кровать. Выкл — тело ложится прямо в костёр (поведение до §113).")]
        public bool lieAroundObstacles = true;
        [Tooltip("§113: длина лежащего тела, доля HexRadius (0.88 = 1.32 wu).")]
        [Range(0.4f, 1.5f)] public float lieBodyLengthFactor = 0.88f;
        [Tooltip("§113: ширина лежащего тела в плечах, доля HexRadius (0.24 = 0.36 wu).")]
        [Range(0.1f, 0.6f)] public float lieBodyWidthFactor = 0.24f;
        [Tooltip("§113: минимальный физический радиус Obstacle-вещи без своего габарита (валун, пальма), доля HexRadius.")]
        [Range(0f, 0.8f)] public float lieSolidRadiusFloorFactor = 0.30f;

        [Header("§49 — cool-off dwell")]
        [Tooltip("Сколько тиков стоит остывать на охлаждающем тайле за один заход.")]
        [Range(0, 300)] public int coolOffDwellTicks = 40;
        [Tooltip("Максимум повторных заходов остывания подряд.")]
        [Range(1, 20)] public int coolOffMaxRearms = 6;

        [Header("§49 — общение")]
        [Tooltip("Длительность разговора в тиках (дольше = заметнее стоят и болтают).")]
        [Range(20, 150)] public int talkDuration = 90;
        [Tooltip("Прибавка к социалу у инициатора за разговор.")]
        [Range(0.05f, 0.6f)] public float talkInitGain = 0.20f;
        [Tooltip("Прибавка к социалу у слушателя за разговор.")]
        [Range(0.05f, 0.6f)] public float talkListenGain = 0.12f;
        [Tooltip("Пассивный социал за тик рядом с компанией («второе действие», Sims-style).")]
        [Range(0f, 0.05f)] public float ambientGain = 0.012f;
        [Tooltip("Не НАЧИНАЕТ разговор, если голод/жажда выше этого (начатый — доводит).")]
        [Range(0.3f, 0.9f)] public float socializeNeedGate = 0.55f;

        [Header("§49 — комфорт сна за ночь")]
        [Tooltip("Комфорт за ночь сна на голой траве. Мало — чтобы всё равно хотелось строить кровать.")]
        [Range(0f, 1f)] public float sleepComfortGrassNight = 0.05f;
        [Tooltip("Комфорт за ночь на листовом коврике (tier-1).")]
        [Range(0f, 1f)] public float sleepComfortLeafNight = 0.30f;
        [Tooltip("Комфорт за ночь на нормальной кровати (полная полоска).")]
        [Range(0f, 1f)] public float sleepComfortBedNight = 1.0f;
        [Tooltip("Добавка к комфорту за ночь, если спит рядом с горящим костром.")]
        [Range(0f, 0.5f)] public float sleepComfortFireBonusNight = 0.05f;
        [Tooltip("Штраф к комфорту за ночь под открытым солнцем днём (не в тени/без крыши).")]
        [Range(0f, 0.5f)] public float sleepComfortSunPenaltyNight = 0.15f;
        [Tooltip("Штраф к комфорту за ночь, если мокнет под дождём (без крыши).")]
        [Range(0f, 0.5f)] public float sleepComfortRainPenaltyNight = 0.15f;
        [Tooltip("Добавка к комфорту за ночь, если спит в тёплой куртке (подстилка-подушка).")]
        [Range(0f, 0.5f)] public float sleepComfortJacketPadNight = 0.06f;
        [Tooltip("Прирост комфорта за тик БОДРСТВОВАНИЯ у горящего костра.")]
        [Range(0f, 0.02f)] public float awakeFireComfortGain = 0.003f;
        [Tooltip("Вес костра при выборе места сна (smartSleepSpot).")]
        [Range(0f, 5f)] public float sleepSpotFireWeight = 1.5f;
        [Tooltip("Вес тени при выборе места сна в жару (smartSleepSpot).")]
        [Range(0f, 5f)] public float sleepSpotShadeWeight = 1.5f;
        [Tooltip("§65: заранее идти спать к костру, не падая замертво на месте работы.")]
        public bool deadTiredSeek = true;
        [Tooltip("§65: ниже этой энергии (0..1) — бросить дело и лечь спать у костра.")]
        [Range(0.02f, 0.45f)] public float deadTiredEnergy = 0.15f;
        [Tooltip("§65: добавка к желанию спать при полном изнеможении (ср. голод/жажда = 1.0).")]
        [Range(0f, 1f)] public float deadTiredSleepBoost = 0.30f;
        [Tooltip("§49.9: после 23:00 включать подготовку костра и непрерывный сон до полного восстановления энергии.")]
        public bool nightSleepSchedule = true;
        [Tooltip("§49.9: при этой энергии или ниже после 23:00 начинается подготовка ко сну.")]
        [Range(0.05f, 0.5f)] public float nightSleepEnergy = 0.25f;
        [Tooltip("§49.9: приоритет цепочки дрова → костёр → сон; критический голод/жажда всё равно выше.")]
        [Range(0f, 2f)] public float nightSleepBoost = 1.0f;
        [Tooltip("§49.9: до какой энергии длится ночной сон (0.999 = полная шкала с допуском float).")]
        [Range(0.8f, 1f)] public float nightSleepWakeEnergy = 0.999f;
        [Tooltip("§65 ЭКСПЕРИМЕНТ, по умолчанию 0=ВЫКЛ: >0 — сон запрещает только опасность за столько тиков (давно ушедший волк не держит без сна). Резко срезает коллапсы, НО дестабилизирует бой (спящих загрызают; смертей на соаке НЕмонотонно от окна). Включать только после отдельного соака баланса собак.")]
        [Range(0, 2400)] public int sleepDangerRecencyTicks = 0;
        [Tooltip("Множитель скорости нарастания холода/жары ВО СНЕ (0.5 = вдвое медленнее).")]
        [Range(0.1f, 1f)] public float thermalSleepFactor = 0.5f;

        [Header("§49 — вода / тень")]
        [Tooltip("Сдвиг эффективной температуры в тени, °C СО ЗНАКОМ (−7 = на 7° холоднее; щит от жары).")]
        [Range(-12f, 0f)] public float shadeCooling = -7f;
        [Tooltip("Кипятить, пока жажда НЕ срочная: ниже этого порога готовит кипячёную, выше — пьёт сырую.")]
        [Range(0.35f, 0.85f)] public float boilThirstCeiling = 0.6f;
        [Tooltip("Насколько жажда толкает огне-цепочку ради кипячения. 0.1 = безопасно, 0.3 ≈ 20% кипячёной ценой стабильности.")]
        [Range(0f, 0.5f)] public float boilChainWeight = 0.1f;

        [Header("§49.7 — мокрая одежда")]
        [Tooltip("Множитель скорости за КАЖДУЮ мокрую НАСТОЯЩУЮ вещь (бельё не считается). 0.9 = −10% за вещь.")]
        [Range(0.5f, 1f)] public float wetDragPerGarment = 0.9f;
        [Tooltip("Пол замедления от мокрой одежды (ниже не опускается).")]
        [Range(0.5f, 1f)] public float wetDragFloor = 0.8f;
        [Tooltip("Сколько комфорта снимает за тик сам факт, что промокла (мокрое бельё тоже считается).")]
        [Range(0f, 0.02f)] public float wetComfortPenalty = 0.004f;

        [Header("§53 — сострадание и взаимопомощь")]
        [Tooltip("Включить сострадание. Выкл — механика полностью спит (поведение как до §53).")]
        [MirrorField(typeof(Spec53), "Enabled")]
        public bool compassionEnabled = true;
        [Tooltip("Скорость расхода сострадания за тик × страдание рядом × черта характера.")]
        [Range(0f, 0.1f)] public float compassionRate = 0.02f;
        [Tooltip("Скорость восстановления сострадания к полному, когда рядом никто не страдает.")]
        [Range(0f, 0.05f)] public float recoverRate = 0.01f;
        [Tooltip("Вес заявки «помочь» = страдание × черта × это. 0.85 позволяет заботливой бросить стройку ради умирающего.")]
        [Range(0f, 1.5f)] public float aidWeight = 0.85f;
        [Tooltip("Вес накопленного «долга» (1 − сострадание) в заявке помощи.")]
        [Range(0f, 1f)] public float pressureWeight = 0.2f;
        [Tooltip("Свой ГОЛОД, при/выше которого уже не до чужих — сначала спасает себя.")]
        [Range(0f, 1f)] public float selfHungerGate = 0.6f;
        [Tooltip("Своё ЗДОРОВЬЕ, ниже которого уже не помогает другим.")]
        [Range(0f, 1f)] public float selfHealthGate = 0.5f;
        [Tooltip("Порог страдания соседа (0..1), ниже которого не стоит идти помогать.")]
        [Range(0f, 1f)] public float sufferingThreshold = 0.3f;
        [Tooltip("§53.7: помощь ТРАТИТ припасы помогающей (еда/вода/бинт/таблетка). Выкл — старая «бесплатная» помощь и никаких походов за припасом.")]
        public bool aidCostsSupplies = true;
        [Tooltip("§53.7: сколько тиков поход за припасом ради подруги держится, даже если страдающая ушла из виду.")]
        [Range(120, 4800)] public int aidErrandTicks = 1200;
        [Tooltip("§53.7: какая доля заявки «помочь» перетекает в добывающее дело. <1 — сходить за припасом чуть менее срочно, чем сама помощь.")]
        [Range(0f, 1.5f)] public float aidErrandBidShare = 0.85f;
        [Tooltip("На сколько падает ГОЛОД накормленного (если у еды своя питательность — берётся она).")]
        [Range(0f, 1f)] public float feedRelief = 0.5f;
        [Tooltip("На сколько падает ЖАЖДА напоенного (тратится глоток из фляги/кокоса).")]
        [Range(0f, 1f)] public float hydrateRelief = 0.5f;
        [Tooltip("На сколько заживают раненые части при перевязке соседа.")]
        [Range(0f, 0.5f)] public float treatHeal = 0.15f;
        [Tooltip("На сколько прибавляется КРОВЬ перевязанного соседа.")]
        [Range(0f, 0.5f)] public float treatBlood = 0.2f;
        [Tooltip("⭐ ПОТОЛОК ЛЕЧЕНИЯ у НУЛЕВОГО врачевания: выше этого перевязка зону не поднимет, сколько ни повторяй (мастер лечит до единицы). Без потолка сиделка вылечивала соседку до 100%, просто садясь рядом. ⚠️ ОБЯЗАН быть выше Spec105.TorsoExitHealth (0.15) — навык у всех стартует с нуля, и при меньшем потолке новичок не сможет даже поднять умирающую на ноги. Собственное заживление тела этим НЕ ограничено.")]
        [Range(0.05f, 1f)] public float treatCapNovice = 0.35f;
        [Tooltip("На сколько прибавляется ЗДОРОВЬЕ больного, которому дали лекарство (и снимается болезнь).")]
        [Range(0f, 0.5f)] public float medicateHeal = 0.1f;
        [Tooltip("На сколько снижается СТРЕСС утешённого соседа.")]
        [Range(0f, 1f)] public float consoleStressRelief = 0.3f;
        [Tooltip("§110: на сколько тиков утешение укорачивает ПЛАЧ (с подругой рядом она выплакивается быстрее).")]
        [Range(0, 400)] public int consoleCryingReliefTicks = 120;
        [Tooltip("§110: утешала на коленях — столько тиков стоит после, пока играет вставание с колен (28 ≈ длина клипа). Иначе уезжает по земле в позе молитвы.")]
        [Range(0, 120)] public int consoleStandUpTicks = 28;
        [Tooltip("Прибавка к отношениям с ОБЕИХ сторон за помощь (у разговора 0.075 — доброта роднит сильнее).")]
        [Range(0f, 0.5f)] public float aidRelationshipGain = 0.18f;
        [Tooltip("Длительность действия помощи (тиков).")]
        [Range(10, 200)] public int aidDuration = 70;
        [Tooltip("Сколько своего сострадания восстанавливает завершённая помощь.")]
        [Range(0f, 1f)] public float aidSelfRestore = 0.4f;
        [Tooltip("Минимум личной черты сострадания при рождении (разброс колонии).")]
        [Range(0f, 1f)] public float traitMin = 0.35f;
        [Tooltip("Максимум личной черты сострадания при рождении.")]
        [Range(0f, 1f)] public float traitMax = 1.0f;

        [Header("§68 — сама себя перевязывает")]
        [Tooltip("Включить самолечение. Выкл — как раньше: раны латает только подруга через «помощь», а свои бинты уходят лишь в пассивном последнем шансе.")]
        public bool selfTreatEnabled = true;
        [Tooltip("Тяжесть ран (макс из: 1−HP, 1−худшая зона, 1−кровь), с которой она бросает дела и перевязывается.")]
        [Range(0f, 1f)] public float selfTreatBurdenThreshold = 0.25f;
        [Tooltip("ПОСЛЕДНИЙ бинт — неприкосновенный запас: с одним бинтом нужна вот такая тяжесть ран.")]
        [Range(0f, 1f)] public float selfTreatLastBandageBurden = 0.45f;
        [Tooltip("База заявки «перевязаться».")]
        [Range(0f, 1f)] public float selfTreatBase = 0.35f;
        [Tooltip("Множитель тяжести ран в заявке.")]
        [Range(0f, 2f)] public float selfTreatWeight = 1.0f;
        [Tooltip("Ниже этой крови перевязка получает аварийную прибавку и обходит все дела.")]
        [Range(0f, 1f)] public float selfTreatBleedBlood = 0.6f;
        [Tooltip("Размер аварийной прибавки при кровопотере.")]
        [Range(0f, 2f)] public float selfTreatBleedEmergency = 0.6f;
        [Tooltip("Сколько тиков она наматывает повязку.")]
        [Range(10, 200)] public int selfTreatDuration = 60;
        [Tooltip("На сколько поднимаются раненые зоны от СВОЕЙ повязки (чуть меньше, чем от чужих рук).")]
        [Range(0f, 0.5f)] public float selfTreatHeal = 0.12f;
        [Tooltip("На сколько прибавляется своя КРОВЬ после перевязки.")]
        [Range(0f, 0.5f)] public float selfTreatBlood = 0.18f;
    }
}
