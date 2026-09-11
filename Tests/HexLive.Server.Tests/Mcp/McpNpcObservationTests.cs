using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpNpcObservationTests
{
    [Test]
    public void CarriedDeathRemainsExplicitAfterRemovalFromTheLivingRoster()
    {
        using var host = CreateHost();
        var setup = host.Read(world =>
        {
            var actor = Observer(world); var patient = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            ClearPerception(actor); actor.Perception.Objects.Clear();
            actor.CarriedNpcId = patient.Id; patient.CarriedByNpcId = actor.Id;
            patient.Health = 0; world.Entities.Npcs.Remove(patient.Id); world.Entities.Corpses[patient.Id] = patient;
            return (actor.Id.Value, patient.Id.Value);
        });
        using var dead = Describe(host, setup.Item1);
        var carried = dead.RootElement.GetProperty("carriedPerson");
        Assert.That(carried.GetProperty("npcId").GetInt32(), Is.EqualTo(setup.Item2));
        Assert.That(carried.GetProperty("lifeStatus").GetString(), Is.EqualTo("dead"));
        host.Read(w => { w.Entities.Corpses.Remove(new EntityId(setup.Item2)); return true; });
        using var missing = Describe(host, setup.Item1);
        Assert.That(missing.RootElement.GetProperty("carriedPerson").GetProperty("lifeStatus").GetString(), Is.EqualTo("unknown"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OnlyACurrentlySeenCorpseRevealsTheDeadPersonsIdentity(bool remembered)
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world); var patient = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            ClearPerception(actor); actor.Perception.Objects.Clear();
            patient.Health = 0; patient.DisplayName = "corpse-name-sentinel";
            world.Entities.Npcs.Remove(patient.Id); world.Entities.Corpses[patient.Id] = patient;
            var anchor = world.Entities.Objects.Values.First(); anchor.DefinitionId = ContentIds.CorpseNpc; anchor.CurrentUser = patient.Id;
            actor.Perception.Objects.Add(new PerceivedObject { Id = anchor.Id, FromMemory = remembered });
            return actor.Id.Value;
        });
        using var response = Describe(host, actorId);
        var corpses = response.RootElement.GetProperty("visibleCorpses");
        Assert.That(corpses.GetArrayLength(), Is.EqualTo(remembered ? 0 : 1));
        if (!remembered) Assert.That(corpses[0].GetProperty("nameId").GetString(), Is.EqualTo("corpse-name-sentinel"));
    }

    [Test]
    public void VisibleMissingLegsIdentifyTwoProsthesesAndDoNotExposeHiddenPatients()
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world);
            var patient = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            ClearPerception(actor);
            patient.Body.Severed.Add(BodyPart.LegL);
            patient.Body.Severed.Add(BodyPart.LegR);
            actor.Perception.Agents.Add(new PerceivedAgent { Id = patient.Id, CanSee = true });
            return actor.Id.Value;
        });
        using (var response = Describe(host, actorId))
        {
            var body = response.RootElement.GetProperty("visibleNpcs")[0].GetProperty("bodyObservation");
            var missing = body.GetProperty("limbs").EnumerateArray().Where(x => x.GetProperty("needsProsthetic").GetBoolean()).ToArray();
            Assert.That(missing.Select(x => x.GetProperty("part").GetString()), Is.EquivalentTo(new[] { "LegL", "LegR" }));
            Assert.That(body.GetProperty("installationReadinessChecked").GetBoolean(), Is.False);
        }
        host.Read(world => { Observer(world).Perception.Agents[0].CanSee = false; return true; });
        using var hidden = Describe(host, actorId);
        Assert.That(hidden.RootElement.GetProperty("visibleNpcs").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void AnInstalledLegDoesNotRequestASecondProsthesisForTheSameLimb()
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world);
            var patient = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            ClearPerception(actor);
            patient.Body.Severed.Add(BodyPart.LegL);
            patient.Body.Condition(BodyPart.LegL).Prosthetic = new ProstheticState { DefinitionId = "prosthetic-test", Part = BodyPart.LegL };
            actor.Perception.Agents.Add(new PerceivedAgent { Id = patient.Id, CanSee = true });
            return actor.Id.Value;
        });
        using var response = Describe(host, actorId);
        var leg = response.RootElement.GetProperty("visibleNpcs")[0].GetProperty("bodyObservation").GetProperty("limbs")
            .EnumerateArray().Single(x => x.GetProperty("part").GetString() == "LegL");
        Assert.That(leg.GetProperty("missing").GetBoolean(), Is.True);
        Assert.That(leg.GetProperty("needsProsthetic").GetBoolean(), Is.False);
        Assert.That(leg.GetProperty("prostheticDefinitionId").GetString(), Is.EqualTo("prosthetic-test"));
    }

    [Test]
    public void VisibleDashaHasTheUiNameAndObservedAidWithoutPrivateStateOrAnIntroduction()
    {
        using var host = CreateHost();
        var setup = host.Read(world =>
        {
            var actor = Observer(world);
            var target = world.Entities.Npcs.Values.First(n => n.Id != actor.Id && n.Faction == actor.Faction);
            ClearPerception(actor);
            target.DisplayName = "Dasha";
            target.Inventory.Items.Add(new ItemInstance("private-pocket-sentinel"));
            // Do not create a relationship or reveal needs; the observer already
            // has an aid assessment, which must survive the MCP boundary.
            actor.Perception.Agents.Add(new PerceivedAgent
            {
                Id = target.Id, CanSee = true, Faction = target.Faction,
                Distance = 2.5f, Suffering = 0.9f, AidKind = AidKind.Hydrate,
                IsUnconscious = true, IsDying = true, IsReachable = true,
            });
            return (actor.Id.Value, target.Id.Value, actor.Social.Relationships.Count);
        });
        using var response = Describe(host, setup.Item1);
        var rows = response.RootElement.GetProperty("visibleNpcs");
        var dasha = rows[0];
        Assert.Multiple(() =>
        {
            Assert.That(rows.GetArrayLength(), Is.EqualTo(1));
            Assert.That(dasha.GetProperty("npcId").GetInt32(), Is.EqualTo(setup.Item2));
            Assert.That(dasha.GetProperty("nameId").GetString(), Is.EqualTo("Dasha"));
            Assert.That(dasha.GetProperty("names").GetProperty("ru").GetString(), Is.EqualTo("Даша"));
            Assert.That(dasha.GetProperty("names").GetProperty("en").GetString(), Is.EqualTo("Dasha"));
            Assert.That(dasha.GetProperty("suffering").GetSingle(), Is.EqualTo(0.9f));
            Assert.That(dasha.GetProperty("aidKind").GetString(), Is.EqualTo("Hydrate"));
            Assert.That(dasha.GetProperty("unconscious").GetBoolean(), Is.True);
            Assert.That(dasha.GetProperty("dying").GetBoolean(), Is.True);
            Assert.That(dasha.GetProperty("distance").GetSingle(), Is.EqualTo(2.5f));
            Assert.That(dasha.TryGetProperty("needs", out _), Is.False);
            Assert.That(dasha.TryGetProperty("inventory", out _), Is.False);
            Assert.That(dasha.TryGetProperty("memory", out _), Is.False);
            Assert.That(dasha.GetProperty("observerRelationship").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.RootElement.GetRawText(), Does.Not.Contain("private-pocket-sentinel"));
        });
        host.Read(world =>
        {
            Assert.That(Observer(world).Social.Relationships.Count, Is.EqualTo(setup.Item3), "Observation must not create acquaintance");
            return true;
        });
    }

    [Test]
    public void GiftRecipientSelectionSeesOnlyTheObserversOwnRelationship()
    {
        using var host = CreateHost();
        var actorId = host.Read(world =>
        {
            var actor = Observer(world);
            var target = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            ClearPerception(actor);
            actor.Perception.Agents.Add(new PerceivedAgent { Id = target.Id, CanSee = true });
            actor.Social.GetOrCreate(target.Id).Affinity = .8f;
            target.Social.GetOrCreate(actor.Id).Affinity = -.7f;
            return actor.Id.Value;
        });
        using var response = Describe(host, actorId);
        var row = response.RootElement.GetProperty("visibleNpcs")[0];
        Assert.That(row.GetProperty("observerRelationship").GetProperty("affinity").GetSingle(), Is.EqualTo(.8f));
        Assert.That(row.GetRawText(), Does.Not.Contain("-0.7"));
    }

    [Test]
    public void SightIsRequiredAndUnknownNamesStayExactWhileVisibleHostilesAreIncluded()
    {
        using var host = CreateHost();
        var id = host.Read(world =>
        {
            var actor = Observer(world);
            ClearPerception(actor);
            var heard = new NPCState { Id = new EntityId(980002), DisplayName = "hidden-name-sentinel" };
            var seen = new NPCState { Id = new EntityId(980001), DisplayName = "Custom Name", Faction = Faction.Outsiders };
            world.Entities.Npcs[heard.Id] = heard;
            world.Entities.Npcs[seen.Id] = seen;
            actor.Perception.Agents.Add(new PerceivedAgent { Id = heard.Id, CanHear = true, CanSee = false });
            actor.Perception.Remembered.Add(new RememberedAgent { Id = heard.Id });
            actor.Perception.Hostiles.Add(new PerceivedAgent { Id = seen.Id, CanSee = true, Faction = seen.Faction, IsMoving = true });
            actor.Perception.Hostiles.Add(new PerceivedAgent { Id = new EntityId(989999), CanSee = true });
            return actor.Id.Value;
        });
        using var response = Describe(host, id);
        var rows = response.RootElement.GetProperty("visibleNpcs");
        Assert.Multiple(() =>
        {
            Assert.That(rows.GetArrayLength(), Is.EqualTo(1));
            Assert.That(rows[0].GetProperty("npcId").GetInt32(), Is.EqualTo(980001));
            Assert.That(rows[0].GetProperty("names").GetProperty("ru").GetString(), Is.EqualTo("Custom Name"));
            Assert.That(rows[0].GetProperty("hostile").GetBoolean(), Is.True);
            Assert.That(rows[0].GetProperty("sameCamp").GetBoolean(), Is.False);
            Assert.That(rows[0].GetProperty("moving").GetBoolean(), Is.True);
            Assert.That(rows.GetRawText(), Does.Not.Contain("980002").And.Not.Contain("989999"));
            Assert.That(response.RootElement.GetRawText(), Does.Not.Contain("hidden-name-sentinel"));
        });
    }

    [Test]
    public void VisiblePeopleRemainAddressablePastTheSummaryBudgetInSortedOrder()
    {
        using var host = CreateHost();
        var count = ContextBudget.Default.MaxAgentRows + 1;
        var id = host.Read(world =>
        {
            var actor = Observer(world);
            ClearPerception(actor);
            for (var index = count - 1; index >= 0; index--)
            {
                var person = new NPCState { Id = new EntityId(970000 + index), DisplayName = "Custom" + index };
                world.Entities.Npcs[person.Id] = person;
                actor.Perception.Agents.Add(new PerceivedAgent { Id = person.Id, CanSee = true });
            }
            // Duplicated perception does not duplicate the addressable person.
            actor.Perception.Agents.Add(actor.Perception.Agents[0]);
            return actor.Id.Value;
        });
        using var response = Describe(host, id);
        var rows = response.RootElement.GetProperty("visibleNpcs");
        Assert.That(rows.GetArrayLength(), Is.EqualTo(count));
        for (var index = 0; index < count; index++)
            Assert.That(rows[index].GetProperty("npcId").GetInt32(), Is.EqualTo(970000 + index));
    }

    [Test]
    public void EmbeddedNamesCarryBothLocalesAndTheCanonicalUiName()
    {
        using var resource = typeof(McpTools).Assembly.GetManifestResourceStream("HexLive.Mcp.NpcNames.json");
        Assert.That(resource, Is.Not.Null, "The published server must carry the generated names");
        var embedded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(resource!)!;
        // Source freshness is checked by Tools/export_npc_names.py --check;
        // a published server archive intentionally does not contain Unity assets.
        Assert.That(embedded.Count, Is.GreaterThanOrEqualTo(31));
        foreach (var (nameId, names) in embedded)
        {
            Assert.That(nameId, Is.Not.Empty);
            Assert.That(names.Keys, Is.EquivalentTo(new[] { "en", "ru" }), nameId);
            Assert.That(names["en"], Is.Not.Empty, nameId);
            Assert.That(names["ru"], Is.Not.Empty, nameId);
        }
        Assert.That(embedded["dasha"]["en"], Is.EqualTo("Dasha"));
        Assert.That(embedded["dasha"]["ru"], Is.EqualTo("Даша"));
    }

    private static NPCState Observer(WorldState world) => world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

    private static void ClearPerception(NPCState actor)
    {
        actor.Perception.Agents.Clear();
        actor.Perception.Hostiles.Clear();
        actor.Perception.Remembered.Clear();
    }

    private static JsonDocument Describe(WorldHost host, int npcId)
    {
        var tools = new McpTools(host, new ControlLeases(45));
        var result = tools.Call("describe_colonist", JsonSerializer.SerializeToElement(new { npcId }), "mcp:npc-observation-test", out var error);
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
        Path.Combine(Path.GetTempPath(), "mcp-npcs-" + Guid.NewGuid().ToString("N") + ".sav"),
        Path.Combine(Root(), "SimData/simdata.json"), false);
}
