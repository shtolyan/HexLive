using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

[NonParallelizable]
public sealed class McpPlanningObservationTests
{
    private string _directory = "";
    [SetUp] public void Setup() => _directory = Directory.CreateTempSubdirectory("planning-observation-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void BodyReservesAreIndependentAndActorScoped()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        host.Read(w => { var n = w.Entities.Npcs[new EntityId(901)]; n.Needs.Energy = .2f; n.Needs.Stamina = .8f; n.Needs.Breath = .5f; return true; });
        var result = Call(tools, "describe_colonist", new { npcId = 901 });
        var needs = result.GetProperty("bodyNeeds");
        Assert.That(needs.GetProperty("energy").GetProperty("value").GetSingle(), Is.EqualTo(.2f));
        Assert.That(needs.GetProperty("stamina").GetProperty("value").GetSingle(), Is.EqualTo(.8f));
        Assert.That(needs.GetProperty("breath").GetProperty("value").GetSingle(), Is.EqualTo(.5f));
        tools.Call("describe_colonist", JsonSerializer.SerializeToElement(new { npcId = 901 }), "fixture", out var error, _ => false);
        Assert.That(error, Is.True);
    }

    [TestCase(0f, 0.049f)]
    [TestCase(1f, 1f)]
    public void HungerAndThirstKeepNumericValuesAndDeclareOppositeReserveDirection(float hunger, float thirst)
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        host.Read(w => { var n = w.Entities.Npcs[new EntityId(901)]; n.Needs.Hunger = hunger; n.Needs.Thirst = thirst; return true; });
        var needs = Call(tools, "describe_colonist", new { npcId = 901 }).GetProperty("bodyNeeds");
        Assert.Multiple(() =>
        {
            Assert.That(needs.GetProperty("hunger").GetSingle(), Is.EqualTo(hunger));
            Assert.That(needs.GetProperty("thirst").GetSingle(), Is.EqualTo(thirst));
            Assert.That(needs.GetProperty("energy").GetProperty("higherIsBetter").GetBoolean(), Is.True);
            foreach (var name in new[] { "hunger", "thirst" })
            {
                var scale = needs.GetProperty("scales").GetProperty(name);
                Assert.That(scale.GetProperty("higherIsBetter").GetBoolean(), Is.False);
                Assert.That(scale.GetProperty("zeroMeaning").GetString(), Is.Not.Empty);
                Assert.That(scale.GetProperty("oneMeaning").GetString(), Is.Not.EqualTo(scale.GetProperty("zeroMeaning").GetString()));
            }
        });
    }

    [Test]
    public void RestReadinessExplainsNativeSleepRefusalWithoutMutatingTheActor()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        host.Read(w =>
        {
            var n = w.Entities.Npcs[new EntityId(901)];
            n.Needs.Hunger = n.Needs.Thirst = 0f;
            n.Perception.Mobs.Clear(); n.Perception.Hostiles.Clear();
            n.Mind.AdrenalineUntilTick = w.Tick + 100;
            n.Mind.RestCooldownUntilTick = w.Tick + 20;
            return true;
        });
        var before = host.Read(w => (w.Tick, w.Events.HighestSeq, w.Entities.Npcs[new EntityId(901)].Position));
        var first = Call(tools, "describe_colonist", new { npcId = 901 }).GetProperty("restReadiness");
        Assert.That(first.GetProperty("sleepBodyReady").GetBoolean(), Is.False);
        Assert.That(first.GetProperty("sleepBodyBlockReason").GetString(), Is.EqualTo("SleepDanger"));
        Assert.That(first.GetProperty("adrenalineTicksRemaining").GetInt64(), Is.EqualTo(100));
        Assert.That(first.GetProperty("idleRestCooldownTicksRemaining").GetInt64(), Is.EqualTo(20));
        Assert.That(first.GetProperty("sleepSpaceChecked").GetBoolean(), Is.False);
        Assert.That(host.Read(w => (w.Tick, w.Events.HighestSeq, w.Entities.Npcs[new EntityId(901)].Position)), Is.EqualTo(before));
        host.Read(w => { var n = w.Entities.Npcs[new EntityId(901)]; n.Mind.AdrenalineUntilTick = w.Tick; n.Mind.RestCooldownUntilTick = w.Tick - 1; return true; });
        var ready = Call(tools, "describe_colonist", new { npcId = 901 }).GetProperty("restReadiness");
        Assert.That(ready.GetProperty("sleepBodyReady").GetBoolean(), Is.True);
        Assert.That(ready.GetProperty("adrenalineTicksRemaining").GetInt64(), Is.Zero);
        Assert.That(ready.GetProperty("idleRestCooldownTicksRemaining").GetInt64(), Is.Zero);
    }

    [Test]
    public void RecentGiftResultsIncludeOnlyTheAttachedActorsLastEightEvents()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        host.Read(w =>
        {
            w.Events.Clear();
            for (var i = 0; i < 10; i++) w.Events.Add(new HexLive.Simulation.Runtime.SimulationEvent
                { EntityId = 901, Type = "GiftGiven", Tick = w.Tick, Message = "own-" + i });
            w.Events.Add(new HexLive.Simulation.Runtime.SimulationEvent
                { EntityId = 902, Type = "GiftGiven", Tick = w.Tick, Message = "foreign" });
            w.Events.Add(new HexLive.Simulation.Runtime.SimulationEvent
                { EntityId = 901, Type = "RelationshipChanged", Tick = w.Tick, Message = "not-a-gift" });
            return true;
        });
        var result = Call(tools, "describe_colonist", new { npcId = 901 }).GetProperty("recentGiftResults");
        Assert.That(result.GetProperty("partialHistory").GetBoolean(), Is.True);
        var events = result.GetProperty("events");
        Assert.That(events.GetArrayLength(), Is.EqualTo(8));
        Assert.That(events[0].GetProperty("details").GetString(), Is.EqualTo("own-2"));
        Assert.That(events[7].GetProperty("details").GetString(), Is.EqualTo("own-9"));
        Assert.That(result.GetRawText(), Does.Not.Contain("foreign").And.Not.Contain("not-a-gift"));
    }

    [TestCase(0)]
    [TestCase(7)]
    public void DropPreviewRejectsUnboundedRadius(int radius)
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        tools.Call("read_inventory_drop", JsonSerializer.SerializeToElement(new
            { npcId = 901, index = 0, expectedDefinitionId = ContentIds.Coconut, approachRadiusTiles = radius }),
            "fixture", out var error);
        Assert.That(error, Is.True);
    }

    [Test]
    public void DropPreviewIsActorScopedAndRejectsStaleInventoryWithoutTakingControl()
    {
        using var host = Host(); var leases = new ControlLeases(45); var tools = new McpTools(host, leases);
        host.Read(w => { var n = w.Entities.Npcs[new EntityId(901)]; n.Inventory.Items.Clear();
            n.Inventory.Items.Add(new HexLive.Simulation.Agents.ItemInstance(ContentIds.Coconut)); return true; });
        var before = host.Read(w => (w.Tick, w.Entities.Objects.Count, w.Entities.Npcs[new EntityId(901)].Position));
        var result = Call(tools, "read_inventory_drop", new { npcId = 901, index = 0, expectedDefinitionId = ContentIds.Coconut });
        Assert.That(result.GetProperty("checkedOrigins").GetInt32(), Is.InRange(1, 19));
        Assert.That(result.GetProperty("routeChecked").GetBoolean(), Is.False);
        Assert.That(result.GetProperty("reserved").GetBoolean(), Is.False);
        Assert.That(leases.TryRenew(901, "fixture", out _), Is.False);
        var stale = Call(tools, "read_inventory_drop", new { npcId = 901, index = 0, expectedDefinitionId = ContentIds.Stick });
        Assert.That(stale.GetProperty("error").GetString(), Is.EqualTo("StaleInventoryItem"));
        tools.Call("read_inventory_drop", JsonSerializer.SerializeToElement(new
            { npcId = 901, index = 0, expectedDefinitionId = ContentIds.Coconut }), "fixture", out var denied, _ => false);
        Assert.That(denied, Is.True);
        Assert.That(host.Read(w => (w.Tick, w.Entities.Objects.Count, w.Entities.Npcs[new EntityId(901)].Position)), Is.EqualTo(before));
        Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Inventory.Items.Single().DefinitionId), Is.EqualTo(ContentIds.Coconut));
    }

    [Test]
    public void CampMembershipIsReadFromTheActorsActualFactionAndCurrentTile()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        var expected = host.Read(w =>
        {
            var npc = w.Entities.Npcs[new EntityId(901)];
            return HexLive.Simulation.Runtime.ColonyQueries.InCamp(w, npc.Tile, npc.Faction);
        });
        Assert.That(Call(tools, "describe_colonist", new { npcId = 901 }).GetProperty("inOwnCamp").GetBoolean(), Is.EqualTo(expected));
        host.Read(w =>
        {
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Tile = w.Tiles.Items.Keys.First(t => !HexLive.Simulation.Runtime.ColonyQueries.InCamp(w, t, npc.Faction));
            return true;
        });
        Assert.That(Call(tools, "describe_colonist", new { npcId = 901 }).GetProperty("inOwnCamp").GetBoolean(), Is.False);
    }

    [Test]
    public void RecipesReadTheLiveCatalogAndExposeAllIngredients()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        var expected = RecipeCatalog.ByGoal.Values.First(r => r.OutputDefinitionId == "resource.rope");
        var result = Call(tools, "read_recipes", new { definitionId = expected.OutputDefinitionId });
        var recipe = result.GetProperty("recipes")[0];
        Assert.That(recipe.GetProperty("inputs").EnumerateArray().Select(i => (i.GetProperty("definitionId").GetString(), i.GetProperty("count").GetInt32())),
            Is.EqualTo(expected.Inputs.Select(i => (i.Id, i.Count))));
        Assert.That(recipe.GetProperty("baseWorkTicks").GetInt32(), Is.EqualTo(expected.BaseWorkTicks));
        Assert.That(recipe.GetProperty("station").GetString(), Is.EqualTo(expected.Station));
        Assert.That(result.GetProperty("version").GetString(), Has.Length.EqualTo(64));
        Assert.That(Call(tools, "read_recipes", new { definitionId = "does.not.exist" }).GetProperty("recipes").GetArrayLength(), Is.Zero);
        Assert.That(Call(tools, "read_recipes", new { }).GetProperty("recipes").GetArrayLength(), Is.EqualTo(RecipeCatalog.ByGoal.Count));
    }

    [Test]
    public void BedCatalogUsesTheSameBillAsANewSite()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        var entry = Call(tools, "read_build_catalog", new { definitionId = "bed.basic" }).GetProperty("entries")[0];
        Assert.That(entry.GetProperty("requiresCompletedFloor").GetBoolean(), Is.True);
        var materials = entry.GetProperty("materials").EnumerateArray().ToDictionary(m => m.GetProperty("definitionId").GetString()!, m => m.GetProperty("required").GetInt32());
        Assert.That(materials["resource.log"], Is.EqualTo(HexLive.Simulation.Runtime.SimBalance.BedBasicBillLogs));
        Assert.That(materials["resource.rope"], Is.EqualTo(HexLive.Simulation.Runtime.SimBalance.BedBasicBillRope));
        Assert.That(materials["resource.palm_leaf"], Is.EqualTo(HexLive.Simulation.Runtime.SimBalance.BedBasicBillLeaves));
        Assert.That(materials["resource.stick"], Is.EqualTo(HexLive.Simulation.Runtime.SimBalance.BedBasicBillSticks));
    }

    private static JsonElement Call(McpTools tools, string name, object arguments)
    {
        var text = tools.Call(name, JsonSerializer.SerializeToElement(arguments), "fixture", out var error);
        Assert.That(error, Is.False, text);
        using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
    }
    private WorldHost Host() => new(12345, GameMode.Feud, Path.Combine(_directory, "world.sav"),
        Path.Combine(Root(), "SimData/simdata.json"), false, companionProfile: "masha");
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SimData/simdata.json"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
}
