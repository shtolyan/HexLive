using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Journal;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

/// <summary>§171.26: the body's own diary reaches the voice as ready first-person phrases.</summary>
public sealed class McpJournalObservationTests
{
    [Test]
    public void DiaryEntriesUseTheSameKeyLadderAsThePlayersJournal()
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world);
            actor.Journal.Clear();
            var now = world.Tick;
            // Written term: journal.FoodShared.got.v0 with Dasha substituted.
            actor.Journal.Add(new JournalEntry { Tick = now - 30 * Spec136.HourTicks, Type = "FoodShared",
                Perspective = JournalPerspective.Received, SubjectNameId = "dasha", Register = JournalRegister.Plain });
            // No journal.* term: the colony feed's words with «Я» as the doer.
            actor.Journal.Add(new JournalEntry { Tick = now - 5 * Spec136.HourTicks, Type = "CoconutEaten" });
            // Quiet run of hours with a chore.
            actor.Journal.Add(new JournalEntry { Tick = now - 2 * Spec136.HourTicks, QuietHours = 3, Chore0 = "TreeChopped" });
            actor.Journal.Add(new JournalEntry { Tick = now, Type = "CompanionNarrative", Extra = "Записала сама." });
            return actor.Id.Value;
        });
        using var response = Describe(host, actorId);
        var root = response.RootElement;
        var rows = root.GetProperty("journal").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("dayLengthTicks").GetInt32(), Is.EqualTo(WorldBalance.DayLengthTicks));
            Assert.That(rows, Has.Length.EqualTo(4));
            Assert.That(rows[0].GetProperty("text").GetString(), Is.EqualTo("Даша дала мне поесть. Я умирала с голоду."));
            Assert.That(rows[0].GetProperty("hoursAgo").GetInt32(), Is.EqualTo(30));
            Assert.That(rows[0].GetProperty("type").GetString(), Is.EqualTo("FoodShared"));
            Assert.That(rows[1].GetProperty("text").GetString(), Is.EqualTo("Я съела кокос."));
            Assert.That(rows[2].GetProperty("quietHours").GetInt32(), Is.EqualTo(3));
            Assert.That(rows[2].GetProperty("text").GetString(), Does.Contain("3").And.Contain("валила дерево"));
            Assert.That(rows[3].GetProperty("text").GetString(), Is.EqualTo("Записала сама."));
            Assert.That(rows[3].GetProperty("hoursAgo").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public void OnlyTheNewestEntriesAreSentOldestFirst()
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world);
            actor.Journal.Clear();
            for (var i = 0; i < 30; i++)
                actor.Journal.Add(new JournalEntry { Tick = i * Spec136.HourTicks, Type = "CoconutEaten" });
            return actor.Id.Value;
        });
        using var response = Describe(host, actorId);
        var ticks = response.RootElement.GetProperty("journal").EnumerateArray().Select(r => r.GetProperty("tick").GetInt32()).ToArray();
        Assert.That(ticks, Has.Length.EqualTo(McpJournalObservations.MaxEntries));
        Assert.That(ticks, Is.Ordered.Ascending);
        Assert.That(ticks.Last(), Is.EqualTo(29 * Spec136.HourTicks));
    }

    private static NPCState Observer(WorldState world) => world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

    private static JsonDocument Describe(WorldHost host, int npcId)
    {
        var tools = new McpTools(host, new ControlLeases(45));
        var result = tools.Call("describe_colonist", JsonSerializer.SerializeToElement(new { npcId }), "mcp:journal-observation-test", out var error);
        Assert.That(error, Is.False, result);
        return JsonDocument.Parse(result);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Source root");
    }

    private static WorldHost CreateHost() => new(12345, GameMode.Feud,
        Path.Combine(Path.GetTempPath(), "mcp-journal-" + Guid.NewGuid().ToString("N") + ".sav"),
        Path.Combine(Root(), "SimData/simdata.json"), false);
}
