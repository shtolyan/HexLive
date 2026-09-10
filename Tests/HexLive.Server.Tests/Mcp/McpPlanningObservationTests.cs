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
