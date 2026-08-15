using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
    public sealed class FogObserverContractTests
    {
        [Test]
        public void HiddenRelationTargetCannotBecomeFogObserver()
        {
            var renderer = File.ReadAllText(Path.Combine(
                RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
                "Rendering", "HexWorldRenderer.cs"));
            var spec = File.ReadAllText(Path.Combine(RepoPaths.Root, "Spec", "125.md"));

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain("private int _fogObserverNpcId = -1;"));
                Assert.That(renderer, Does.Contain("ResolveFogObserverId(snapshot, requestedId, _fogObserverNpcId)"));
                Assert.That(renderer, Does.Contain("npc.Faction != HexLive.Simulation.Agents.Faction.Colony || npc.Health <= 0f"));
                Assert.That(renderer, Does.Contain("previousIsValid |= npc.Id.Value == previousId;"));
                Assert.That(renderer, Does.Contain("firstColonyId == int.MaxValue ? -1 : firstColonyId"));
                Assert.That(renderer, Does.Contain("_fogHidesNpcs = selectedOnly;"));
                Assert.That(renderer, Does.Contain("var selectedId = _fogObserverNpcId;"),
                    "The perception ring must follow the validated observer, not the relation target.");
                Assert.That(renderer, Does.Not.Contain("_fogHidesNpcs = selectedId >= 0;"));
                Assert.That(spec, Does.Contain("Карточка отношений может выбрать скрытого чужака"));
            });
        }
    }
}
