using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Bridges the saved <see cref="HexTuningConfig"/> asset and the tuning
    /// statics scattered across the assemblies (HexHopTuning in the engine-
    /// free simulation, plus the presentation-side water/swim knobs). Apply
    /// pushes the asset into the statics; Capture reads the live statics back
    /// (for the "save" button).
    /// </summary>
    public static class HexTuning
    {
        // Under a Resources folder so the shipping game can load it with no
        // scene reference.
        public const string ResourcePath = "HexLive/HexTuningConfig";

        public static void Apply(HexTuningConfig c)
        {
            if (c == null)
            {
                return;
            }

            HexHopTuning.HopSeconds = c.hopSeconds;
            HexHopTuning.DownHopSeconds = c.downHopSeconds;
            HexHopTuning.TakeoffSeconds = c.hopTakeoffSeconds;
            HexHopTuning.LandingSeconds = c.hopLandingSeconds;
            HexHopTuning.EdgePadding = c.hopEdgePadding;
            HexHopTuning.DownHopUp = c.hopDownUp;
            HexHopTuning.DownFallStartFrac = c.hopDownFallStartFrac;
            HexHopTuning.DivePlungeDepth = c.divePlungeDepth;

            MovementSystem.SwimEntryPauseSeconds = c.swimEntryPauseSeconds;
            MovementSystem.SwimSpeedFactor = c.swimSpeedFactor;

            SwimVisuals.SinkDepth = c.sinkDepth;
            SwimVisuals.WadeDepth = c.wadeDepth;
            NpcActorView.SwimBodyLift = c.swimBodyLift;
            NpcActorView.LedgeSeatLift = c.ledgeSeatLift;
            NpcActorView.LedgeSeatBack = c.ledgeSeatBack;

            WaterWave.Amplitude = c.waveAmplitude;
            WaterWave.Frequency = c.waveFrequency;
            WaterWave.Speed = c.waveSpeed;

            // §49 sleep / social / water overhaul.
            Spec49.SleepComfortGrassNight = c.sleepComfortGrass;
            Spec49.SleepComfortLeafNight = c.sleepComfortLeaf;
            Spec49.SleepComfortBedNight = c.sleepComfortBed;
            Spec49.SleepComfortFireBonusNight = c.sleepComfortFireBonus;
            Spec49.SleepComfortSunPenaltyNight = c.sleepComfortSunPenalty;
            Spec49.SleepComfortRainPenaltyNight = c.sleepComfortRainPenalty;
            Spec49.SmartSleepSpot = c.smartSleepSpot;
            Spec49.ThermalSleepFactor = c.thermalSleepFactor;
            Spec49.TalkDuration = c.talkDuration;
            Spec49.TalkInitGain = c.talkInitGain;
            Spec49.TalkListenGain = c.talkListenGain;
            Spec49.AmbientGain = c.ambientSocialGain;
            Spec49.SocializeNeedGate = c.socializeNeedGate;
            Spec49.ShadeCooling = -c.shadeCooling; // asset stores magnitude, static is signed
            Spec49.BoilThirstCeiling = c.boilThirstCeiling;
            Spec49.BoilChainWeight = c.boilChainWeight;
            Spec49.WetDragPerGarment = c.wetDragPerGarment;
            Spec49.WetDragFloor = c.wetDragFloor;
            Spec49.WetComfortPenalty = c.wetComfortPenalty;

            // §50 limb loss / amputation.
            Spec50.Enabled = c.limbLossEnabled;
            Spec50.LimbSeverThreshold = c.limbSeverThreshold;
            Spec50.GrindSeverChance = c.limbGrindSeverChance;
            Spec50.LimbSeverBloodLoss = c.limbSeverBloodLoss;
            Spec50.LimbSeverWoundSeverity = c.limbSeverWoundSeverity;
            Spec50.SeveredLimbMobilityMult = c.severedLimbMobilityMult;
            Spec50.CrawlSpeedFactor = c.crawlSpeedFactor;
            Spec50.SeveredLimbDecayTicks = c.severedLimbDecayTicks;
            Spec50.HazardSeverChance = c.hazardSeverChance;

            // §53 compassion & mutual aid.
            Spec53.Enabled = c.compassionEnabled;
            Spec53.CompassionRate = c.compassionRate;
            Spec53.RecoverRate = c.compassionRecoverRate;
            Spec53.AidWeight = c.compassionAidWeight;
            Spec53.PressureWeight = c.compassionPressureWeight;
            Spec53.SelfHungerGate = c.compassionSelfHungerGate;
            Spec53.SelfHealthGate = c.compassionSelfHealthGate;
            Spec53.SufferingThreshold = c.compassionSufferingThreshold;
            Spec53.FeedRelief = c.compassionFeedRelief;
            Spec53.TreatHeal = c.compassionTreatHeal;
            Spec53.TreatBlood = c.compassionTreatBlood;
            Spec53.MedicateHeal = c.compassionMedicateHeal;
            Spec53.ConsoleStressRelief = c.compassionConsoleStressRelief;
            Spec53.AidRelationshipGain = c.compassionAidRelationshipGain;
            Spec53.AidDuration = c.compassionAidDuration;
            Spec53.AidSelfRestore = c.compassionAidSelfRestore;
            Spec53.TraitMin = c.compassionTraitMin;
            Spec53.TraitMax = c.compassionTraitMax;

            // Core character balance (needs / thresholds / restores / thermal /
            // health / blood / combat) — SimBalance.
            SimBalance.HungerRate = c.hungerRate;
            SimBalance.ThirstRate = c.thirstRate;
            SimBalance.EnergyRate = c.energyRate;
            SimBalance.ComfortRate = c.comfortRate;
            SimBalance.SocialRate = c.socialRate;
            SimBalance.SweatThirstFactor = c.sweatThirstFactor;

            SimBalance.GetFoodHungerThreshold = c.getFoodHungerThreshold;
            SimBalance.SleepEnergyThreshold = c.sleepEnergyThreshold;
            SimBalance.SitComfortThreshold = c.sitComfortThreshold;
            SimBalance.SitNeedGate = c.sitNeedGate;
            SimBalance.SleepInterruptHunger = c.sleepInterruptHunger;
            SimBalance.SleepInterruptThirst = c.sleepInterruptThirst;
            SimBalance.DressThermalThreshold = c.dressThermalThreshold;
            SimBalance.DressColdTemp = c.dressColdTemp;
            SimBalance.DressWarmthCeiling = c.dressWarmthCeiling;

            SimBalance.StarvingEnterThreshold = c.starvingEnterThreshold;
            SimBalance.StarvingClearThreshold = c.starvingClearThreshold;
            SimBalance.StarvingBoost = c.starvingBoost;
            SimBalance.StarveDeathThreshold = c.starveDeathThreshold;
            SimBalance.StarveDamageBoth = c.starveDamageBoth;
            SimBalance.StarveDamageOne = c.starveDamageOne;

            SimBalance.HealHungerGate = c.healHungerGate;
            SimBalance.HealthRegenPerTick = c.healthRegenPerTick;

            SimBalance.BleedRateFactor = c.bleedRateFactor;
            SimBalance.BloodRefillPerTick = c.bloodRefillPerTick;
            SimBalance.BandageBloodThreshold = c.bandageBloodThreshold;

            SimBalance.RawWaterSickChance = c.rawWaterSickChance;
            SimBalance.SicknessDurationTicks = c.sicknessDurationTicks;
            SimBalance.SicknessDamagePerBout = c.sicknessDamagePerBout;
            SimBalance.SicknessDamageBudgetCap = c.sicknessDamageBudgetCap;
            SimBalance.SickTorsoPerSlowTick = c.sickTorsoPerSlowTick;
            SimBalance.SickTorsoFloor = c.sickTorsoFloor;
            SimBalance.SickComfortPerSlowTick = c.sickComfortPerSlowTick;

            SimBalance.DrinkBottleDurationTicks = c.drinkBottleDurationTicks;
            SimBalance.DrinkThirstRaw = c.drinkThirstRaw;
            SimBalance.DrinkThirstBoiled = c.drinkThirstBoiled;
            SimBalance.DrinkComfortBoiled = c.drinkComfortBoiled;
            SimBalance.BottleCapacity = c.bottleCapacity;   // §52
            SimBalance.CoconutWaterCapacity = c.coconutWaterCapacity;
            SimBalance.HandSlots = c.handSlots;             // §52

            SimBalance.GroundSleepEnergy = c.groundSleepEnergy;
            SimBalance.GroundSitEnergy = c.groundSitEnergy;
            SimBalance.GroundSitComfort = c.groundSitComfort;
            SimBalance.GroundSitComfortLedge = c.groundSitComfortLedge;
            SimBalance.BedEnergy = c.bedEnergy;
            SimBalance.LeafBedEnergy = c.leafBedEnergy;
            SimBalance.ChairComfort = c.chairComfort;
            SimBalance.ChairEnergy = c.chairEnergy;

            SimBalance.CoconutHunger = c.coconutHunger;
            SimBalance.CoconutThirst = c.coconutThirst;
            SimBalance.CookedMeatHunger = c.cookedMeatHunger;

            SimBalance.ColdBandTemp = c.coldBandTemp;
            SimBalance.HotBandTemp = c.hotBandTemp;
            SimBalance.ColdPressureSlope = c.coldPressureSlope;
            SimBalance.HeatPressureSlope = c.heatPressureSlope;
            SimBalance.ThermalPressureCap = c.thermalPressureCap;
            SimBalance.ThermalComfyRecovery = c.thermalComfyRecovery;
            SimBalance.ThermalDamageGate = c.thermalDamageGate;
            SimBalance.ThermalHpHit = c.thermalHpHit;
            SimBalance.IndoorWarmthBonus = c.indoorWarmthBonus;
            SimBalance.WaterCoolBonus = c.waterCoolBonus;
            SimBalance.FireWarmthRange1 = c.fireWarmthRange1;
            SimBalance.FireWarmthRange2 = c.fireWarmthRange2;

            SimBalance.TanRate = c.tanRate;
            SimBalance.SunburnRate = c.sunburnRate;
            SimBalance.SunExposureRate = c.sunExposureRate;
            SimBalance.SunburnBurnDamage = c.sunburnBurnDamage;

            SimBalance.HygieneWashGain = c.hygieneWashGain;
            SimBalance.HygieneDriftLoss = c.hygieneDriftLoss;

            SimBalance.StaminaRestGain = c.staminaRestGain;
            SimBalance.StaminaWorkDrain = c.staminaWorkDrain;
            SimBalance.StaminaIdleGain = c.staminaIdleGain;
            SimBalance.StressUpRate = c.stressUpRate;
            SimBalance.StressDownRate = c.stressDownRate;

            SimBalance.BaseTemperature = c.baseTemperature;
            SimBalance.TemperatureAmplitude = c.temperatureAmplitude;
            SimBalance.RainTempDrop = c.rainTempDrop;

            SimBalance.HealPerSlowTick = c.woundHealPerSlowTick;
            SimBalance.MaxWounds = c.maxWounds;
            SimBalance.GashesPerHit = c.gashesPerHit;
            SimBalance.MinSplittableDamage = c.minSplittableDamage;

            SimBalance.BiteDamagePerPass = c.dogBiteDamage;
            SimBalance.NpcStrikePerPass = c.dogStrikeDamage;
            SimBalance.RaidChancePerDay = c.dogRaidChancePerDay;
            SimBalance.RaidPackSize = c.dogRaidPackSize;
            SimBalance.AggroRadiusTiles = c.dogAggroRadiusTiles;
            SimBalance.RoamChance = c.dogRoamChance;
            SimBalance.SharkBiteDamage = c.sharkBiteDamage;
        }

        // Read the current live statics INTO the asset (the "save current
        // tuning" direction). Mirror of Apply.
        public static void Capture(HexTuningConfig c)
        {
            if (c == null)
            {
                return;
            }

            c.hopSeconds = HexHopTuning.HopSeconds;
            c.downHopSeconds = HexHopTuning.DownHopSeconds;
            c.hopTakeoffSeconds = HexHopTuning.TakeoffSeconds;
            c.hopLandingSeconds = HexHopTuning.LandingSeconds;
            c.hopEdgePadding = HexHopTuning.EdgePadding;
            c.hopDownUp = HexHopTuning.DownHopUp;
            c.hopDownFallStartFrac = HexHopTuning.DownFallStartFrac;
            c.divePlungeDepth = HexHopTuning.DivePlungeDepth;

            c.swimEntryPauseSeconds = MovementSystem.SwimEntryPauseSeconds;
            c.swimSpeedFactor = MovementSystem.SwimSpeedFactor;

            c.sinkDepth = SwimVisuals.SinkDepth;
            c.wadeDepth = SwimVisuals.WadeDepth;
            c.swimBodyLift = NpcActorView.SwimBodyLift;
            c.ledgeSeatLift = NpcActorView.LedgeSeatLift;
            c.ledgeSeatBack = NpcActorView.LedgeSeatBack;

            c.waveAmplitude = WaterWave.Amplitude;
            c.waveFrequency = WaterWave.Frequency;
            c.waveSpeed = WaterWave.Speed;

            // §49 sleep / social / water overhaul.
            c.sleepComfortGrass = Spec49.SleepComfortGrassNight;
            c.sleepComfortLeaf = Spec49.SleepComfortLeafNight;
            c.sleepComfortBed = Spec49.SleepComfortBedNight;
            c.sleepComfortFireBonus = Spec49.SleepComfortFireBonusNight;
            c.sleepComfortSunPenalty = Spec49.SleepComfortSunPenaltyNight;
            c.sleepComfortRainPenalty = Spec49.SleepComfortRainPenaltyNight;
            c.smartSleepSpot = Spec49.SmartSleepSpot;
            c.thermalSleepFactor = Spec49.ThermalSleepFactor;
            c.talkDuration = Spec49.TalkDuration;
            c.talkInitGain = Spec49.TalkInitGain;
            c.talkListenGain = Spec49.TalkListenGain;
            c.ambientSocialGain = Spec49.AmbientGain;
            c.socializeNeedGate = Spec49.SocializeNeedGate;
            c.shadeCooling = -Spec49.ShadeCooling;
            c.boilThirstCeiling = Spec49.BoilThirstCeiling;
            c.boilChainWeight = Spec49.BoilChainWeight;
            c.wetDragPerGarment = Spec49.WetDragPerGarment;
            c.wetDragFloor = Spec49.WetDragFloor;
            c.wetComfortPenalty = Spec49.WetComfortPenalty;

            // §50 limb loss / amputation.
            c.limbLossEnabled = Spec50.Enabled;
            c.limbSeverThreshold = Spec50.LimbSeverThreshold;
            c.limbGrindSeverChance = Spec50.GrindSeverChance;
            c.limbSeverBloodLoss = Spec50.LimbSeverBloodLoss;
            c.limbSeverWoundSeverity = Spec50.LimbSeverWoundSeverity;
            c.severedLimbMobilityMult = Spec50.SeveredLimbMobilityMult;
            c.crawlSpeedFactor = Spec50.CrawlSpeedFactor;
            c.severedLimbDecayTicks = Spec50.SeveredLimbDecayTicks;
            c.hazardSeverChance = Spec50.HazardSeverChance;

            // §53 compassion & mutual aid.
            c.compassionEnabled = Spec53.Enabled;
            c.compassionRate = Spec53.CompassionRate;
            c.compassionRecoverRate = Spec53.RecoverRate;
            c.compassionAidWeight = Spec53.AidWeight;
            c.compassionPressureWeight = Spec53.PressureWeight;
            c.compassionSelfHungerGate = Spec53.SelfHungerGate;
            c.compassionSelfHealthGate = Spec53.SelfHealthGate;
            c.compassionSufferingThreshold = Spec53.SufferingThreshold;
            c.compassionFeedRelief = Spec53.FeedRelief;
            c.compassionTreatHeal = Spec53.TreatHeal;
            c.compassionTreatBlood = Spec53.TreatBlood;
            c.compassionMedicateHeal = Spec53.MedicateHeal;
            c.compassionConsoleStressRelief = Spec53.ConsoleStressRelief;
            c.compassionAidRelationshipGain = Spec53.AidRelationshipGain;
            c.compassionAidDuration = Spec53.AidDuration;
            c.compassionAidSelfRestore = Spec53.AidSelfRestore;
            c.compassionTraitMin = Spec53.TraitMin;
            c.compassionTraitMax = Spec53.TraitMax;

            // Core character balance (SimBalance) — mirror of Apply.
            c.hungerRate = SimBalance.HungerRate;
            c.thirstRate = SimBalance.ThirstRate;
            c.energyRate = SimBalance.EnergyRate;
            c.comfortRate = SimBalance.ComfortRate;
            c.socialRate = SimBalance.SocialRate;
            c.sweatThirstFactor = SimBalance.SweatThirstFactor;

            c.getFoodHungerThreshold = SimBalance.GetFoodHungerThreshold;
            c.sleepEnergyThreshold = SimBalance.SleepEnergyThreshold;
            c.sitComfortThreshold = SimBalance.SitComfortThreshold;
            c.sitNeedGate = SimBalance.SitNeedGate;
            c.sleepInterruptHunger = SimBalance.SleepInterruptHunger;
            c.sleepInterruptThirst = SimBalance.SleepInterruptThirst;
            c.dressThermalThreshold = SimBalance.DressThermalThreshold;
            c.dressColdTemp = SimBalance.DressColdTemp;
            c.dressWarmthCeiling = SimBalance.DressWarmthCeiling;

            c.starvingEnterThreshold = SimBalance.StarvingEnterThreshold;
            c.starvingClearThreshold = SimBalance.StarvingClearThreshold;
            c.starvingBoost = SimBalance.StarvingBoost;
            c.starveDeathThreshold = SimBalance.StarveDeathThreshold;
            c.starveDamageBoth = SimBalance.StarveDamageBoth;
            c.starveDamageOne = SimBalance.StarveDamageOne;

            c.healHungerGate = SimBalance.HealHungerGate;
            c.healthRegenPerTick = SimBalance.HealthRegenPerTick;

            c.bleedRateFactor = SimBalance.BleedRateFactor;
            c.bloodRefillPerTick = SimBalance.BloodRefillPerTick;
            c.bandageBloodThreshold = SimBalance.BandageBloodThreshold;

            c.rawWaterSickChance = SimBalance.RawWaterSickChance;
            c.sicknessDurationTicks = SimBalance.SicknessDurationTicks;
            c.sicknessDamagePerBout = SimBalance.SicknessDamagePerBout;
            c.sicknessDamageBudgetCap = SimBalance.SicknessDamageBudgetCap;
            c.sickTorsoPerSlowTick = SimBalance.SickTorsoPerSlowTick;
            c.sickTorsoFloor = SimBalance.SickTorsoFloor;
            c.sickComfortPerSlowTick = SimBalance.SickComfortPerSlowTick;

            c.drinkBottleDurationTicks = SimBalance.DrinkBottleDurationTicks;
            c.drinkThirstRaw = SimBalance.DrinkThirstRaw;
            c.drinkThirstBoiled = SimBalance.DrinkThirstBoiled;
            c.drinkComfortBoiled = SimBalance.DrinkComfortBoiled;
            c.bottleCapacity = SimBalance.BottleCapacity;
            c.coconutWaterCapacity = SimBalance.CoconutWaterCapacity;

            c.groundSleepEnergy = SimBalance.GroundSleepEnergy;
            c.groundSitEnergy = SimBalance.GroundSitEnergy;
            c.groundSitComfort = SimBalance.GroundSitComfort;
            c.groundSitComfortLedge = SimBalance.GroundSitComfortLedge;
            c.bedEnergy = SimBalance.BedEnergy;
            c.leafBedEnergy = SimBalance.LeafBedEnergy;
            c.chairComfort = SimBalance.ChairComfort;
            c.chairEnergy = SimBalance.ChairEnergy;

            c.coconutHunger = SimBalance.CoconutHunger;
            c.coconutThirst = SimBalance.CoconutThirst;
            c.cookedMeatHunger = SimBalance.CookedMeatHunger;

            c.coldBandTemp = SimBalance.ColdBandTemp;
            c.hotBandTemp = SimBalance.HotBandTemp;
            c.coldPressureSlope = SimBalance.ColdPressureSlope;
            c.heatPressureSlope = SimBalance.HeatPressureSlope;
            c.thermalPressureCap = SimBalance.ThermalPressureCap;
            c.thermalComfyRecovery = SimBalance.ThermalComfyRecovery;
            c.thermalDamageGate = SimBalance.ThermalDamageGate;
            c.thermalHpHit = SimBalance.ThermalHpHit;
            c.indoorWarmthBonus = SimBalance.IndoorWarmthBonus;
            c.waterCoolBonus = SimBalance.WaterCoolBonus;
            c.fireWarmthRange1 = SimBalance.FireWarmthRange1;
            c.fireWarmthRange2 = SimBalance.FireWarmthRange2;

            c.tanRate = SimBalance.TanRate;
            c.sunburnRate = SimBalance.SunburnRate;
            c.sunExposureRate = SimBalance.SunExposureRate;
            c.sunburnBurnDamage = SimBalance.SunburnBurnDamage;

            c.hygieneWashGain = SimBalance.HygieneWashGain;
            c.hygieneDriftLoss = SimBalance.HygieneDriftLoss;

            c.staminaRestGain = SimBalance.StaminaRestGain;
            c.staminaWorkDrain = SimBalance.StaminaWorkDrain;
            c.staminaIdleGain = SimBalance.StaminaIdleGain;
            c.stressUpRate = SimBalance.StressUpRate;
            c.stressDownRate = SimBalance.StressDownRate;

            c.baseTemperature = SimBalance.BaseTemperature;
            c.temperatureAmplitude = SimBalance.TemperatureAmplitude;
            c.rainTempDrop = SimBalance.RainTempDrop;

            c.woundHealPerSlowTick = SimBalance.HealPerSlowTick;
            c.maxWounds = SimBalance.MaxWounds;
            c.gashesPerHit = SimBalance.GashesPerHit;
            c.minSplittableDamage = SimBalance.MinSplittableDamage;

            c.dogBiteDamage = SimBalance.BiteDamagePerPass;
            c.dogStrikeDamage = SimBalance.NpcStrikePerPass;
            c.dogRaidChancePerDay = SimBalance.RaidChancePerDay;
            c.dogRaidPackSize = SimBalance.RaidPackSize;
            c.dogAggroRadiusTiles = SimBalance.AggroRadiusTiles;
            c.dogRoamChance = SimBalance.RoamChance;
            c.sharkBiteDamage = SimBalance.SharkBiteDamage;
        }

        // The game's startup path: load the asset from Resources and apply it,
        // so tuned values ship without touching code constants.
        public static void LoadAndApply()
        {
            var config = Resources.Load<HexTuningConfig>(ResourcePath);
            if (config != null)
            {
                Apply(config);
            }
        }
    }
}
