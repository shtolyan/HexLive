using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class LocalWorldDeleteUiContractTests
{
    [Test]
    public void LocalWorldDeletionIsValidatedConfirmedAndRefreshesTheCatalog()
    {
        var saveGame = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Bootstrap", "SaveGame.cs"));
        var loading = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "UI", "LoadingScreen.cs"));
        var styles = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "HexLive", "UI",
            "WorldLibraryPanel.uss"));
        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(saveGame, Does.Contain("public static bool DeleteWorld(string worldId)"));
            Assert.That(saveGame, Does.Contain("if (!IsSafeWorldId(worldId))"),
                "A UI-provided id must not become an unchecked filesystem path.");
            Assert.That(saveGame, Does.Contain("Directory.Delete(directory, recursive: true)"));
            Assert.That(saveGame, Does.Contain("PlayerPrefs.DeleteKey(ActiveWorldPref)"));

            Assert.That(loading, Does.Contain("if (!confirmingDelete)"));
            Assert.That(loading, Does.Contain("menu.worlds.delete.confirm"));
            Assert.That(loading, Does.Contain("SaveGame.DeleteWorld(world.Id)"));
            Assert.That(loading, Does.Contain("_worlds = SaveGame.ListWorlds()"));
            Assert.That(loading, Does.Contain("card.RemoveFromHierarchy()"));
            Assert.That(loading, Does.Contain("empty.AddToClassList(\"is-visible\")"));

            Assert.That(styles, Does.Contain(".world-card-actions.is-confirming"));
            Assert.That(styles, Does.Contain(".world-card-delete-cancel"));
            Assert.That(localization, Does.Contain("Term: menu.worlds.delete"));
            Assert.That(localization, Does.Contain("Term: menu.worlds.delete.warning"));
        });
    }
}
