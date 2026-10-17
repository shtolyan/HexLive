using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

/// <summary>§171.25: camps are common knowledge, people are personal experience.</summary>
public sealed class McpSocialObservationTests
{
    [Test]
    public void CampsGiveDirectionDistanceAndStanceButNeverResidents()
    {
        using var host = CreateHost();
        var (actorId, far) = host.Read(world =>
        {
            var actor = Observer(world);
            world.FactionHomes.Clear();
            world.FactionHomes[Faction.Colony] = actor.Tile;
            // Pure +Y in world space: Q + R/2 == 0. Hex distance is exactly 30.
            var north = new TileCoord(actor.Tile.Q - 15, actor.Tile.R + 30);
            world.FactionHomes[Faction.Colony3] = north;
            world.FactionHomes[Faction.Outsiders] = new TileCoord(actor.Tile.Q + 40, actor.Tile.R);
            actor.Memory.SurveyedTiles.Remove(north);
            return (actor.Id.Value, north);
        });
        using (var response = Describe(host, actorId))
        {
            var camps = response.RootElement.GetProperty("camps").EnumerateArray().ToArray();
            Assert.That(camps.Select(c => c.GetProperty("camp").GetString()), Is.EqualTo(new[] { "Colony", "Colony3" }));
            var own = camps[0]; var other = camps[1];
            Assert.Multiple(() =>
            {
                Assert.That(own.GetProperty("number").GetInt32(), Is.EqualTo(1));
                Assert.That(own.GetProperty("own").GetBoolean(), Is.True);
                Assert.That(own.GetProperty("here").GetBoolean(), Is.True);
                Assert.That(own.GetProperty("direction").GetString(), Is.Empty);
                Assert.That(own.GetProperty("stance").GetString(), Is.EqualTo("own"));
                Assert.That(other.GetProperty("number").GetInt32(), Is.EqualTo(3));
                Assert.That(other.GetProperty("direction").GetString(), Is.EqualTo("N"));
                Assert.That(other.GetProperty("distanceTiles").GetInt32(), Is.EqualTo(30));
                Assert.That(other.GetProperty("stance").GetString(), Is.EqualTo("hostile"), "Feud: camps are at war");
                Assert.That(other.GetProperty("visited").GetBoolean(), Is.False);
                Assert.That(other.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
                    { "camp", "number", "own", "here", "direction", "distanceTiles", "stance", "visited" }),
                    "No residents, counts or stock of another camp");
            });
        }
        host.Read(world =>
        {
            Observer(world).Memory.SurveyedTiles[far] = world.Tick;
            world.Mode = GameMode.HugeIsland;
            return true;
        });
        using var later = Describe(host, actorId);
        var neighbour = later.RootElement.GetProperty("camps")[1];
        Assert.That(neighbour.GetProperty("visited").GetBoolean(), Is.True);
        Assert.That(neighbour.GetProperty("stance").GetString(), Is.EqualTo("neutral"), "Solo-camp worlds start neutral");
    }

    [TestCase(1, 0, "E")]
    [TestCase(0, 1, "NE")]
    [TestCase(-1, 2, "N")]
    [TestCase(-1, 1, "NW")]
    [TestCase(-1, 0, "W")]
    [TestCase(0, -1, "SW")]
    [TestCase(1, -2, "S")]
    [TestCase(1, -1, "SE")]
    public void EightSectorsFollowWorldAxes(int q, int r, string expected) =>
        Assert.That(McpSocialObservations.Direction(TileCoord.Zero, new TileCoord(q, r)), Is.EqualTo(expected));

    [Test]
    public void AStrangerFromAnotherCampIsKnownOnlyAfterAMeetingAndObservationCreatesNoBond()
    {
        using var host = CreateHost();
        var (actorId, strangerId, bonds) = host.Read(world =>
        {
            var actor = Observer(world);
            var stranger = new NPCState { Id = new EntityId(970001), DisplayName = "Dasha", Faction = Faction.Colony3 };
            world.Entities.Npcs[stranger.Id] = stranger;
            actor.Social.Relationships.Remove(stranger.Id);
            actor.Memory.KnownAgents.Remove(stranger.Id);
            return (actor.Id.Value, stranger.Id.Value, actor.Social.Relationships.Count);
        });
        using (var before = Describe(host, actorId))
            Assert.That(People(before).Any(p => p.GetProperty("npcId").GetInt32() == strangerId), Is.False);
        host.Read(world =>
        {
            Observer(world).Memory.KnownAgents[new EntityId(strangerId)] = new AgentMemory
                { Id = new EntityId(strangerId), Faction = Faction.Colony3, LastSeenTick = world.Tick - 3 * Spec136.HourTicks };
            return true;
        });
        using var after = Describe(host, actorId);
        var row = People(after).Single(p => p.GetProperty("npcId").GetInt32() == strangerId);
        Assert.Multiple(() =>
        {
            Assert.That(row.GetProperty("names").GetProperty("ru").GetString(), Is.EqualTo("Даша"));
            Assert.That(row.GetProperty("camp").GetString(), Is.EqualTo("Colony3"));
            Assert.That(row.GetProperty("sameCamp").GetBoolean(), Is.False);
            Assert.That(row.GetProperty("relationship").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(row.GetProperty("hoursSinceSeen").GetInt32(), Is.EqualTo(3));
            Assert.That(row.GetProperty("hoursSinceTalk").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
        host.Read(world =>
        {
            Assert.That(Observer(world).Social.Relationships.Count, Is.EqualTo(bonds), "Observation must not create acquaintance");
            return true;
        });
    }

    [Test]
    public void ADeadAcquaintanceStaysWithoutALifeStatus()
    {
        using var host = CreateHost();
        var (actorId, friendId) = host.Read(world =>
        {
            var actor = Observer(world);
            var friend = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            var bond = actor.Social.GetOrCreate(friend.Id);
            bond.Familiarity = 1f; bond.Affinity = .9f; bond.LastInteractionTick = Math.Max(1, world.Tick);
            friend.Health = 0; world.Entities.Npcs.Remove(friend.Id); world.Entities.Corpses[friend.Id] = friend;
            return (actor.Id.Value, friend.Id.Value);
        });
        using var response = Describe(host, actorId);
        var row = People(response).Single(p => p.GetProperty("npcId").GetInt32() == friendId);
        Assert.That(row.GetProperty("relationship").GetProperty("affinity").GetSingle(), Is.EqualTo(.9f));
        foreach (var leak in new[] { "alive", "lifeStatus", "health", "dead" })
            Assert.That(row.TryGetProperty(leak, out _), Is.False, leak);
    }

    [Test]
    public void KnownPeopleAreBoundedAndOrderedByFamiliarity()
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world);
            for (var i = 0; i < 20; i++)
            {
                var person = new NPCState { Id = new EntityId(960000 + i), DisplayName = "p" + i, Faction = Faction.Colony2 };
                world.Entities.Npcs[person.Id] = person;
                actor.Social.GetOrCreate(person.Id).Familiarity = .9f - i * .01f;
            }
            return actor.Id.Value;
        });
        using var response = Describe(host, actorId);
        var rows = People(response);
        Assert.That(rows, Has.Length.EqualTo(McpSocialObservations.MaxKnownPeople));
        Assert.That(rows[0].GetProperty("npcId").GetInt32(), Is.EqualTo(960000));
        Assert.That(response.RootElement.GetProperty("knownPeopleTotal").GetInt32(), Is.GreaterThanOrEqualTo(20));
    }

    private static JsonElement[] People(JsonDocument response) =>
        response.RootElement.GetProperty("knownPeople").EnumerateArray().ToArray();

    private static NPCState Observer(WorldState world) => world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

    private static JsonDocument Describe(WorldHost host, int npcId)
    {
        var tools = new McpTools(host, new ControlLeases(45));
        var result = tools.Call("describe_colonist", JsonSerializer.SerializeToElement(new { npcId }), "mcp:social-observation-test", out var error);
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
        Path.Combine(Path.GetTempPath(), "mcp-social-" + Guid.NewGuid().ToString("N") + ".sav"),
        Path.Combine(Root(), "SimData/simdata.json"), false);
}
