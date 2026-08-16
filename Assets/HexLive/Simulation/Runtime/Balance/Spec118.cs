namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Spec §118: Kenshi-inspired body damage, medicine and rescue.
/// All fields are mirrored into a balance asset and SimData; the feature can
/// be bisected without reverting saves because the state itself is always
/// present and defaults to neutral values.
/// </summary>
public class Spec118
{
    protected Spec118() { }
    public static bool Enabled = true;
    public static bool DamageTypesEnabled = true;
    public static bool MedicalEnabled = true;
    public static bool RescueEnabled = true;

    // §118.4 r2 (#166): спасают не только умирающих и коматозных, но и ЗАСТРЯВШУЮ
    // — в сознании, ноги не держат, а маршрута до дома с её проходимостью нет.
    // Выключатель отдельный: это единственная ветка спасения, которая срабатывает
    // у человека при здоровье, и её цена — занятая пара рук.
    public static bool StrandedRescueEnabled = true;
    public static bool SplintsEnabled = true;
    public static bool ProstheticsEnabled = true;

    public static float InstantBloodLossFactor = 0.20f;
    public static float SteadyBleedPerSlowTick = 0.012f;
    public static float ClotPerSlowTickLowToughness = 0.004f;
    public static float ClotPerSlowTickHighToughness = 0.008f;
    public static float DegenerationCutThreshold = 0.20f;
    public static float DegenerationStep = 0.10f;
    public static float DegenerationPerStep = 0.0015f;
    public static float BluntRecoveryPerSlowTick = 0.010f;
    public static float CutRecoveryPerSlowTick = 0.0033f;

    // ⭐ §118.2: во сколько раз МЕДЛЕННЕЕ рубцуется рана, которую никто не
    // перевязал. Свернувшаяся сама рана теперь тоже заживает (иначе разбитая в
    // ноль конечность стоит в нуле вечно и колонистка умирает от жажды, не в
    // силах открыть кокос), но повязка обязана оставаться заметно лучше — иначе
    // бинты перестают быть нужны. 0.25 = вчетверо дольше.
    public static float NaturalScarringFactor = 0.25f;
    public static float GroundRestHealMultiplier = 2f;
    public static float LeafBedHealMultiplier = 4f;
    public static float BasicBedHealMultiplier = 8f;
    public static float LeafBedDegenerationMultiplier = 0.5f;
    public static float BasicBedDegenerationMultiplier = 0f;

    public static int VitalKnockoutTicks = 80;
    public static float VitalWakeHealth = 0.05f;
    public static float BloodWakeHealth = 0.10f;
    public static float ComaThresholdMin = 0.10f;
    public static float ComaThresholdToughnessGain = 0.75f;
    public static float StumpBloodLoss = 0.35f;
    public static float StumpWoundSeverity = 0.35f;

    public static float HitBiasMax = 3f;
    public static float HitBiasGain = 1f;
    public static int HitBiasDecayTicks = 480;

    public static int BandageTicksNovice = 80;
    public static int BandageTicksExpert = 30;
    public static float DangerousRiseToughnessTraining = 0.002f;

    public static int SplintCraftTicks = 40;
    public static float SplintSupportNovice = 0.20f;
    public static float SplintSupportExpert = 0.50f;
    public static float SplintSeverCredit = 0.25f;

    public static float CarrySpeedMin = 0.35f;
    public static float CarrySpeedStrengthGain = 0.45f;
    public static float CarrySpeedMax = 0.80f;
    public static int RescueThreatRadiusTiles = 3;
    public static float RescueFightOdds = 0.50f;

    public static float WoodenArmFunction = 0.45f;
    public static float WoodenLegFunction = 0.60f;
    public static float MechanicalArmFunction = 0.80f;
    public static float MechanicalLegFunction = 0.80f;
    public static float WoodenProstheticDurability = 0.60f;
    public static float MechanicalProstheticDurability = 1.20f;
    public static int WoodenProstheticInstallTicks = 120;
    public static int MechanicalProstheticInstallTicks = 160;
    public static float MechanicalProstheticDropChance = 0.12f;
    public static float MechanicalPartsDropChance = 0.25f;
    public static int MechanicalLootMinWave = 3;
}

// Compatibility spelling used while the implementation plan still targeted
// the already-occupied §116. The fields are declared by Spec118, so balance
// reflection and SimData expose the canonical current section number.
public sealed class Spec116 : Spec118
{
    private Spec116() { }
}

}
