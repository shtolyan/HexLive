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
