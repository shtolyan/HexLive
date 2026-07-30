using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Угрозы и жёсткие последствия: §50 потеря конечностей, §57 крик о помощи
    /// и защита друга, §62 дальнее обнаружение врага, каннибализм и §56
    /// предация (SimBalance-блок). Поле ассета = camelCase одноимённого
    /// статика; исключения помечены [MirrorField].
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/Threat", fileName = "ThreatBalance")]
    [MirrorTarget(typeof(Spec50))]
    [MirrorTarget(typeof(Spec57))]
    [MirrorTarget(typeof(Spec62))]
    [MirrorTarget(typeof(SimBalance))]
    public sealed class ThreatBalanceConfig : ScriptableObject
    {
        [Header("Потеря конечности (§50)")]
        [Tooltip("Включить ампутацию (потерю руки/ноги). Выкл — механика полностью спит.")]
        [MirrorField(typeof(Spec50), "Enabled")]
        public bool limbLossEnabled = true;
        [Tooltip("Порог урона ОДНОГО удара, при котором добитая до 0 конечность отрывается сразу (акула 0.2). Укус собаки ~0.06 сам по себе не рвёт.")]
        [Range(0.05f, 0.5f)] public float limbSeverThreshold = 0.14f;
        [Tooltip("Шанс (0..1), что мелкий укус, ДОБИВШИЙ измолотую ногу до 0, оторвёт её.")]
        [Range(0f, 1f)] public float grindSeverChance = 0.25f;
        [Tooltip("Мгновенная кровопотеря (доля шкалы Blood) в момент отрыва конечности.")]
        [Range(0f, 1f)] public float limbSeverBloodLoss = 0.4f;
        [Tooltip("Глубина культёвой раны (тяжесть) — держит кровотечение.")]
        [Range(0f, 1f)] public float limbSeverWoundSeverity = 0.35f;
        [Tooltip("Для расчёта кровотечения культя считается конечностью с этим HP (не 0): разовая кровопотеря уже снята при отрыве, культя лишь сочится. 0 = старое поведение (ампутация = смертный приговор).")]
        [Range(0f, 0.4f)] public float stumpBleedPartFloor = 0.35f;
        [Tooltip("Множитель силы удара для ОТРУБЛЕННОЙ РУКИ. Одна рука — ×это, обе — ×это².")]
        [Range(0f, 0.5f)] public float severedLimbMobilityMult = 0.15f;
        [Tooltip("Доля скорости ходьбы при ползании (потеряна одна/обе ноги).")]
        [Range(0.1f, 1f)] public float crawlSpeedFactor = 0.3333f;
        [Tooltip("Сколько тиков отрубленная конечность лежит в мире до разложения (4800 ≈ 2 игровых дня).")]
        [Range(600f, 9600f)] public float severedLimbDecayTicks = 4800f;
        [Tooltip("Шанс (0..1) за медленный тик потерять ногу на тайле-хазарде (риф/капкан).")]
        [Range(0f, 1f)] public float hazardSeverChance = 1f;

        [Header("Крик о помощи (§57)")]
        [Tooltip("Включить крик о помощи в бою (подмога прибегает).")]
        public bool helpCryEnabled = true;
        [Tooltip("Радиус слышимости крика в тайлах.")]
        [Range(1, 20)] public int helpCryRadiusTiles = 6;
        [Tooltip("Кулдаун между криками в тиках.")]
        [Range(0, 1200)] public int helpCryCooldownTicks = 240;
        [Tooltip("Максимум откликнувшихся на один крик.")]
        [Range(1, 8)] public int maxHelpCryResponders = 2;
        [Tooltip("Порог решения откликнуться (взвешенная оценка).")]
        [Range(0f, 1f)] public float helpCryDecisionThreshold = 0.56f;
        [Tooltip("Своё здоровье, ниже которого на помощь не бегут.")]
        [Range(0f, 1f)] public float helpCryHealthGate = 0.65f;
        [Tooltip("Включить защиту друга (guard рядом с близким).")]
        public bool friendGuardEnabled = true;
        [Tooltip("Радиус, в котором друг считается «рядом», тайлы.")]
        [Range(1, 20)] public int friendGuardRadiusTiles = 6;
        [Tooltip("Минимальная симпатия, чтобы считаться другом для защиты.")]
        [Range(-1f, 1f)] public float friendGuardAffinity = 0.25f;

        [Header("Дальнее обнаружение врага (§62)")]
        [Tooltip("Включить раннее обнаружение (⚠️, атака первой или обход).")]
        public bool threatAlertEnabled = true;
        [Tooltip("Радиус обнаружения врага в тайлах.")]
        [Range(1, 20)] public int spotRadiusTiles = 4;
        [Tooltip("Кулдаун ⚠️-реакции в тиках.")]
        [Range(0, 2400)] public int cueCooldownTicks = 600;
        [Tooltip("Минимальное здоровье всех костей (доля), чтобы решиться атаковать первой.")]
        [Range(0f, 1f)] public float fitBoneHealth = 0.8f;
        [Tooltip("Атака первой — только если врагов не больше этого.")]
        [Range(1, 5)] public int attackMaxPack = 1;
        [Tooltip("Сколько тиков держится решение атаковать (не передумывает каждый тик).")]
        [Range(0, 1200)] public int attackLockTicks = 240;
        [Tooltip("Радиус danger-кольца вокруг врага для обхода, тайлы.")]
        [Range(1, 6)] public int dangerRingTiles = 2;
        [Tooltip("Штраф стоимости шага в danger-кольце для pathfinder'а.")]
        public long dangerStepCost = 80L;

        [Header("Бой в углу — застрявший flee (§29C.4A)")]
        [Tooltip("Сколько тиков подряд собака держит убегающую в мельке, прежде чем побег признаётся провалившимся и она встаёт драться до победного (4 тика/с). Меньше = решается быстрее.")]
        [Range(4, 120)] public int fleeStallTicks = 16;
        [Tooltip("Насколько тиков держится решение драться после последнего тика контакта (перезаряжается каждый тик боя; гасит качели flee↔бой, спадает когда враг мёртв/ушёл).")]
        [Range(4, 240)] public int fightCommitGraceTicks = 40;

        [Header("Тупиковая стойка — волк рядом, но не достаёт (§29C.4A)")]
        [Tooltip("Сколько тиков ПОДРЯД здоровая девочка стоит в стойке «дерётся» против волка на соседнем тайле, который так и не дошёл до мелька (ни укуса, ни удара), прежде чем она перестаёт держать стойку и возвращается к делам. Реальный контакт сбрасывает счётчик.")]
        [Range(8, 240)] public int standoffReleaseTicks = 40;
        [Tooltip("Сколько тиков после «отпускания» стойки девочка игнорирует повторный захват стойки (успевает уйти/попить/перепланировать). Потом стойка взводится заново, если волк всё ещё рядом.")]
        [Range(40, 1200)] public int standoffReleaseGraceTicks = 240;

        [Header("Каннибализм (разделка найденного тела)")]
        [Tooltip("Разрешить разделку тела соседки (только при настоящем голоде).")]
        public bool cannibalismEnabled = true;
        [Tooltip("Штраф комфорта за разделку тела.")]
        [Range(0f, 1f)] public float cannibalismComfortPenalty = 0.25f;
        [Tooltip("Голод, выше которого возможна разделка тела.")]
        [Range(0.5f, 1f)] public float cannibalizeHungerGate = 0.85f;

        [Header("Предация (§56 — убить слабейшую ради мяса)")]
        [Tooltip("Включить предацию. Выкл — поведение как до §56, байт-в-байт.")]
        public bool predationEnabled = true;
        [Tooltip("Голод-гейт предации (строго выше гейта разделки — сначала едят найденное тело).")]
        [Range(0.5f, 1f)] public float predationHungerGate = 0.95f;
        [Tooltip("Потолок черты сострадания: только чёрствые (≤ этого) рассматривают убийство.")]
        [Range(0f, 1f)] public float predationCompassionCeiling = 0.45f;
        [Tooltip("Базовый скор цели — ниже любой настоящей еды.")]
        [Range(0f, 0.5f)] public float predationBaseScore = 0.05f;
        [Tooltip("Урон за удар по жизненно важному (решительный: слабый удар проигрывает гонку с голодом).")]
        [Range(0f, 1f)] public float predationStrikePerPass = 0.35f;
        [Tooltip("Тяжёлый штраф комфорта убийце (ср. 0.25 за разделку найденного).")]
        [Range(0f, 1f)] public float predationComfortPenalty = 0.6f;
        [Tooltip("Обвал симпатии каждого свидетеля к убийце.")]
        [Range(0f, 1f)] public float predationWitnessAffinityLoss = 0.8f;
        [Tooltip("Жертва отбивается и БЕЖИТ в укрытие, когда её здоровье падает ниже этого.")]
        [Range(0f, 1f)] public float predationFleeHealth = 0.6f;
    }
}
