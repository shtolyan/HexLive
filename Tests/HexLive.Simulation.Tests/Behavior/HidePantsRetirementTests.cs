using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class HidePantsRetirementTests
{
    [Test]
    public void OldSimDataCannotReviveTheDefinitionOrRecipe()
    {
        try
        {
            TestWorld.CreateWorld();
            Assert.That(SimDataFile.ApplyJson("{\"worldObjects\":[{\"id\":\"clothing.leather_pants\",\"displayName\":\"Hide Pants\"}],\"recipes\":[{\"output\":\"clothing.leather_pants\",\"inputs\":[{\"id\":\"resource.hide\",\"count\":1}]}]}"), Is.True);
            var defs = PrototypeContentCatalog.CreateDefaults().ToDictionary(p => p.Key, p => p.Value);
            WorldObjectLibrary.ApplyTo(defs);
            Assert.Multiple(() =>
            {
                Assert.That(defs.ContainsKey(ContentIds.LeatherPants), Is.False);
                Assert.That(RecipeCatalog.ByGoal.ContainsKey(GoalType.CraftLeather), Is.False);
                Assert.That(RecipeCatalog.ByOutput.ContainsKey(ContentIds.LeatherPants), Is.False);
            });
        }
        finally { SimDataFile.Require(RepoPaths.SimData); }
    }

    [Test]
    public void SaveReloadRemovesPantsEverywhereAndPreservesOtherItemsAndProjectMaterials()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Add(new ItemInstance(ContentIds.LeatherPants));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.LeatherPants));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stone) { Durability = .62f });
        npc.Mind.CurrentGoal = npc.Plan.Goal = GoalType.CraftLeather;
        var anchor = npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
        var pants = WorldObjectMutations.SpawnObject(world, ContentIds.LeatherPants, npc.Fragment, npc.Tile, anchor);
        pants.CraftWorkRequired = 24;
        pants.CraftWorkDone = 12;
        pants.Contents.Add(new ItemInstance(ContentIds.Hide) { Wetness = .37f });
        npc.Execution.CraftProjectId = pants.Id;
        var box = WorldObjectMutations.SpawnObject(world, ContentIds.MedkitBox, npc.Fragment, npc.Tile, anchor);
        box.Contents.Add(new ItemInstance(ContentIds.LeatherPants));
        box.Contents.Add(new ItemInstance(ContentIds.Bandage));
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld();
        using var reader = new BinaryReader(bytes);
        WorldSaveSerializer.Read(restored, reader);
        var savedNpc = restored.Entities.Npcs[npc.Id];
        Assert.Multiple(() =>
        {
            Assert.That(savedNpc.WornItems.Contains(ContentIds.LeatherPants), Is.False);
            Assert.That(savedNpc.Inventory.Items.Contains(ContentIds.LeatherPants), Is.False);
            Assert.That(savedNpc.Inventory.Items.Any(i => i.DefinitionId == ContentIds.Stone && Math.Abs(i.Durability - .62f) < .001), Is.True);
            Assert.That(savedNpc.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(savedNpc.Execution.CraftProjectId, Is.Null);
            Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.LeatherPants), Is.False);
            Assert.That(restored.Entities.Objects.Values.SelectMany(o => o.Contents).Any(i => i.DefinitionId == ContentIds.LeatherPants), Is.False);
            Assert.That(restored.Entities.Objects[box.Id].Contents.Select(i => i.DefinitionId), Is.EquivalentTo(new[] { ContentIds.Bandage }));
            Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Hide && Math.Abs(o.Wetness - .37f) < .001), Is.True);
        });
    }
}
