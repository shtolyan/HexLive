using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class AtomicHandleLifetimeContractTests
    {
        [Test]
        public void HairCachesDisposeAndValidateAtomicHandlesBeforeReadingTheirAssets()
        {
            var path = Path.Combine(RepoPaths.Root, "Assets", "HexLive",
                "UnityPresentation", "Wearing", "HairContent.cs");
            var source = File.ReadAllText(path);

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("RuntimeInitializeLoadType.SubsystemRegistration"));
                Assert.That(source, Does.Contain("Hair.Clear();"));
                Assert.That(source, Does.Contain("Materials.Clear();"));
                Assert.That(source, Does.Contain(
                    "foreach (var handle in Hair.Values) handle?.Dispose();"));
                Assert.That(source, Does.Contain(
                    "foreach (var handle in Materials.Values) handle?.Dispose();"));
                Assert.That(source, Does.Contain("cached.Asset != null"));
                Assert.That(source, Does.Contain("loaded?.Asset != null"));
            });
        }
    }
}
