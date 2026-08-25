using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Bridges the saved <see cref="HexTuningConfig"/> asset and the movement/
    /// water-feel statics. The hop block mirrors into HexHopTuning via
    /// <see cref="SimConfigMirror"/> (name convention + [MirrorField]); the
    /// swim/wave/ledge block feeds presentation statics that live outside the
    /// balance classes, so those lines stay hand-written and their config
    /// fields are [MirrorIgnore]. The game BALANCE that used to run through
    /// here now loads via <see cref="BalanceTuning"/> (Resources/HexLive/Balance).
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

            // The hop block → HexHopTuning, and the two swim SIM knobs →
            // MovementSystem. Those two used to be applied by hand right here,
            // which kept them out of BalanceReflection and out of simdata.json —
            // so headless runs used code defaults. They are ordinary mapped
            // fields now; do not re-add a hand-written assignment.
            SimConfigMirror.Apply(c);

            SwimVisuals.SinkDepth = c.sinkDepth;
            SwimVisuals.WadeDepth = c.wadeDepth;
            SwimVisuals.ShoreLevelSteps = c.waterShoreLevel;
            SwimVisuals.SurfaceDropFrac = c.waterSurfaceDrop;
            NpcActorView.SwimBodyLift = c.swimBodyLift;
            NpcActorView.LedgeSeatLift = c.ledgeSeatLift;
            NpcActorView.LedgeSeatBack = c.ledgeSeatBack;
            NpcActorView.MaleSeatForward = c.maleSeatForward;
            NpcActorView.MaleSeatDown = c.maleSeatDown;

            // §71.5: походка. Пер-клиповая калибровка живёт в NpcAnimSet.strides
            // (это данные о клипах), здесь — дефолты и сглаживание.
            NpcActorView.FullWalkBodyHeightsPerSec = c.walkBodyHeightsPerSec;
            NpcActorView.SlowRunCadence = c.slowRunCadence;
            NpcActorView.RunCadence = c.runCadence;
            NpcActorView.MinGaitCadence = c.minGaitCadence;
            NpcActorView.MaxGaitCadence = c.maxGaitCadence;
            NpcActorView.MaxWalkCadence = c.maxWalkCadence;
            NpcActorView.WalkStretchCadence = c.walkStretchCadence;
            NpcActorView.MaxWalkGait = c.maxWalkGait;
            NpcActorView.SpeedSmoothTau = c.speedSmoothTau;
            NpcActorView.WalkHoldSeconds = c.walkHoldSeconds;
            NpcActorView.MidJourneyWalkHoldSeconds = c.midJourneyWalkHoldSeconds;
            NpcActorView.PivotYawSpeed = c.pivotYawSpeed;

            WaterWave.Amplitude = c.waveAmplitude;
            WaterWave.Frequency = c.waveFrequency;
            WaterWave.Speed = c.waveSpeed;
        }

        // Read the current live statics INTO the asset (the "save current
        // tuning" direction). Mirror of Apply.
        public static void Capture(HexTuningConfig c)
        {
            if (c == null)
            {
                return;
            }

            SimConfigMirror.Capture(c); // HexHopTuning + the MovementSystem swim knobs

            c.sinkDepth = SwimVisuals.SinkDepth;
            c.wadeDepth = SwimVisuals.WadeDepth;
            c.waterShoreLevel = SwimVisuals.ShoreLevelSteps;
            c.waterSurfaceDrop = SwimVisuals.SurfaceDropFrac;
            c.swimBodyLift = NpcActorView.SwimBodyLift;
            c.ledgeSeatLift = NpcActorView.LedgeSeatLift;
            c.ledgeSeatBack = NpcActorView.LedgeSeatBack;
            c.maleSeatForward = NpcActorView.MaleSeatForward;
            c.maleSeatDown = NpcActorView.MaleSeatDown;

            c.walkBodyHeightsPerSec = NpcActorView.FullWalkBodyHeightsPerSec;
            c.slowRunCadence = NpcActorView.SlowRunCadence;
            c.runCadence = NpcActorView.RunCadence;
            c.minGaitCadence = NpcActorView.MinGaitCadence;
            c.maxGaitCadence = NpcActorView.MaxGaitCadence;
            c.maxWalkCadence = NpcActorView.MaxWalkCadence;
            c.walkStretchCadence = NpcActorView.WalkStretchCadence;
            c.maxWalkGait = NpcActorView.MaxWalkGait;
            c.speedSmoothTau = NpcActorView.SpeedSmoothTau;
            c.walkHoldSeconds = NpcActorView.WalkHoldSeconds;
            c.midJourneyWalkHoldSeconds = NpcActorView.MidJourneyWalkHoldSeconds;
            c.pivotYawSpeed = NpcActorView.PivotYawSpeed;

            c.waveAmplitude = WaterWave.Amplitude;
            c.waveFrequency = WaterWave.Frequency;
            c.waveSpeed = WaterWave.Speed;
        }

        // The game's startup path: load the asset from Resources and apply it,
        // so tuned values ship without touching code constants.
        public static void LoadAndApply()
        {
            HexTuningConfig config;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                config = UnityEditor.AssetDatabase.LoadAssetAtPath<HexTuningConfig>(
                    "Assets/HexLiveContent/RuntimeSource/HexTuningConfig.asset");
            }
            else
#endif
            {
                config = HexLive.UnityPresentation.Content.AtomicResources.Load<HexTuningConfig>(
                    ResourcePath);
            }
            if (config != null)
            {
                Apply(config);
            }
        }
    }
}
