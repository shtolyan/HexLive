using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class RetiredGarmentCatalogTests
{
    [Test]
    public void RetiredDefinitionRemainsReadableButCannotBeSpawned()
    {
        var live = new GarmentParams(
            "skirt.live", "Live Skirt", WearLayer.Wear,
            0.1f, 0f, 0f, 8, 1, GarmentSex.Female, BodyPart.Pelvis);
        var retired = new GarmentParams(
            "skirt.retired", "Retired Skirt", WearLayer.Wear,
            0.1f, 0f, 0f, 8, 1, GarmentSex.Female, BodyPart.Pelvis)
        {
            Retired = true,
        };

        try
        {
            GarmentLibrary.Override(new[] { live, retired });

            Assert.Multiple(() =>
            {
                Assert.That(GarmentLibrary.Active, Has.Count.EqualTo(2));
                Assert.That(GarmentLibrary.Spawnable, Has.Count.EqualTo(1));
                Assert.That(GarmentLibrary.IsSpawnable(live.Id), Is.True);
                Assert.That(GarmentLibrary.IsSpawnable(retired.Id), Is.False);
                Assert.That(GarmentLibrary.FitsSex(GarmentSex.Female, retired.Id), Is.True,
                    "an already-existing retired item still needs its definition");
            });
        }
        finally
        {
            SimDataFile.Require(RepoPaths.SimData);
        }
    }
}

}
