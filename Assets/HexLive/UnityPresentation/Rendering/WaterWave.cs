using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{

// Spec 20.16 / §40.18-B: THE single source of truth for the water swell —
// OWNED here, not buried in the third-party material. These fields drive both
// the surface MESH (pushed into the shader every frame by PushToShader) AND
// the height a swimmer snaps to (Height), so there is exactly one place to
// tune the wave and the two can never drift apart.
//
//   y(x,z,t) = sin( t*Speed + (x*dirX + z*dirZ) * Frequency ) * Amplitude
//   dirX = cos(DirectionDegrees/57), dirZ = sin(DirectionDegrees/57)
//
// The shader's vertex offset computes the IDENTICAL expression from the pushed
// material uniforms (see the "single-source world-space swell" block in
// "Definitive Stylized Water URP.shader").
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
