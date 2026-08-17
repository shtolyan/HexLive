namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Сколько мира влезает в одно описание (§144.8).
/// <para>
/// Появилось из замера: живая сводка колонистки весила <b>30–88 КБ одной
/// строкой</b> — до 350 объектов, из них 176 пальмовых листьев ПООДИНОЧКЕ.
/// Читателя два, и оба страдали одинаково: внешний агент тратил на это
/// контекст, которого потом не хватало на решение, а LLM-контур §32.15 платил
/// за то же токенами на каждом запросе.
/// </para>
/// <para>
/// ⭐ Это ПАРАМЕТР, а не второй сборщик. §144.3 требует, чтобы описание мира у
/// обоих путей управления строил один код: разойдись они — и вопрос «почему он
/// решил иначе» превратился бы в сравнение двух текстов, которых никто не писал
/// вместе. Поэтому <see cref="Unbounded"/> обязан давать ровно прежние байты, а
/// не «примерно то же»: только так видно, что сжатие ничего не выдумало.
/// </para>
/// </summary>
public readonly struct ContextBudget
{
    public ContextBudget(
        int maxObjectRows,
        int maxAgentRows,
        int maxKnownObjectRows,
        int maxKnownAgentRows,
        int maxDangerRows,
        int nearestPerDefinition,
        bool aggregate)
    {
        MaxObjectRows = maxObjectRows;
        MaxAgentRows = maxAgentRows;
        MaxKnownObjectRows = maxKnownObjectRows;
        MaxKnownAgentRows = maxKnownAgentRows;
        MaxDangerRows = maxDangerRows;
        NearestPerDefinition = nearestPerDefinition;
        Aggregate = aggregate;
    }

    public int MaxObjectRows { get; }

    public int MaxAgentRows { get; }

    public int MaxKnownObjectRows { get; }

    public int MaxKnownAgentRows { get; }

    public int MaxDangerRows { get; }

    /// <summary>
    /// Сколько экземпляров показывать поимённо в схлопнутой строке. Ноль
    /// экземпляров означал бы «есть 146 листьев, но взять нельзя ни один»:
    /// действовать агент может только по id.
    /// </summary>
    public int NearestPerDefinition { get; }

    /// <summary>Схлопывать ли одинаковые определения в одну строку.</summary>
    public bool Aggregate { get; }

    /// <summary>
    /// Прежнее поведение байт в байт. Существует ради теста: сжатие обязано
    /// доказать, что оно ничего не потеряло сверх заявленного.
    /// </summary>
    public static ContextBudget Unbounded => new(
        maxObjectRows: int.MaxValue,
        maxAgentRows: int.MaxValue,
        maxKnownObjectRows: int.MaxValue,
        maxKnownAgentRows: int.MaxValue,
        maxDangerRows: int.MaxValue,
        nearestPerDefinition: 0,
        aggregate: false);

    /// <summary>
    /// Что видит и агент, и LLM-контур по умолчанию.
    /// <para>
    /// Числа выбраны от вопроса «по чему он реально может действовать». По
    /// 146-му пальмовому листу — не может: чтобы до него дойти, надо пройти
    /// мимо первых трёх. По 25-му классу объектов вокруг — тоже: решение
    /// принимается по ближайшему костру, а не по двадцать пятому камню.
    /// </para>
    /// </summary>
    public static ContextBudget Default => new(
        maxObjectRows: 24,
        maxAgentRows: 12,
        maxKnownObjectRows: 24,
        maxKnownAgentRows: 12,
        maxDangerRows: 8,
        nearestPerDefinition: 3,
        aggregate: true);
}

}
