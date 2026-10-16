using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class ConstructionUnitWorkTests
{
    private static WorldObjectState StockedWardrobe() => new()
    {
        DefinitionId = ContentIds.BuildSite, BuildProduct = ContentIds.Wardrobe,
        BillSticks = 4, BillBoards = 8, BillRope = 1,
        InstalledBuildMaterials = new int[BuildSiteMath.AllMaterials.Length]
    };

    private static void Stock(WorldObjectState site)
    {
        foreach (var material in BuildSiteMath.AllMaterials)
            for (var i = 0; i < BuildSiteMath.Bill(site, material); i++)
                site.Contents.Add(new ItemInstance(material));
    }

    [Test]
    public void AStockedBundleInstallsExactlyOneUnitPerWorkInStageOrder()
    {
        var world = TestWorld.CreateWorld(12084);
        var site = StockedWardrobe();
        Stock(site);
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
        Assert.That(BuildSiteMath.IsAssembled(site), Is.False);
        var order = Enumerable.Repeat(ContentIds.Stick, 4)
            .Concat(Enumerable.Repeat(ContentIds.Board, 8)).Append(ContentIds.Rope).ToArray();
        for (var step = 0; step < order.Length; step++)
        {
            var before = site.InstalledBuildMaterials.Sum();
            Assert.That(BuildSiteMath.HasReadyWork(world, site), Is.True);
            var previous = BuildSiteMath.Installed(site, order[step]);
            Assert.That(BuildSiteMath.TryInstallOne(world, site), Is.True);
            Assert.That(site.InstalledBuildMaterials.Sum(), Is.EqualTo(before + 1));
            Assert.That(BuildSiteMath.Installed(site, order[step]), Is.EqualTo(previous + 1));
            Assert.That(site.Contents, Has.Count.EqualTo(13), "Stock is not destroyed/recreated by installation.");
        }
        Assert.That(BuildSiteMath.IsAssembled(site), Is.True);
        Assert.That(BuildSiteMath.TryInstallOne(world, site), Is.False);
    }

    [Test]
    public void ActualBuildCompletionInstallsOnePieceNotTheWholeDeliveredBundle()
    {
        var world = TestWorld.CreateWorld(12084);
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Add(new ItemInstance("tool.hammer"));
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0);
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite,
            anchor.Fragment, anchor.Tiles[0], anchor.Id);
        site.BuildProduct = ContentIds.Wardrobe;
        site.BillSticks = 4; site.BillBoards = 8; site.BillRope = 1;
        Stock(site);
        var complete = typeof(ExecutionSystem).GetMethod("ApplyFurnitureSite", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(complete, Is.Not.Null);
        for (var i = 1; i <= 4; i++)
        {
            complete.Invoke(null, new object[] { world, npc, site });
            Assert.That(site.InstalledBuildMaterials.Sum(), Is.EqualTo(i));
            Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.True,
                "The site must not raise just because all stock was delivered.");
        }
    }

    [Test]
    public void FutureStageStockCannotInstallBeforeSupports()
    {
        var world = TestWorld.CreateWorld(12084);
        var site = StockedWardrobe();
        site.Contents.Add(new ItemInstance(ContentIds.Board));
        Assert.That(BuildSiteMath.HasReadyWork(world, site), Is.False);
        Assert.That(BuildSiteMath.TryInstallOne(world, site), Is.False);
        Assert.That(site.InstalledBuildMaterials.Sum(), Is.Zero);
    }

    [Test]
    public void InstalledPrefixAndUninstalledStockSurviveSaveLoadAndSnapshot()
    {
        const int seed = 12084;
        var world = TestWorld.CreateWorld(seed);
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0);
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite,
            anchor.Fragment, anchor.Tiles[0], anchor.Id);
        site.BuildProduct = ContentIds.Wardrobe;
        site.BillSticks = 4; site.BillBoards = 8; site.BillRope = 1;
        Stock(site);
        for (var i = 0; i < 5; i++) Assert.That(BuildSiteMath.TryInstallOne(world, site), Is.True);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(bytes, Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, reader);
        var loaded = restored.Entities.Objects[site.Id];
        Assert.That(loaded.InstalledBuildMaterials, Is.EqualTo(site.InstalledBuildMaterials));
        Assert.That(loaded.Contents, Has.Count.EqualTo(13));
        var snapshot = WorldSnapshotExporter.Export(restored).Objects.Single(o => o.Id.Value == site.Id.Value);
        Assert.That(snapshot.DeliveredSticks, Is.EqualTo(4));
        Assert.That(snapshot.DeliveredBoards, Is.EqualTo(1), "Wire visual progress is installed, not raw stock.");
        Assert.That(snapshot.DeliveredRope, Is.Zero);
        Assert.That(BuildSiteMath.TryInstallOne(restored, loaded), Is.True);
        Assert.That(loaded.InstalledBuildMaterials.Sum(), Is.EqualTo(6));
    }

    [Test]
    public void PreviousSaveVersionLoadsWithoutReadingNewConstructionChannels()
    {
        const int seed = 12084;
        var world = TestWorld.CreateWorld(seed);
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0);
        var site = WorldObjectMutations.SpawnObject(world, ContentIds.BuildSite,
            anchor.Fragment, anchor.Tiles[0], anchor.Id);
        site.BuildProduct = ContentIds.Wardrobe;
        site.BillSticks = 4; site.BillBoards = 8; site.BillRope = 1;
        site.LeafDryingStartTick = 123;
        site.Contents.Add(new ItemInstance(ContentIds.Stick));
        site.Contents.Add(new ItemInstance(ContentIds.Stick));
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, 84);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(bytes, Encoding.UTF8, true))
            WorldSaveSerializer.Read(restored, reader);
        var loaded = restored.Entities.Objects[site.Id];
        Assert.That(loaded.InstalledBuildMaterials, Is.Null);
        Assert.That(loaded.LeafDryingStartTick, Is.EqualTo(123));
        BuildSiteMath.TrackWork(loaded);
        Assert.That(loaded.InstalledBuildMaterials.Sum(), Is.EqualTo(2));
    }

    [Test]
    public void LegacyPartialSiteKeepsAlreadyVisiblePrefix()
    {
        var site = StockedWardrobe();
        site.InstalledBuildMaterials = null;
        site.Contents.Add(new ItemInstance(ContentIds.Stick));
        site.Contents.Add(new ItemInstance(ContentIds.Stick));
        BuildSiteMath.TrackWork(site);
        Assert.That(BuildSiteMath.Installed(site, ContentIds.Stick), Is.EqualTo(2));
        Assert.That(site.InstalledBuildMaterials.Sum(), Is.EqualTo(2));
    }
}
