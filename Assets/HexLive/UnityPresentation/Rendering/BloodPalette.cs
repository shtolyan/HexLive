using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>§44/§50: one scarlet pigment for splashes, drips and fresh pools.</summary>
    public static class BloodPalette
    {
        public static readonly Color Fresh = new(0.72f, 0.02f, 0.04f, 1f);
        public static readonly Color Dried = new(0.15f, 0.02f, 0.02f, 1f);

        public static void TintParticles(GameObject root)
        {
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = Fresh;
                // Authored particle gradients may fade alpha, but not recolour blood.
                var lifetime = ps.colorOverLifetime;
                if (lifetime.enabled)
                {
                    lifetime.color = WhiteWithSameAlpha(lifetime.color);
                }
                var speed = ps.colorBySpeed;
                if (speed.enabled) speed.color = WhiteWithSameAlpha(speed.color);
            }
        }

        private static Color White(float alpha) => new(1, 1, 1, alpha);
        private static Gradient White(Gradient source)
        {
            var result = new Gradient { mode = source.mode };
            result.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, source.alphaKeys);
            return result;
        }
        private static ParticleSystem.MinMaxGradient WhiteWithSameAlpha(ParticleSystem.MinMaxGradient value)
        {
            switch (value.mode)
            {
                case ParticleSystemGradientMode.Color: return new(White(value.color.a));
                case ParticleSystemGradientMode.TwoColors: return new(White(value.colorMin.a), White(value.colorMax.a));
                case ParticleSystemGradientMode.TwoGradients: return new(White(value.gradientMin), White(value.gradientMax));
                default:
                    var result = new ParticleSystem.MinMaxGradient(White(value.gradient));
                    result.mode = value.mode;
                    return result;
            }
        }
    }
}
