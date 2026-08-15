using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// §54 Stranded-Deep цикл ресурсов: дерево-цепочка, биллы костра/кроватей/
    /// сушилки, файбер→верёвка/ткань, разделка туш и порча мяса, кроны пальм,
    /// гейты стройки. Поле ассета = camelCase одноимённого статика SimBalance.
    /// ВАЖНО (§54.12): биллы костра/кроватей/сушилки ДОЛЖНЫ равняться суммам
    /// стадий BuildSiteMath (стадии зеркалят группы префаба) — гейт проверяет.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/Resource Loop", fileName = "ResourceLoopBalance")]
    [MirrorTarget(typeof(SimBalance))]
    public sealed class ResourceLoopConfig : ScriptableObject
    {
        [Header("Дерево-цепочка")]
        [Tooltip("Сколько палок даёт расщепление одного бревна (топором).")]
        [Range(1, 12)] public int logSplitYield = 4;
        [Tooltip("Длительность расщепления бревна в тиках.")]
        [Range(10, 600)] public int logSplitDurationTicks = 120;

        [Header("Костёр — staged-билл (§54.14; = суммам BuildSiteMath.CampfireStages)")]
        [Tooltip("Палки: 9 куча + 2 стойки + 1 перекладина.")]
        [Range(1, 40)] public int campfireBillSticks = 12;
        [Tooltip("Камни плотного кольца.")]
        [Range(1, 40)] public int campfireBillStones = 18;
        [Tooltip("Верёвки: по одной на узел стойки.")]
        [Range(0, 10)] public int campfireBillRope = 2;
        [Tooltip("Стадия 2 (кольцо): топливо горит с этим множителем (0.5 = вдвое дольше).")]
        [Range(0.1f, 1f)] public float campfireRingBurnMultiplier = 0.5f;
        [Tooltip("§120: очаг ПОД КРЫШЕЙ жжёт топливо ещё с этим множителем (поверх кольца: 0.5×0.5 = каменное кольцо в доме горит вчетверо дольше голого костра).")]
        [Range(0.1f, 1f)] public float indoorFireBurnMultiplier = 0.5f;
        [Tooltip("Стадия 3 (вертел): сколько тиков жарится кусок мяса.")]
        [Range(50, 800)] public int meatRoastDurationTicks = 200;
        [Tooltip("Сколько кусков висит на перекладине одновременно (= 6 слотам вида CampfireSpitMeat).")]
        [Range(1, 8)] public int campfireSpitCapacity = 6;
        [Tooltip("§54.17: базовая ставка аукциона CookMeat (Base + Weight·Hunger — вместе перебивают GetFood при любом голоде, иначе она вечно носит мясо и не вешает).")]
        [Range(0f, 1f)] public float cookMeatBase = 0.4f;
        [Tooltip("§54.17: вес голода в ставке аукциона CookMeat.")]
        [Range(0f, 2f)] public float cookMeatHungerWeight = 1f;
        [Tooltip("Сколько палок рассыпано у метки очага на старте мира (иначе холодный старт дедлочится).")]
        [Range(0, 30)] public int campfireStarterSticks = 14;
        [Tooltip("Сколько тиков после последнего замерзания доступно трение-разжигание (ручное сверло).")]
        [Range(0, 1200)] public int frictionLightGraceTicks = 400;

        [Header("Единственная кровать — билл (§54.2)")]
        [Tooltip("bed.basic: брёвна боковых направляющих.")]
        [Range(1, 12)] public int bedBasicBillLogs = 4;
        [Tooltip("bed.basic: палки-поперечины.")]
        [Range(1, 30)] public int bedBasicBillSticks = 5;
        [Tooltip("bed.basic: верёвки.")]
        [Range(0, 30)] public int bedBasicBillRope = 10;
        [Tooltip("bed.basic: листья матраса.")]
        [Range(1, 100)] public int bedBasicBillLeaves = 50;

        [Header("Сушилка (§35.5B; = суммам BuildSiteMath.DryingRackStages)")]
        [Tooltip("Палки: 2 стойки + 2 рейки.")]
        [Range(1, 20)] public int rackBillSticks = 4;
        [Tooltip("Верёвки-обвязки.")]
        [Range(0, 20)] public int rackBillRope = 4;
        [Tooltip("Сколько вещей сохнет на сушилке одновременно (по числу вешалок префаба).")]
        [Range(1, 16)] public int rackCapacity = 8;

        [Header("Сборщик воды (§54.15; = суммам BuildSiteMath.WaterCollectorStages)")]
        [Tooltip("Палки: 4 стойки + 4 верхние обвязки.")]
        [Range(1, 20)] public int waterCollectorBillSticks = 8;
        [Tooltip("Камни подставки под ёмкость.")]
        [Range(0, 20)] public int waterCollectorBillStones = 5;
        [Tooltip("Верёвки: по две на угол рамы.")]
        [Range(0, 20)] public int waterCollectorBillRope = 8;
        [Tooltip("Листья воронки.")]
        [Range(1, 40)] public int waterCollectorBillLeaves = 11;
        [Tooltip("За сколько тиков НЕПРЕРЫВНОГО дождя бутылка в сборщике наполняется доверху (600 = четверть игрового дня).")]
        [Range(60, 4800)] public int waterCollectorFillTicks = 600;

        [Header("Пальмы (§54.2: вид кроны = дроп листьев)")]
        [Tooltip("Листьев в кроне/дропе большой пальмы (~хватает на кровать с одной пальмы).")]
        [Range(1, 100)] public int bigPalmCrownLeaves = 42;
        [Tooltip("Листьев в кроне/дропе маленькой пальмы (сейчас не спавнится).")]
        [Range(1, 40)] public int smallPalmCrownLeaves = 8;
        [Tooltip("§64.9: НЕПРИКОСНОВЕННЫЙ ЗАПАС пальм — стройка валит пальму только если видит БОЛЬШЕ этого числа. Пальма не отрастает, а кокосы = единственная вода: соак показал смерть всей колонии от жажды на 21-й день после того, как стройка забрала грове под ноль.")]
        [Range(0, 20)] public int palmGroveReserve = 4;

        [Header("Гейты стройки (§54.13)")]
        [Tooltip("Стройка ставится на паузу, когда голод/жажда выше этого (как lifeThreatened).")]
        [Range(0f, 1f)] public float buildNeedGate = 0.65f;
        [Tooltip("Стройку замораживает только СВЕЖАЯ опасность — виденная в последние столько тиков.")]
        [Range(0, 2400)] public int buildDangerFreshTicks = 600;

        [Header("Плот — гейт побега (§40.15)")]
        [Tooltip("Голод/жажда, выше которых девушка не идёт носить брёвна на плот (кокосовая экономика держит нужды ~0.55-0.7 — гейт 0.55 запирал эндшпиль навсегда).")]
        [Range(0f, 1f)] public float raftNeedGate = 0.7f;
        [Tooltip("Опасность в памяти отменяет рейс к плоту только в этом радиусе от девушки, тайлы (далёкий волк не отменяет берег).")]
        [Range(0, 12)] public int raftDangerRadiusTiles = 4;

        [Tooltip("Радиус правила «сначала подбери с земли, потом добывай ещё», тайлы. Меряется и от NPC, и от его стройки; общеостровной меры быть не может — восприятие помнит предметы по всей карте.")]
        [Range(1, 12)] public int pickUpFirstRadiusTiles = 4;

        [Header("Файбер / крафт-цены")]
        [Tooltip("Волокна с одного волокнистого растения (юкка).")]
        [Range(1, 12)] public int fiberPerPlant = 4;
        [Tooltip("Волокон на одну верёвку.")]
        [Range(1, 8)] public int ropeFiberCost = 1;
        [Tooltip("Волокон на один лоскут ткани.")]
        [Range(1, 12)] public int clothFiberCost = 4;
        [Tooltip("Палок на нож.")]
        [Range(1, 5)] public int knifeStickCost = 1;
        [Tooltip("Камней на нож.")]
        [Range(1, 5)] public int knifeStoneCost = 1;

        [Header("Туши и разделка")]
        [Tooltip("Сколько тиков туша лежит до разложения.")]
        [Range(300, 9600)] public int carcassDecayTicks = 2400;
        [Tooltip("Длительность разделки ножом в тиках.")]
        [Range(5, 200)] public int butcherDurationTicks = 30;
        [Tooltip("Сколько сырого мяса даёт туша (+ одна шкура).")]
        [Range(1, 8)] public int carcassMeatYield = 2;

        [Header("Порча мяса (наземные предметы)")]
        [Tooltip("Сырое мясо гниёт через столько тиков на земле.")]
        [Range(300, 24000)] public int meatRawSpoilTicks = 12000;
        [Tooltip("Жареное мясо держится дольше (жарка = консервация).")]
        [Range(300, 24000)] public int meatCookedSpoilTicks = 20000;
    }
}
