using HexLive.UnityPresentation.Rendering;
using NUnit.Framework;
using UnityEngine;
namespace HexLive.Tests
{
    public sealed class BloodPaletteTests
    {
        [Test] public void ContinuousDripsKeepRedAndPreserveTheirFade()
        {
            var go = new GameObject("blood-test");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main; main.startColor = Color.white;
                var gradient = new Gradient();
                gradient.SetKeys(new[] { new GradientColorKey(Color.green, 0), new GradientColorKey(Color.blue, 1) },
                    new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(.25f, 1) });
                var over = ps.colorOverLifetime; over.enabled = true; over.color = gradient;
                var speed = ps.colorBySpeed; speed.enabled = true; speed.color = new Color(0, 1, 0, .5f);
                BloodPalette.TintParticles(go);
                Assert.That(ps.main.startColor.color, Is.EqualTo(BloodPalette.Fresh));
                Assert.That(ps.colorOverLifetime.color.gradient.Evaluate(.5f).r, Is.EqualTo(1));
                Assert.That(ps.colorOverLifetime.color.gradient.Evaluate(1).a, Is.EqualTo(.25f).Within(.001f));
                Assert.That(ps.colorBySpeed.enabled, Is.True);
                Assert.That(ps.colorBySpeed.color.color, Is.EqualTo(new Color(1, 1, 1, .5f)));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
