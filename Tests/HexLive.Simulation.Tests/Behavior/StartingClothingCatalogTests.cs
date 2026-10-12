using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

// §171.10: starting clothes are the server's choice. A world on an older catalogue
// without a starter must still accept the character instead of a «clothing
// incompatible» refusal the player cannot act on.
public sealed class StartingClothingCatalogTests
{
    [Test]
    public void StarterMissingFromTheWorldCatalogueIsSkippedNotRefused()
    {
        var femaleBriefs = new GarmentParams("underwear.briefs_primal", "Hide Briefs", WearLayer.Underwear,
            0.03f, 0f, 0f, 8, 1, GarmentSex.Female, BodyPart.Pelvis);
        try
        {
            GarmentLibrary.Override(new[] { femaleBriefs });
            Assert.Multiple(() =>
            {
                Assert.That(PlayerCharacterCreation.StartingClothing("Kshishtof"), Is.Empty);
                Assert.That(PlayerCharacterCreation.StartingClothing("Marta"), Is.EqualTo(new[] { "underwear.briefs_primal" }));
            });
            var errors = new List<string>();
            WorldCreation.ValidateCharacter(new CharacterCreationConfig {
                    Name = "Новый", Body = "Kshishtof", Traits = new(),
                    Clothing = PlayerCharacterCreation.StartingClothing("Kshishtof") },
                0f, 0f, "character", (path, code) => errors.Add(path + ":" + code));
            Assert.That(errors.Where(e => e.StartsWith("character.clothing", StringComparison.Ordinal)), Is.Empty);
        }
        finally
        {
            SimDataFile.Require(RepoPaths.SimData);
        }
    }
}

}
