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
    /// <summary>Сколько вещей помещается в гардероб (как у сушилки — 8 плечиков).</summary>
    public const int WardrobeCapacity = 8;

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
}

}
