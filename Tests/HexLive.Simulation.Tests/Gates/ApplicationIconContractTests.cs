using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§152.2: the desktop application icon is bootstrap Player content.</summary>
public sealed class ApplicationIconContractTests
{
    private const string IconGuid = "9d8a6c4f2b1e47a59f3c0d6e8b1247ac";

    [Test]
    public void PlayerSettingsIconGuidResolvesInsideBootstrapResources()
    {
        var settings = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "ProjectSettings", "ProjectSettings.asset"));
        var iconPath = Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "HexLive", "UI", "app_icon.png");
        var metaPath = iconPath + ".meta";

        Assert.Multiple(() =>
        {
            Assert.That(settings, Does.Contain("guid: " + IconGuid),
                "PlayerSettings must keep the approved icon reference.");
            Assert.That(File.Exists(iconPath), Is.True,
                "The application icon must ship in the Player bootstrap, not an atomic UI bundle.");
            Assert.That(File.Exists(metaPath), Is.True);
            Assert.That(File.ReadAllText(metaPath), Does.Contain("guid: " + IconGuid),
                "The restored asset must retain the GUID already serialized by PlayerSettings.");
        });
    }
}
