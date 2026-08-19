using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class DisplayStartupContractTests
    {
        [Test]
        public void DesktopPlayerAlwaysStartsFullHdFullscreen()
        {
            var settings = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "ProjectSettings", "ProjectSettings.asset"));
            var policy = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                "Bootstrap", "DisplayStartupPolicy.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(settings, Does.Contain("defaultScreenWidth: 1920"));
                Assert.That(settings, Does.Contain("defaultScreenHeight: 1080"));

                // 0 is FullScreenMode.ExclusiveFullScreen — the only mode that
                // honours an exact resolution. FullScreenWindow (1) adopts the
                // desktop surface instead, which on a Retina panel silently
                // renders several times the authored Full HD pixel count.
                Assert.That(settings, Does.Contain("fullscreenMode: 0"));
                Assert.That(settings, Does.Contain("defaultIsNativeResolution: 0"));

                Assert.That(policy, Does.Contain("RuntimeInitializeLoadType.BeforeSceneLoad"));
                Assert.That(policy, Does.Contain("-hexlive-native-resolution"));
                Assert.That(policy, Does.Contain("-hexlive-borderless"));
                Assert.That(policy, Does.Contain("display.systemWidth"));
                Assert.That(policy, Does.Contain("display.systemHeight"));
                Assert.That(policy,
                    Does.Contain("Screen.SetResolution(Width, Height, FullScreenMode.ExclusiveFullScreen)"));

                // The policy must report what it GOT, not only what it asked
                // for: request-only logging is what let a declined request read
                // exactly like an honoured one.
                Assert.That(policy, Does.Contain("[Display] APPLIED"));
                Assert.That(policy, Does.Contain("Screen.width"));
                Assert.That(policy, Does.Contain("[Display] NOT Full HD"));
            });
        }
    }
}
