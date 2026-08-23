namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §133: своя одежда — гардероб, раздевание у дома, разрешение и скромность.
///
/// <para>
/// ⭐ Здесь намеренно <c>const</c>, а не <c>static</c>-поля, как в Spec118 и
/// соседях. Тюнимый ноб обязан иметь зеркало в Unity-ассете и строку в
/// <c>SimData/simdata.json</c> (гейты BalanceParity/SimDataFreshness/
/// BalanceKnobHygiene), а экспорт делается только из редактора. Константа для
/// <see cref="Content.BalanceReflection.IsTunable"/> невидима (она отсекает
/// <c>IsLiteral</c>), поэтому фича целиком живёт headless. Цена честная: пока
/// это не крутится ползунком в инспекторе. Промоушен в нобы = завести поля
/// <c>public static</c>, добавить класс в <c>BalanceReflection.BalanceClasses</c>,
/// смиррорить в конфиг-ассет и переснять экспорт.
/// </para>
/// </summary>
public static class Spec133
{
    // ── Гардероб ─────────────────────────────────────────────────────────
    /// <summary>Сколько вещей помещается в гардероб: двенадцать реальных плечиков.</summary>
    public const int WardrobeCapacity = 12;

    /// <summary>
    /// Сушка в гардеробе при ГОРЯЩЕМ очаге в доме — вровень с уличной сушилкой
    /// (×5). Смысл в том, что тепло даёт очаг, а крыша уже отсекает дождь.
    /// </summary>
    public const float WardrobeDryMultiplierLit = 5f;

    /// <summary>
    /// Очаг потух — остаётся глухой шкаф: сохнет медленно, но всё же лучше, чем
    /// куча на мокрой земле.
    /// </summary>
    public const float WardrobeDryMultiplierUnlit = 1.5f;

    // ── Где раздеваются ──────────────────────────────────────────────────
    /// <summary>
    /// Радиус «у дома» в тайлах: дальше этого одежда считается разбросанной по
    /// карте, и её несут домой (§133 StowClothes).
    /// </summary>
    public const int HomeStowRadiusTiles = 3;

    /// <summary>
    /// До какой духоты раздеваться в жару ходят ДОМОЙ. Выше — снимают на месте:
    /// когда тепловой удар на пороге, идти через полострова в гардероб глупо, и
    /// брошенная в поле кофта — меньшее зло. Это и есть «крайний случай».
    /// </summary>
    public const float UndressAtHomeMaxDiscomfort = 0.85f;

    // ── Закреплённый выбранный комплект ──────────────────────────────────
    /// <summary>
    /// Период фоновой сверки: 16 тиков = около четырёх секунд штатного времени.
    /// Активный возврат проверяется каждый medium-pass лишь чтобы не оборвать
    /// уже начатый путь; совпадающий комплект не сканируется каждый тик.
    /// </summary>
    public const int OutfitAuditIntervalTicks = 16;

    /// <summary>Небольшой разброс первого аудита между NPC.</summary>
    public const int OutfitAuditStaggerTicks = 4;

    /// <summary>
    /// Ставка восстановления комплекта: выше бытовых дел и обычного сна, ниже
    /// критической еды/воды, боя и спасения.
    /// </summary>
    public const float OutfitMaintenanceNeed = 1.2f;

    /// <summary>
    /// На тело возвращается действительно высохшая вещь; 5% оставлены как
    /// защита от float-хвоста естественной сушки.
    /// </summary>
    public const float OutfitRedressWetnessMax = 0.05f;
}

}
