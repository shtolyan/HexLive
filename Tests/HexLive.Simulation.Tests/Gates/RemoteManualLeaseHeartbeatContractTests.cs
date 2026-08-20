using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

public sealed class RemoteManualLeaseHeartbeatContractTests
{
    [Test]
    public void SelectedRemoteManualActorsRenewTheirLeaseBeforeClicks()
    {
        var source = File.ReadAllText(FindRepoFile(
            "Assets", "HexLive", "UnityPresentation", "Input", "SimulationInputAdapter.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("RemoteLeaseHeartbeatSeconds = 2f"));
            Assert.That(source, Does.Contain("RefreshControlSelection();\n        RenewRemoteLeaseForSelection();"));
            Assert.That(source, Does.Contain("_runner.Link.IsRemote"));
            Assert.That(source, Does.Contain("new SetManualControlCommand("));
            Assert.That(source, Does.Contain("new SetGroupManualControlCommand("));
            Assert.That(source, Does.Contain("SelectedActors(), true"));
        });
    }

    private static string FindRepoFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, Path.Combine(relativePath));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(Path.Combine(relativePath));
    }
}

}
