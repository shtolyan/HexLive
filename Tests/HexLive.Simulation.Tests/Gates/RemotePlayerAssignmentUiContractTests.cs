using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§149.3: UI must ask about this NPC, not merely whether the socket
/// has a control token.</summary>
public sealed class RemotePlayerAssignmentUiContractTests
{
    [Test]
    public void PerNpcAuthorityFlowsFromHandshakeToEveryManualUiEntry()
    {
        var api = Read("Assets", "HexLive", "UnityPresentation", "Bootstrap", "ISimulationSource.cs");
        var remote = Read("Assets", "HexLive", "UnityPresentation", "Bootstrap", "Remote",
            "RemoteSocketBackend.cs");
        var input = Read("Assets", "HexLive", "UnityPresentation", "Input", "SimulationInputAdapter.cs");
        var panel = Read("Assets", "HexLive", "UnityPresentation", "UI", "CharacterPanel.cs");
        var loot = Read("Assets", "HexLive", "UnityPresentation", "UI", "LootTransferPanel.cs");
        var localization = Read("Assets", "Resources", "I2Languages.asset");
        var viewer = Read("Server", "HexLive.Server", "ViewerConnection.cs");

        Assert.Multiple(() =>
        {
            Assert.That(api, Does.Contain("bool CanControlNpc(EntityId npc)"));
            Assert.That(remote, Does.Contain("_handshake.AssignedNpcIds.Contains(npc.Value)"));
            Assert.That(input, Does.Contain("_runner.CanControlNpc(npc.Id)"));
            Assert.That(panel, Does.Contain("_runner.CanControlNpc(npc.Id)"));
            Assert.That(loot, Does.Contain("_runner.CanControlNpc(looter.Id)"));
            Assert.That(remote, Does.Contain("_craftingOptions.TryGetValue"));
            Assert.That(viewer, Does.Contain("PlayerCraftingOptions.Capture"));
            Assert.That(viewer, Does.Contain("SendCraftingOptionsIfChangedAsync"));
            Assert.That(localization, Does.Contain("Term: toast.order_rejected.NotAssigned"));
        });
    }

    private static string Read(params string[] path)
    {
        var fullPath = RepoPaths.Root;
        for (var i = 0; i < path.Length; i++)
        {
            fullPath = Path.Combine(fullPath, path[i]);
        }

        return File.ReadAllText(fullPath);
    }
}

}
