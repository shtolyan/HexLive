using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

// Spec 20.16 / §40.18-B: THE single source of truth for the SWIMMER's swell —
// the height a body in deep water snaps to (Height).
//
//   y(x,z,t) = sin( t*Speed + (x*dirX + z*dirZ) * Frequency ) * Amplitude
//   dirX = cos(DirectionDegrees/57), dirZ = sin(DirectionDegrees/57)
//
// PushToShader feeds these params into the live water material when it
// exposes the matching _Waves* uniforms. That was authored for the retired
// "Definitive Stylized Water URP" material; the canonical hand-written
// `HexLive/StylizedWater` shader (Spec 20.16 r2) carries its own gentle
// _WaveAmp=0.06 vertex wave with a DIFFERENT formula, so for it the push is
// a deliberate no-op and the swimmer's bob is an approximation of the visual
// surface (±0.1 wu против ±0.06 wu — расхождение живёт в игре с атомарной
// миграции и глазом не читается).
public static class WaterWave
{
    // Crest height in WORLD UNITS — how far the surface rises above still level
    // at a wave peak. THE amplitude knob; SwimTest exposes it live. Both the
    // mesh and the swimmer scale by exactly this.
    public static float Amplitude = 0.1f;

    // Spatial frequency (radians per world unit). Wavelength = 2π / Frequency.
    public static float Frequency = 1.4f;

    // Time scroll speed of the swell.
    public static float Speed = 2f;

    // Travel heading, degrees (kept on the shader's degrees/57 convention so
    // both sides rotate the same way).
    public static float DirectionDegrees = 360f;

    // Vertical displacement of the surface at world (x,z), in world units.
    public static float Height(float worldX, float worldZ, float time)
    {
        var dir = DirectionDegrees / 57f;
        var dirX = Mathf.Cos(dir);
        var dirZ = Mathf.Sin(dir);
        var phase = time * Speed + (worldX * dirX + worldZ * dirZ) * Frequency;
        return Mathf.Sin(phase) * Amplitude;
    }

    // Convenience for "now". _TimeParameters.x (the shader clock) is
    // Time.timeSinceLevelLoad, so both read the same beat.
    public static float HeightNow(float worldX, float worldZ)
    {
        return Height(worldX, worldZ, Time.timeSinceLevelLoad);
    }

    // Feed our owned params into the live water material so the MESH waves with
    // the exact same formula. The shader's height is sin(...) * _WavesIntensity
    // * 0.4, so _WavesIntensity carries Amplitude/0.4; _WavesAmplitude is the
    // shader's spatial-frequency term, _WavesSpeed the time term, _WavesDirection
    // the heading. (These uniforms also feed foam — a taller swell foams more.)
    public static void PushToShader()
    {
        var mat = HexWorldRenderer.ActiveWaterMaterial;
        if (mat == null)
        {
            return;
        }

        if (mat.HasProperty(IntensityId))
        {
            mat.SetFloat(IntensityId, Amplitude / 0.4f);
        }
        if (mat.HasProperty(FreqId))
        {
            mat.SetFloat(FreqId, Frequency);
        }
        if (mat.HasProperty(SpeedId))
        {
            mat.SetFloat(SpeedId, Speed);
        }
        if (mat.HasProperty(DirId))
        {
            mat.SetFloat(DirId, DirectionDegrees);
        }
    }

    private static readonly int IntensityId = Shader.PropertyToID("_WavesIntensity");
    private static readonly int FreqId = Shader.PropertyToID("_WavesAmplitude");
    private static readonly int SpeedId = Shader.PropertyToID("_WavesSpeed");
    private static readonly int DirId = Shader.PropertyToID("_WavesDirection");
}

}
