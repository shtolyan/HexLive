using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PreparedPeopleCatalogTests
{
    [Test]
    public void PreparedMetadataLoadsThroughSimDataWithOnlyPrimalItemsAndCorrectSex()
    {
        var original = GarmentLibrary.Active.ToArray();
        try
        {
            var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root,
                "Assets/HexLiveContent/People/catalog.json")));
            var rows = catalog["records"].AsArray().Where(r => r["type"].GetValue<string>() == "wear").ToArray();
            var items = new JsonArray();
            foreach (var row in rows)
            {
                var item = row["metadata"]["simulation"].DeepClone().AsObject();
                item["id"] = row["id"].GetValue<string>();
                items.Add(item);
            }
            Assert.That(SimDataFile.ApplyJson(new JsonObject { ["garments"] = items }.ToJsonString()), Is.True);
            Assert.That(GarmentLibrary.Spawnable.Select(g => g.Id),
                Is.EquivalentTo(rows.Select(r => r["id"].GetValue<string>())));
            Assert.That(GarmentLibrary.Spawnable.Count(g => g.Sex == GarmentSex.Female), Is.EqualTo(41));
            Assert.That(GarmentLibrary.Spawnable.Count(g => g.Sex == GarmentSex.Male), Is.EqualTo(37));
            foreach (var item in GarmentLibrary.Spawnable)
            {
                Assert.That(GarmentLibrary.FitsSex(item.Sex, item.Id), Is.True);
                var opposite = item.Sex == GarmentSex.Female ? GarmentSex.Male : GarmentSex.Female;
                Assert.That(GarmentLibrary.FitsSex(opposite, item.Id), Is.False, item.Id);
                Assert.That(GarmentLibrary.IsSpawnable(item.PrototypeId), Is.True, item.Id);
            }
            Assert.That(GarmentLibrary.IsSpawnable("underwear.bra_riot"), Is.False);
            Assert.That(GarmentLibrary.Spawnable.Where(g => g.Id.StartsWith("gear.backpack_primal_")).All(g => g.Capacity == 9), Is.True);
            foreach (var seed in new[] { 1, 169 })
            {
                var world = TestWorld.CreateWorld(seed);
                Assert.That(world.Entities.Npcs.Values.Any(n => n.Sex == GarmentSex.Male), Is.True);
                Assert.That(world.Entities.Npcs.Values.Any(n => n.Sex == GarmentSex.Female), Is.True);
                foreach (var npc in world.Entities.Npcs.Values)
                {
                    var worn = npc.WornItems.Select(item =>
                    {
                        Assert.That(GarmentLibrary.IsSpawnable(item.DefinitionId), Is.True,
                            $"seed={seed} npc={npc.Id}: unavailable starter {item.DefinitionId}");
                        Assert.That(GarmentLibrary.FitsSex(npc.Sex, item.DefinitionId), Is.True);
                        return GarmentLibrary.Spawnable.Single(g => g.Id == item.DefinitionId);
                    }).ToArray();
                    Assert.That(worn.Count(g => g.Layer == WearLayer.Underwear && g.Covers.Contains(BodyPart.Pelvis)), Is.EqualTo(1));
                    if (npc.Sex == GarmentSex.Female)
                        Assert.That(worn.Count(g => g.Layer == WearLayer.Underwear && g.Covers.Contains(BodyPart.Torso)), Is.EqualTo(1));
                    Assert.That(worn.Count(g => g.Layer == WearLayer.Bags && g.Capacity == 9), Is.EqualTo(1));
                    Assert.That(worn.Any(g => g.Armor >= .1f && g.Covers.Contains(BodyPart.ArmL)), Is.True);
                    Assert.That(worn.Any(g => g.Armor >= .1f && g.Covers.Contains(BodyPart.LegL)), Is.True);
                }
            }
        }
        finally { GarmentLibrary.Override(original); }
    }
}
