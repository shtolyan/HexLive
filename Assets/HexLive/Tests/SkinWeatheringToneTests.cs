#if UNITY_EDITOR
using System.Reflection;
using HexLive.UnityPresentation.Wearing;
using NUnit.Framework;
using UnityEngine;

namespace HexLive.Tests
{
public sealed class SkinWeatheringToneTests
{
    private static Color Tone(float tan, float burn, float strength) => (Color)typeof(NpcActorView).Assembly
        .GetType("HexLive.UnityPresentation.Wearing.SkinWeatheringTone")
        .GetMethod("Compose", BindingFlags.Static | BindingFlags.NonPublic)
        .Invoke(null, new object[] { tan, burn, strength });

    [Test]
    public void CleanSkinAndDisabledTanKeepAuthoredComplexion()
    {
        Assert.That(Tone(0f, 0f, .8f), Is.EqualTo(Color.white));
        Assert.That(Tone(1f, 0f, 0f), Is.EqualTo(Color.white));
    }

    [Test]
    public void FullTanWarmsSkinWithoutHalvingItsBlueChannel()
    {
        var full = Tone(1f, 0f, .8f);
        Assert.That(full.r, Is.GreaterThan(full.g));
        Assert.That(full.g, Is.GreaterThan(full.b));
        Assert.That(full.b, Is.GreaterThan(.65f));
        var half = Tone(.5f, 0f, .8f);
        Assert.That(half.r, Is.GreaterThan(full.r));
        Assert.That(half.g, Is.GreaterThan(full.g));
        Assert.That(half.b, Is.GreaterThan(full.b));
    }

    [Test]
    public void SunburnStillAddsRedOverTan()
    {
        var tan = Tone(.7f, 0f, .8f);
        var burn = Tone(.7f, 1f, .8f);
        Assert.That(burn.r, Is.GreaterThan(tan.r));
        Assert.That(burn.g, Is.LessThan(tan.g));
        Assert.That(burn.b, Is.LessThan(tan.b));
    }
}
}
#endif
