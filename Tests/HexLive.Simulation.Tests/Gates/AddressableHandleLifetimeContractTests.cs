using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class AddressableHandleLifetimeContractTests
    {
        [Test]
        public void HairCachesResetAndValidateHandlesBeforeReadingTheirResults()
        {
            var path = Path.Combine(RepoPaths.Root, "Assets", "HexLive",
                "UnityPresentation", "Wearing", "HairContent.cs");
            var source = File.ReadAllText(path);

            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("RuntimeInitializeLoadType.SubsystemRegistration"));
                Assert.That(source, Does.Contain("Hair.Clear();"));
                Assert.That(source, Does.Contain("Materials.Clear();"));
                Assert.That(source, Does.Contain("cached.IsValid()"));
                Assert.That(source, Does.Contain("if (!handle.IsValid())"));
            });
        }
    }
}
