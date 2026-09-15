namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §167: указания и лидерство — просьба, согласие и власть, которую слушают.
///
/// <para>
/// Здесь <c>const</c>, как в <see cref="Spec133"/>: константа невидима для
/// BalanceParity/SimDataFreshness/BalanceKnobHygiene и фича живёт headless.
/// <c>SpecDream</c> — антипример: <c>static</c>-поля без строки в
/// <c>BalanceReflection.BalanceClasses</c> молча не экспортируются вовсе.
/// Промоушен в нобы: <c>public static</c> + класс в BalanceClasses +
/// зеркало в конфиг-ассете + переснять экспорт.
/// </para>
/// </summary>
public static class Spec167
{
    /// <summary>Общий выключатель слоя: false — тема-просьба не срабатывает, тяга 0.</summary>
    public const bool Enabled = true;

    // ── Тяга §23.12 ──────────────────────────────────────────────────────
    /// <summary>
    /// Слагаемое на ВЕДУЩУЮ цель указания. Как <c>SpecDream.BuildPull</c> (0.5):
    /// лид ≈ 1.0–1.25 — выше Sit/Dress/Socialize, ниже любой нужды ≥0.7 с
    /// emergency. Постоянное слагаемое не мерцает и не даёт churn §35.4a.
    /// </summary>
    public const float Pull = 0.45f;

    /// <summary>Доля тяги на цепочку сырья (0.27): лид всегда впереди фидеров.</summary>
    public const float FeederPullShare = 0.6f;

    /// <summary>Сколько держится обещание: 2400 тиков ≈ игровой день по §19.7B.</summary>
    public const int DirectiveTicks = 2400;

    // ── Готовность (детерминизм, без броска; форма WearPermissionMath) ────
    public const float WillingnessThreshold = 0.6f;
    public const float WillingnessBase = 0.35f;
    public const float AffinityWeight = 0.4f;
    public const float TrustWeight = 0.2f;
    public const float AuthorityWeight = 0.3f;
    public const float IndustryWeight = 0.15f;

    /// <summary>Своя нужда выше этого — «мне самой сейчас не до того», отказ без обиды.</summary>
    public const float RefuseNeedThreshold = 0.55f;
    public const float RefuseEnergyThreshold = 0.35f;

    // ── Запас лагеря ─────────────────────────────────────────────────────
    public const int StockFoodTarget = 4;
    public const int StockWaterTarget = 3;
    public const int StockWoodTarget = 6;

    /// <summary>Радиус ценза вокруг очага лагеря (тайлы) — тот же, что «в лагере» §72.</summary>
    public static int StockRadiusTiles => Spec72.MaxCampRadiusTiles;

    /// <summary>Кэш ценза: одна переоценка на 16 тиков (medium×4).</summary>
    public const int CensusBucketTicks = 16;

    // ── Крик ─────────────────────────────────────────────────────────────
    public const int ShoutRadiusTiles = 8;
    public const float ShoutPenalty = 0.1f;
    public const int MaxShoutResponders = 3;

    // ── Authority (§28.2) ────────────────────────────────────────────────
    public const float AuthorityOnAccept = 0.05f;
    public const float AuthorityOnDone = 0.10f;

    /// <summary>Остывает как дружба (§94): лидерство надо подтверждать делом.</summary>
    public const float AuthorityDriftPerTick = 0.0002f;

    // ── Инициатива (фаза B) ──────────────────────────────────────────────
    /// <summary>
    /// §167.7: сама видит нехватку и просит. ВЫКЛЮЧАТЕЛЬ ФИЧИ, не ноб (в
    /// BalanceClasses не входит и в simdata не едет): включение меняет золотой
    /// трейс и принимается сознательно по соаку (<c>hexsoak --autonomous-ask</c>).
    /// </summary>
    public static bool AutonomousAsk = false;
    public const int AskCooldownTicks = 1200;
    public const int MaxHoldersPerKind = 2;
    public const float InitiativeShare = 0.5f;
    public const float InitiativePull = 0.3f;

    /// <summary>§167.8: сколько сим ждёт ответа агента, прежде чем решить сам.</summary>
    public static int PendingTimeoutTicks => Spec49.TalkDuration;
}

}
