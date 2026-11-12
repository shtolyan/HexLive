using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

public sealed class RemoteManualLeaseHeartbeatContractTests
{
    [Test]
    public void SelectedRemoteManualActorsRenewTheirLeaseBeforeClicks()
    {
        var source = RepoPaths.ReadText(FindRepoFile(
            "Assets", "HexLive", "UnityPresentation", "Input", "SimulationInputAdapter.cs"));

        Assert.Multiple(() =>
        {
            // §121.7 / #728: the heartbeat is the player's ATTENTION, not the mere
            // selection — it stops when the mouse and keyboard go quiet and no
            // window of that character is open, so a forgotten one returns to AI.
            Assert.That(source, Does.Contain("PresenceHeartbeatSeconds = 2f"));
            Assert.That(source, Does.Contain("PresenceAttentionSeconds = 4f"));
            Assert.That(source, Does.Contain("RefreshControlSelection();"));
            Assert.That(source, Does.Contain("UpdatePresenceHeartbeat();"));
            Assert.That(source, Does.Contain("LootTransferPanel.IsOpen || ContextMenuPanel.IsOpen"));
            Assert.That(source, Does.Contain("foreach (var id in _manualSelectedIds)"));
            Assert.That(source, Does.Contain("new SetManualControlCommand(new EntityId(id), true)"));
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
