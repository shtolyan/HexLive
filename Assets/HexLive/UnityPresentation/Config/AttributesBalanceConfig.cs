using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// §76 Характеристики и навыки: кто она (шесть врождённых чисел, катятся от
    /// сида и фиксированы на всю жизнь) и что умеет (восемь навыков, растут от
    /// практики). Поле ассета = camelCase одноимённого статика Spec76.
    ///
    /// ⭐ Разброс по умолчанию НОЛЬ, и это не робость: при нём каждая девушка
    /// ровно на среднем, каждый множитель ровно 1.0f, каждое умножение точное —
    /// то есть игра побайтово до-§76, но весь новый код остаётся на горячем
    /// пути. Расширять только по лестнице соаков §76.7 (A/B тождественность →
    /// 0.15 → 0.30 → 0.50).
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Balance/Attributes", fileName = "AttributesBalance")]
    [MirrorTarget(typeof(Spec76))]
    public sealed class AttributesBalanceConfig : ScriptableObject
    {
        [Header("Общее (§76)")]
        [Tooltip("Выключить — характеристик и навыков как будто нет: все множители 1.0, поведение возвращается к до-§76 байт-в-байт.")]
        public bool enabled = true;
        [Tooltip("Навыки отдельным выключателем от характеристик, чтобы соак мог развести «сдвинули характеристики» и «сдвинули навыки».")]
        public bool skillsEnabled = true;

        [Header("Ролл характеристик")]
        [Tooltip("Человеческое среднее. Тело ровно на нём даёт все множители = 1, то есть до-§76 игру.")]
        [Range(0.1f, 0.9f)] public float attributeMean = 0.5f;
        [Tooltip("Полуширина полосы: характеристики ложатся РОВНО в [среднее − разброс, среднее + разброс], сумма у всех одинакова (бюджет). 0 = все ровно средние (доказуемый no-op). Целевое после лестницы соаков — 0.5, это ±15%.")]
        [Range(0f, 0.5f)] public float attributeSpread = 0f;

        [Header("Что даёт характеристика (±15% при разбросе 0.5 и усилении 0.3)")]
        [Tooltip("Сила → урон в ближнем бою.")]
        [Range(0f, 1.2f)] public float meleeDamageGain = 0.3f;
        [Tooltip("Стойкость → сколько урона доходит до плоти. Максимального HP в игре нет, поэтому «больше живучести» это меньше входящего урона (§76.3).")]
        [Range(0f, 1.2f)] public float incomingDamageGain = 0.3f;
        [Tooltip("Ловкость → скорость шага.")]
        [Range(0f, 1.2f)] public float moveSpeedGain = 0.3f;
        [Tooltip("Ловкость → скорость поворота.")]
        [Range(0f, 1.2f)] public float turnSpeedGain = 0.3f;
        [Tooltip("Ловкость → пауза между размахами (§29C.3). Замах и длина клипа НЕ трогаются: это анимация.")]
        [Range(0f, 1.2f)] public float attackCooldownGain = 0.3f;
        [Tooltip("Выносливость → потолок стамины.")]
        [Range(0f, 1.2f)] public float staminaCeilingGain = 0.3f;
        [Tooltip("Выносливость → расход стамины на работе и скорость падения энергии (сон).")]
        [Range(0f, 1.2f)] public float staminaDrainGain = 0.3f;
        [Tooltip("Выносливость → расход дыхания на бегу (§71).")]
        [Range(0f, 1.2f)] public float breathGain = 0.3f;
        [Tooltip("Неприхотливость → ставки голода и жажды.")]
        [Range(0f, 1.2f)] public float metabolismGain = 0.3f;
        [Tooltip("Неприхотливость → давление жары и холода. Скорость отогрева у огня не трогается — за это отвечает сам огонь.")]
        [Range(0f, 1.2f)] public float thermalToleranceGain = 0.3f;
        [Tooltip("Стойкость → скорость заживления ран и регенерации зон.")]
        [Range(0f, 1.2f)] public float healRateGain = 0.3f;
        [Tooltip("Стойкость → скорость кровопотери.")]
        [Range(0f, 1.2f)] public float bleedGain = 0.3f;
        [Tooltip("Сила/Смекалка → длительность работы (рубка и стройка от Силы, крафт и врачевание от Смекалки).")]
        [Range(0f, 1.2f)] public float workSpeedGain = 0.3f;
        [Tooltip("Сила выше этого даёт +1 слот переноски. Слоты целые, поэтому это полоса, а не кривая.")]
        [Range(0.5f, 1f)] public float carrySlotThreshold = 0.85f;

        [Header("Навыки")]
        [Tooltip("Опыт за тик фактически отработанного времени. 0.0004 — рубка пальмы (60 тиков) даёт 0.024 навыка, то есть дуга на всю жизнь колонии, а не на неделю.")]
        [Range(0f, 0.005f)] public float skillXpPerWorkTick = 0.0004f;
        [Tooltip("Опыт за попадание в ближнем бою. Драки редки и коротки, поэтому платят за событие, иначе Бой не сдвинулся бы вовсе.")]
        [Range(0f, 0.05f)] public float skillXpPerHit = 0.004f;
        [Tooltip("Насколько Смекалка ускоряет обучение.")]
        [Range(0f, 2f)] public float skillLearnWitsGain = 0.8f;
        [Tooltip("Затухание: прирост ∝ (1 − навык)^N. При 2 последняя четверть навыка стоит примерно как первые три, поэтому колония приходит к ОДНОЙ мастерице, а не к трём на максимуме всего.")]
        [Range(0.5f, 4f)] public float skillDiminishExp = 2f;
        [Tooltip("Что даёт полный навык ремесла/добычи/стройки: длительность ×(1 − навык × это).")]
        [Range(0f, 0.9f)] public float skillWorkSpeedGain = 0.35f;
        [Tooltip("Что даёт полный навык Боя: урон ×(1 + навык × это).")]
        [Range(0f, 2f)] public float skillDamageGain = 0.4f;
        [Tooltip("Что даёт полный навык Врачевания: сила перевязки ×(1 + навык × это).")]
        [Range(0f, 2f)] public float skillHealGain = 0.5f;
        [Tooltip("Что даёт полный навык Общения: утешение ×(1 + навык × это).")]
        [Range(0f, 2f)] public float skillSocialGain = 0.4f;
        [Tooltip("Ширина полосы для трассы SkillUp. Событие пишется только на пересечении полосы, иначе 240k-тиковый соак утонет в десятке тысяч строк на девушку. 0.1 = одна на показанный уровень.")]
        [Range(0.01f, 0.5f)] public float skillTraceBand = 0.1f;

        [Header("Перки (§76.6)")]
        [Tooltip("Характеристика не ниже этого даёт именной значок-дар. Значки живут ВНУТРИ вкладки §76, а не в ряду эффектов: двенадцать вечных чипов затопили бы ряд и сломали его смысл.")]
        [Range(0.5f, 1f)] public float perkHighBand = 0.70f;
        [Tooltip("Характеристика не выше этого даёт именной изъян.")]
        [Range(0f, 0.5f)] public float perkLowBand = 0.30f;
    }
}
