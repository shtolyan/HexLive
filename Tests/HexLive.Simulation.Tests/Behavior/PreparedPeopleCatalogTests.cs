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
        }
        finally { GarmentLibrary.Override(original); }
    }
}
