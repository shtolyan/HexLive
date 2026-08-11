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
                Assert.That(settings, Does.Contain("fullscreenMode: 1"));
                Assert.That(policy, Does.Contain("RuntimeInitializeLoadType.BeforeSceneLoad"));
                Assert.That(policy,
                    Does.Contain("Screen.SetResolution(Width, Height, FullScreenMode.FullScreenWindow)"));
            });
        }
    }
}
