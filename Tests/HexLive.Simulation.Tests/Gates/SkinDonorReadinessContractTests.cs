using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class SkinDonorReadinessContractTests
{
    private static string Read(string file) => File.ReadAllText(Path.Combine(RepoPaths.Root,
        "Assets/HexLive/UnityPresentation", file));

    [Test]
    public void WorkingSetIncludesSkinDonorsForSnapshotsAndLocalWorlds()
    {
        var source = Read("Wearing/ScenePrewarm.cs");
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("WarmOwnerMain(\"actor\", npc.SkinSet)"));
            Assert.That(source, Does.Contain("Add(\"actor\", npc.SkinSet)"));
            Assert.That(source, Does.Contain("Add(\"actor\", corpse.SkinSet)"));
            Assert.That(source, Does.Contain("actors.Add(npc.SkinSet)"));
            Assert.That(source, Does.Contain("actors.Add(corpse.SkinSet)"));
        });
    }

    [Test]
    public void EveryNpcViewWaitsForDonorBeforeConstructAndPaint()
    {
        var source = Read("Rendering/HexWorldRenderer.cs");
        var create = source.Substring(source.IndexOf("private GameObject CreateNpcView(NpcSnapshot npc)"));
        Assert.That(create.IndexOf("Request(\"actor\", npc.SkinSet"),
            Is.GreaterThanOrEqualTo(0).And.LessThan(create.IndexOf("view.Construct(")));
        var actor = Read("Wearing/NpcActorView.cs");
        var load = actor.Substring(actor.IndexOf("private static Dictionary<string, Material> LoadSkinSet"));
        Assert.That(load.IndexOf("return map; // Loading"),
            Is.GreaterThanOrEqualTo(0).And.LessThan(load.IndexOf("_skinSets[actor] = map")));
    }

    [Test]
    public void ResidencyPinsMaterialsForLiveAndDeadCompositeBodies()
    {
        var source = Read("Content/ContentResidency.cs");
        Assert.That(source, Does.Contain("AddKit(\"actor/\" + npc.SkinSet)"));
        Assert.That(source, Does.Contain("AddKit(\"actor/\" + body.SkinSet)"));
    }
}
