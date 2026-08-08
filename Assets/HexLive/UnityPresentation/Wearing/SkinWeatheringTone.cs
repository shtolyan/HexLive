using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{

internal static class SkinWeatheringTone
{
    internal static Color Compose(float tanLevel, float sunburn, float tanStrength)
    {
        var tan = Mathf.Clamp01(tanLevel);
        var redPhase = Mathf.Clamp01(tan / 0.5f);
        var brownPhase = Mathf.Clamp01((tan - 0.5f) / 0.5f);
        var tint = Color.Lerp(Color.white, new Color(0.90f, 0.75f, 0.66f), redPhase);
        tint = Color.Lerp(tint, new Color(0.66f, 0.50f, 0.38f), brownPhase);
        tint = Color.Lerp(Color.white, tint, Mathf.Clamp01(tanStrength));

        // Acute burn is a temporary flush OF the already tanned skin. Preserve
        // its brown undertone while lifting red and suppressing green/blue;
        // replacing it with a fixed pink target washed the tan away.
        var burn = Mathf.Clamp01(sunburn) * 0.75f;
        return new Color(
            Mathf.Lerp(tint.r, Mathf.Min(1f, tint.r * 1.18f), burn),
            tint.g * Mathf.Lerp(1f, 0.62f, burn),
            tint.b * Mathf.Lerp(1f, 0.68f, burn),
            tint.a);
    }
}

}
