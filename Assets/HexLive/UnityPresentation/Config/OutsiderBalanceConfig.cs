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
    [MirrorTarget(typeof(Spec81))]
    [MirrorTarget(typeof(Spec82))]
    [MirrorTarget(typeof(Spec86))]
    [MirrorTarget(typeof(Spec106))]
    [MirrorTarget(typeof(Spec108))]
    [MirrorTarget(typeof(Spec111))]
    public sealed class OutsiderBalanceConfig : ScriptableObject
    {
        [Header("Общее (§72)")]
        [Tooltip("Выключить — фракций как будто нет: все снова свои, поведение возвращается к до-§72 байт-в-байт.")]
        public bool enabled = false;
        [Tooltip("Сколько чужаков селить. 0 — ни одного. Все они одна фракция и живут одним лагерем, рассаживаясь по якорю и кольцу вокруг него.")]
        [Range(0, 7)] public int outsiderCount = 1;

        [Header("Его лагерь")]
        [Tooltip("Минимум гексов от очага колонии до его стоянки — «другой конец острова».")]
        [Range(4, 16)] public int outsiderCampMinDistanceTiles = 8;
        [Tooltip("Радиус, в котором выживший стартует со знанием построек СВОЕГО лагеря. Чужие лагеря он не знает.")]
        [Range(1, 10)] public int campKnowledgeRadiusTiles = 4;
        [Tooltip("Насколько далеко от ЛЮБОЙ стоянки заводятся собаки. Обычные 5 гексов считаются от тела, а не от очага, поэтому стая заводилась у логова, пока хозяин отошёл.")]
        [Range(0, 20)] public int dogSpawnMinDistanceFromCamp = 9;
        [Tooltip("Насколько широк лагерь: в этом радиусе от якоря очаг и мебель считаются «нашими».")]
        [Range(2, 12)] public int maxCampRadiusTiles = 6;
        [Tooltip("Нижняя граница черты сострадания чужака (у колонии диапазон §53 начинается с 0.35).")]
        [Range(0f, 1f)] public float outsiderCompassionMin = 0f;
        [Tooltip("Верхняя граница черты сострадания чужака. Сострадательный налётчик не налетал бы.")]
        [Range(0f, 1f)] public float outsiderCompassionMax = 0.25f;

        [Header("Его тело (§76) — авторское, не выпавшее")]
        [Tooltip("Характеристики чужака заданы РУКАМИ и одинаковы в каждом мире: девушки катятся от сида по бюджету §76.2, а противник, который на половине сидов выпадает хилым, читается как поломка. Сумма НАРОЧНО больше колонийной (4.0 против 3.0) — «не по тем же правилам». ⚠️ Складывается с «Множитель урона рейда»: крутить по одной ручке за раз, иначе соак не скажет, что подействовало.")]
        [Range(0f, 1f)] public float outsiderStrength = 0.9f;
        [Tooltip("Ловкость чужака (0..1 → 0..10 на листе). Тяжёлый, не быстрый.")]
        [Range(0f, 1f)] public float outsiderAgility = 0.5f;
        [Tooltip("Выносливость чужака. Привык идти весь день.")]
        [Range(0f, 1f)] public float outsiderEndurance = 0.8f;
        [Tooltip("Стойкость чужака: входящий урон, кровопотеря, заживление. Он один, лечить его некому.")]
        [Range(0f, 1f)] public float outsiderToughness = 0.8f;
        [Tooltip("Неприхотливость чужака: голод, жажда, жара и холод. Живёт в дикой земле без очага под боком.")]
        [Range(0f, 1f)] public float outsiderHardiness = 0.6f;
        [Tooltip("Смекалка чужака: скорость обучения навыкам и крафта. Не мастеровой — его сила в руках.")]
        [Range(0f, 1f)] public float outsiderWits = 0.4f;
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

        [Header("Бой — стойка (§72.5)")]
        [Tooltip("Боевая стойка: стоящий боец пятится рендер-позицией, пока пара не разойдётся на эту дистанцию (wu) — дерутся друг напротив друга, а не друг в друге. Джанкшены и дальность ударов не трогаются. 0 = выкл.")]
        [Range(0f, 3f)] public float meleeHoldDistance = 0.9f;
        [Tooltip("Скорость отступания в стойку, wu/сек (шаг ходьбы ~1.2 — это осознанный шаг назад, не телепорт).")]
        [Range(0.1f, 4f)] public float meleeHoldGlideSpeed = 0.8f;
        [Tooltip("§109.11: как далеко стойка может отпятиться от своего узла (wu). Без предела отжим против наступающего каравана через полкарты.")]
        [Range(0.1f, 3f)] public float meleeHoldMaxDriftWorldUnits = 0.75f;

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

        [Header("§81 Абьюз — общение силой")]
        [Tooltip("Включить абьюз. Выключено — мир байт-в-байт прежний.")]
        public bool abuseEnabled = true;
        [Tooltip("Сытость общением, ниже которой он идёт искать жертву (1 = наговорился).")]
        [Range(0f, 2f)] public float abuseSocialFloor = 0.45f;
        [Tooltip("Голод/жажда, выше которых он готов отжимать припас.")]
        [Range(0f, 2f)] public float abuseSupplyFloor = 0.55f;
        [Tooltip("Выше этого он уже умирает — не до переговоров.")]
        [Range(0f, 2f)] public float abuseNeedCeiling = 0.90f;
        [Tooltip("База ставки; сверху прибавляется сила нужды.")]
        [Range(0f, 2f)] public float abuseBaseScore = 0.45f;
        [Tooltip("Сколько дней он не трогает колонию с начала игры.")]
        [Range(0, 12)] public int abuseGraceDays = 2;
        [Tooltip("§81.11: сытость общением, НА или НИЖЕ которой одержимость пробивает льготные дни. Держать много ниже abuseSocialFloor.")]
        [Range(0f, 0.45f)] public float abuseObsessionSocialCeiling = 0.05f;
        [Tooltip("§81.16: худшая витальная зона (голова/грудь/таз) НИЖЕ этого — новую сцену не начинает, сначала зализывает раны. Гейт только на инициации: идущую сцену и самозащиту не трогает.")]
        [Range(0f, 1f)] public float abuseWoundedVitalFloor = 0.5f;
        [Tooltip("Пауза после сцены.")]
        [Range(0, 4800)] public int abuseCooldownTicks = 900;
        [Tooltip("§87: на сколько цель абьюза запирается от аукциона после прерывания. Без замка ближайший пересчёт вернул бы его к «посидеть».")]
        [Range(0, 4800)] public int abuseLockTicks = 240;
        [Tooltip("§89: передышка после СОРВАВШЕЙСЯ попытки. Полный кулдаун за срыв выключал его почти всё время.")]
        [Range(0, 1200)] public int abuseRetryTicks = 40;
        [Tooltip("На сколько сцена отталкивает налёт: ограбил — сегодня не убивает.")]
        [Range(0, 4800)] public int abuseRaidLockoutTicks = 600;
        [Tooltip("Радиус поиска жертвы, тайлы.")]
        [Range(0, 12)] public int abuseScanRadiusTiles = 7;
        [Tooltip("§81.12: жертву ищет ГЛАЗАМИ (радиус ниже), никого не видно — рыщет к лагерю. Выключено — всевидящий выбор по ростеру.")]
        public bool abuseHuntBySight = true;
        [Tooltip("§81.12: на каком радиусе он замечает человека. Симметрия с spotStrangerRadiusTiles.")]
        [Range(0, 12)] public int abuseSightRadiusTiles = 6;
        [Tooltip("§81.12: на сколько гексов новая жертва должна быть БЛИЖЕ, чтобы он передумал на бегу. Гистерезис против метания.")]
        [Range(0, 6)] public int abuseRetargetGainTiles = 2;
        [Tooltip("У порога дома разворачивается, как волк и как налёт.")]
        public bool abuseRespectsSanctuary = true;
        [Tooltip("§81.13: проигравший сцену (кто потерял больше здоровья; при равенстве — жертва) плачет и убегает в свой лагерь.")]
        public bool abuseRoutEnabled = true;
        [Tooltip("Во сколько раз он должен быть сильнее, чтобы она сдалась.")]
        [Range(0f, 2f)] public float abuseSubmitRatio = 1.2f;
        [Tooltip("Вклад оружия и рук в оценку силы.")]
        [Range(0f, 2f)] public float abuseForceOffenseWeight = 0.6f;
        [Tooltip("Вклад брони и целости в оценку силы.")]
        [Range(0f, 2f)] public float abuseForceDefenseWeight = 0.4f;
        [Tooltip("Сколько силы добавляет ей каждая подруга рядом.")]
        [Range(0f, 2f)] public float abuseAllyForceShare = 0.6f;
        [Tooltip("§103: ПОТОЛОК сцены, тиков. Кончается она по числу ударов; это лишь страховка, если удары не ложатся.")]
        [Range(0, 4800)] public int abuseDurationTicks = 60;
        [Tooltip("Такт «она плачет», тиков от начала. §103: обязан быть РАНЬШЕ первого тычка, иначе такт не наступает никогда.")]
        [Range(0, 4800)] public int abuseBeatCryTicks = 2;
        [Tooltip("Такт первого тычка.")]
        [Range(0, 4800)] public int abuseBeatBlowTicks = 4;
        [Tooltip("§103: ПОТОЛОК приговора. Обычно он наступает раньше — как только легли все назначенные удары.")]
        [Range(0, 4800)] public int abuseBeatVerdictTicks = 48;
        [Tooltip("§103: пауза между приговором и тем, как он лезет в её рюкзак.")]
        [Range(0, 60)] public int abuseTakeDelayTicks = 4;
        [Tooltip("§103: сколько ударов он наносит. Столько сцена и длится — три в модели, три в анимации.")]
        [Range(0, 12)] public int abuseMaxBlows = 3;
        [Tooltip("§99: во сколько ДЛИН КЛИПА разводятся удары сцены. Своя скорость оружия для короткой сцены слишком частая — второй замах перебивал первый, и удара было не видно.")]
        [Range(0.5f, 4f)] public float abuseBlowSpacing = 1.2f;
        [Tooltip("§91: симпатия, ниже которой он берётся за ОРУЖИЕ. Выше — только рукопашка. Нож достаётся по истории отношений, а не по броску кубика.")]
        [Range(-1f, 1f)] public float abuseWeaponAffinity = -0.5f;
        [Tooltip("§93: насколько глубоко (0..1 от порога до дна) должна зайти ненависть, чтобы он взялся за САМОЕ тяжёлое оружие. Ниже — берёт что полегче.")]
        [Range(0f, 1f)] public float abuseHeavyWeaponDepth = 0.5f;
        [Tooltip("§91: какая доля потери симпатии достаётся ЕМУ. Без неё его собственная неприязнь не растёт, а от неё зависит, возьмётся ли он за нож и добьёт ли.")]
        [Range(0f, 1f)] public float abuserOwnAffinityShare = 1f;
        [Tooltip("§101: база шанса ответить. Умножается на её ШАНСЫ (1/расклад): голая против мужика с мачете почти никогда не лезет.")]
        [Range(0f, 2f)] public float abuseFightBackBase = 0.9f;
        [Tooltip("§101: сколько храбрости добавляет ЗЛОСТЬ. Та, кого тиранят неделю, огрызается и заведомо проигрывая.")]
        [Range(0f, 2f)] public float abuseFightBackHatred = 0.5f;
        [Tooltip("Сколько защитниц рядом заставляют его бросить сцену.")]
        [Range(0, 12)] public int abuseBreakOffDefenders = 3;
        [Tooltip("⭐ Насколько сцена закрывает ЕГО нужду в общении.")]
        [Range(0f, 2f)] public float abuseSocialGain = 0.35f;
        [Tooltip("Насколько она закрывает нужду ЖЕРТВЫ (обычно 0 — чужой контакт не в счёт).")]
        [Range(0f, 2f)] public float abuseMarkSocialGain = 0f;
        [Tooltip("Сколько стресса это ей стоит.")]
        [Range(0f, 2f)] public float abuseMarkStressCost = 0.30f;
        [Tooltip("Насколько падает её симпатия к нему.")]
        [Range(0f, 2f)] public float abuseAffinityLoss = 0.35f;
        [Tooltip("Насколько падает её доверие к нему.")]
        [Range(0f, 2f)] public float abuseTrustLoss = 0.25f;
        [Tooltip("Закрыть тихую кражу §40.5 до своих — у чужака теперь есть настоящая сцена.")]
        public bool abuseSupersedesPassiveTheft = true;
        [Tooltip("§81.10 Сколько тиков после сцены она ходит понуро (360 = 1.5 минуты).")]
        [Range(0, 2400)] public int sadWalkTicks = 360;
        [Tooltip("§81.10 Насколько медленнее она в это время ходит. 0.5 = вдвое. " +
                 "Понурый клип шире базового, и на полной скорости ноги по нему ехали.")]
        [Range(0.1f, 1f)] public float sadWalkMoveFactor = 0.5f;

        [Header("§106 Вода — убежище")]
        [Tooltip("Пловца не бьют с суши, пловец не бьёт сам, погоня за нырнувшей бросается (Prey/Raid/Abuse/волки). Выключено — вода снова ничего не значит в бою, поведение до-§106.")]
        public bool waterSanctuaryEnabled = true;

        [Header("§82 Солнце и злость")]
        [Tooltip("Во что превращается краснота в ставке «одеться». 1.0 = полностью обгоревшая хочет прикрыться так же, как продрогшая.")]
        [Range(0f, 4f)] public float sunburnDressWeight = 1.0f;
        [Tooltip("⭐ Враждебный NPC в своём лагере подходит, требует уйти и дерётся только после отказа.")]
        public bool territorialEnabled = true;
        [Tooltip("Радиус, в котором хозяин лично замечает чужака внутри границ лагеря.")]
        [Range(0, 12)] public int territoryRadiusTiles = 5;
        [Tooltip("Пауза между выгонами, чтобы не молотил одну и ту же без передышки.")]
        [Range(0, 2400)] public int territoryCooldownTicks = 300;
        [Tooltip("Ниже этой доли здоровья чужак соглашается уйти; ровно на пороге отказывается.")]
        [Range(0f, 1f)] public float territorySubmitHealth = 0.5f;
        [Tooltip("Пауза между требованием и ответом.")]
        [Range(0, 120)] public int territoryResponseDelayTicks = 8;
        [Tooltip("Сколько ударов хозяин объявляет в сцене.")]
        [Range(1, 12)] public int territoryMaxBlows = 3;
        [Tooltip("Интервал между ударами в длинах клипа.")]
        [Range(0.5f, 4f)] public float territoryBlowSpacingClips = 1.2f;
        [Tooltip("Страховочный таймаут драки; нулевой урон не даёт ложной победы.")]
        [Range(1, 600)] public int territoryFightTimeoutTicks = 60;
        [Tooltip("Ставка выгона. Очень высокая намеренно: это реакция на вторжение, а не дело между делами.")]
        [Range(0f, 4f)] public float territoryScore = 2.5f;
        [Tooltip("Насколько сильнее давит нулевое общение. Ниже ~1.5 он тонет среди бытовых дел и никого не трогает.")]
        [Range(0f, 4f)] public float lonelinessDriveMult = 3f;

        [Header("§86 Бой не до смерти, если нет ненависти")]
        [Tooltip("Включить пощаду. Выключено — люди снова добивают друг друга как звери.")]
        public bool mercyEnabled = true;
        [Tooltip("Ниже этой доли здоровья удар человека по человеку не опускает, если бьющий не ненавидит.")]
        [Range(0f, 1f)] public float mercyHealthFloor = 0.55f;
        [Tooltip("Пощадный удар не опускает ЧАСТЬ под ударом ниже этого: голова/торс не уничтожаются (мгновенная смерть), конечность не отрывается. Средний порог выше сам по себе этого не гарантирует — урон копится в одной части.")]
        [Range(0f, 0.5f)] public float mercyPartFloor = 0.05f;

        [Header("§108 Групповая охота — сговор против чужака")]
        [Tooltip("⭐ Трое собрались, обсудили его и пошли бить. Выключено — тема «чужак» исчезает из разговоров, сговор не заключается.")]
        public bool groupHuntEnabled = true;
        [Tooltip("Сколько подруг должно стоять рядом, чтобы разговор мог свернуть на него. Двое — просто разговор; сговор начинается с троих.")]
        [Range(2, 6)] public int groupHuntMinGirls = 3;
        [Tooltip("В каком радиусе считаются «собравшиеся». Маленький намеренно: это кружок у костра, а не весь край острова.")]
        [Range(1, 6)] public int groupHuntGatherRadiusTiles = 4;
        [Tooltip("Симпатия, ниже которой она готова идти его бить. Одна сцена абьюза даёт -0.35, значит двух хватает. ⚠️ Держать ВЫШЕ порога ненависти §86 (-0.6): между ними и живёт «побить, но не убить».")]
        [Range(-1f, 0f)] public float groupHuntHateThreshold = -0.5f;
        [Tooltip("Базовый вес темы «чужак» в розыгрыше разговора (у болтовни ни о чём — 1.00).")]
        [Range(0f, 4f)] public float groupHuntTopicWeight = 1.2f;
        [Tooltip("Сколько к весу темы добавляет ненависть: чем сильнее ненавидят, тем чаще о нём и говорят.")]
        [Range(0f, 4f)] public float groupHuntTopicHateGain = 1.5f;
        [Tooltip("Дальше этого сговариваться не о чем — идти полострова за местью они не станут.")]
        [Range(4, 80)] public int groupHuntMaxPactDistanceTiles = 40;
        [Tooltip("Бюджет охоты в тиках: замок цели. Истёк — охота провалилась, все расходятся.")]
        [Range(120, 4800)] public int groupHuntLockTicks = 900;
        [Tooltip("Насколько передняя охотница может оторваться от самой отставшей, прежде чем встанет и подождёт. Это и есть «держатся вместе».")]
        [Range(0, 8)] public int groupHuntSpreadTiles = 2;
        [Tooltip("Здоровье, ниже которого он уже не принимает бой и бежит в свой лагерь. Высокое намеренно: трое против одного — это погоня, а не казнь.")]
        [Range(0f, 1f)] public float groupHuntTargetFleeHealth = 0.85f;
        [Tooltip("Сколько урона ОХОТНИЦА готова получить В ЭТОЙ расправе, прежде чем выйдет и убежит. Считается разницей с моментом сговора, а не абсолютом: иначе выбывает давно хромающая, не получив ни одного удара.")]
        [Range(0f, 1f)] public float groupHuntHunterFleeDamage = 0.2f;
        [Tooltip("То же по худшей части тела: разбитая голова уводит охотницу раньше, чем просядет среднее по телу.")]
        [Range(0f, 1f)] public float groupHuntHunterFleeWorstDrop = 0.25f;
        [Tooltip("Меньше этого числа охотниц на ногах — охота разваливается.")]
        [Range(1, 4)] public int groupHuntMinRemaining = 2;
        [Tooltip("Передышка после охоты, любой. Без неё они пошли бы на него снова в тот же вечер.")]
        [Range(0, 12000)] public int groupHuntCooldownTicks = 3000;
        [Tooltip("Сколько стресса снимает удавшаяся расправа.")]
        [Range(0f, 1f)] public float groupHuntStressRelief = 0.25f;
        [Tooltip("Насколько теплеют друг к другу те, кто ходил вместе.")]
        [Range(0f, 1f)] public float groupHuntBondAffinity = 0.10f;
        [Tooltip("⭐ Сколько симпатии теряет ТА, КТО ВИДЕЛА сцену (не жертва). Недостающее звено: без него он фиксируется на удобной жертве, её симпатия уходит в -1.00, у двух других остаётся -0.35, и единогласия не бывает никогда.")]
        [Range(0f, 1f)] public float groupHuntWitnessAffinityLoss = 0.18f;
        [Tooltip("В каком радиусе сцену «видно». Шире круга сговора: слышно и с соседнего гекса.")]
        [Range(0, 12)] public int groupHuntWitnessRadiusTiles = 5;
        [Tooltip("Сколько ударов группы значит «проучили»: когда счёт набран и он оторвался или побежал — расправа удалась. Без этой меры у охоты с пощадой нет успешного конца вовсе.")]
        [Range(1, 30)] public int groupHuntBlowsToRout = 6;
        [Tooltip("Насколько он их за это возненавидит. Это его лестница оружия §91: побитый в следующий раз возьмётся за нож.")]
        [Range(0f, 1f)] public float groupHuntTargetGrudge = 0.30f;

        [Header("Обобрать беспомощного (§111)")]
        [Tooltip("Выключить — мир идёт байт-в-байт как до §111: лежащего врага никто не обыскивает.")]
        public bool lootHelplessEnabled = true;
        [Tooltip("Базовая ставка цели. Складывается в 0.95 — выше скорби и быта, ниже аварийных нужд. Он бросает дела и идёт обыскивать.")]
        [Range(0f, 2f)] public float lootHelplessBaseScore = 0.85f;
        [Tooltip("Прибавка, когда у лежащего оружие мощнее его собственного. Это и есть «защита» в мотиве: мачете нельзя скрафтить.")]
        [Range(0f, 1f)] public float lootHelplessWeaponBonus = 0.30f;
        [Tooltip("После карманов — базовый шанс стянуть одну случайную надетую вещь.")]
        [Range(0f, 1f)] public float lootHelplessGarmentBaseChance = 0.15f;
        [Tooltip("Добавка к шансу раздевания от полного стресса лутера.")]
        [Range(0f, 1f)] public float lootHelplessGarmentStressBonus = 0.20f;
        [Tooltip("Добавка к шансу раздевания от личной ненависти к лежащему.")]
        [Range(0f, 1f)] public float lootHelplessGarmentHateBonus = 0.35f;
        [Tooltip("В каком радиусе он замечает лежащего, тайлы. Режется ЗНАНИЕ, а не только дорога — иначе он чует тело через полострова.")]
        [Range(1, 20)] public int lootHelplessSightRadiusTiles = 6;
        [Tooltip("Тиков на одну вещь. Вдвое быстрее лута трупа (20): там раздевание, тут «хоп-хоп» по карманам.")]
        [Range(2, 60)] public int lootHelplessTakeTicks = 10;
        [Tooltip("Передышка после сыгранной сцены — чтобы он не садился на то же тело снова.")]
        [Range(0, 6000)] public int lootHelplessCooldownTicks = 600;
        [Tooltip("Передышка после срыва (очнулась, умерла, не влезло). Короткая: срыв — не попытка.")]
        [Range(0, 600)] public int lootHelplessRetryTicks = 40;
        [Tooltip("Потолок сцены — страховка от зависшего такта, а не игровой срок.")]
        [Range(60, 1200)] public int lootHelplessMaxSceneTicks = 240;
    }
}
