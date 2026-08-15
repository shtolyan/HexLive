using System.IO;
using System.Linq;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class HumanCombatMercyRetirementContractTests
{
    [Test]
    public void LastStrikeMercyGraphStaysRetiredWhileBlobLayoutRemainsReadable()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "Assets")))
            root = Directory.GetParent(root)?.FullName;

        Assert.That(root, Is.Not.Null, "project root");
        var retiredFiles = new[]
        {
            "Assets/HexLive/Simulation/Runtime/Helpers/HumanStrikeDecision.cs",
            "Assets/HexLive/Simulation/Runtime/Balance/Spec86.cs"
        };
        foreach (var retiredFile in retiredFiles)
        {
            Assert.That(File.Exists(Path.Combine(root!, retiredFile)), Is.False,
                $"Legacy mercy file returned: {retiredFile}");
        }

        var activeFiles = new[]
        {
            "Assets/HexLive/Simulation/Agents/NpcState.cs",
            "Assets/HexLive/Simulation/AI/FightScene.cs",
            "Assets/HexLive/Simulation/Runtime/Helpers/MeleeSwing.cs",
            "Assets/HexLive/Simulation/Runtime/Systems/Wildlife/HumanCombatSystem.cs",
            "Assets/HexLive/Simulation/Content/BalanceReflection.cs",
            "Assets/HexLive/UnityPresentation/Config/OutsiderBalanceConfig.cs",
            "SimData/simdata.json"
        };
        var activeGraph = string.Join("\n", activeFiles.Select(path =>
            File.ReadAllText(Path.Combine(root!, path))));
        foreach (var retiredSymbol in new[]
                 {
                     "PendingHumanStrikeKillAuthorized",
                     "PendingHumanStrikeKillIntent",
                     "CapNonLethal",
                     "CalculateKillIntent",
                     "MercyHealthFloor",
                     "MercyPartFloor",
                     "MirrorTarget(typeof(Spec86))"
                 })
        {
            Assert.That(activeGraph, Does.Not.Contain(retiredSymbol), retiredSymbol);
        }

        var serializer = File.ReadAllText(Path.Combine(root!,
            "Assets/HexLive/Simulation/Persistence/WorldSaveSerializer.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(serializer, Does.Contain("w.Write(false); // retired v29 mercy flag"));
            Assert.That(serializer, Does.Contain("w.Write(0f); // retired v29 kill-intent scalar"));
            Assert.That(serializer, Does.Contain("_ = r.ReadBoolean(); // retired v29 mercy flag"));
            Assert.That(serializer, Does.Contain("_ = r.ReadSingle(); // retired v29 kill-intent scalar"));
        });
    }
}
