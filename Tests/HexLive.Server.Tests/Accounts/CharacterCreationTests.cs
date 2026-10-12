using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using HexLive.Server.Accounts;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using Microsoft.AspNetCore.Builder;
using NUnit.Framework;

namespace HexLive.Server.Tests.Accounts;

[NonParallelizable]
public sealed class CharacterCreationTests
{
    private const string Root = AccountEndpoints.Prefix + "/character";

    internal static CharacterCreateRequest Request(string body = "Marta", string camp = "Colony") => new() {
        RequestId = Guid.NewGuid().ToString("N"), Name = "Новый игрок", Body = body,
        Skin = body == "Marta" ? "Rita" : "Tonny", Hair = body == "Marta" ? "AdellHair" : "none",
        Eyes = "", Camp = camp, Traits = new() { "Brave", "Diligent" },
        Attributes = AttributeSet.All.Select(k => new CreationValue { Id = k.ToString(), Value = .6f }).ToList(),
        Skills = SkillSet.All.Select(k => new CreationValue { Id = k.ToString(), Value = .3f }).ToList()
    };

    // Publish tiny inert fixture payloads, never build/publish real AssetBundles.
    internal static async Task PublishPeople(string assetRoot)
    {
        var registry = new AssetRegistryStore(assetRoot);
        var catalog = JsonNode.Parse(File.ReadAllText(AccountHttpTests.RepoFile("Assets/HexLiveContent/People/catalog.json")))!;
        foreach (var row in catalog["records"]!.AsArray().Where(r => r!["type"]!.GetValue<string>() is "actor" or "hair" or "wear"))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("test-payload-" + row!["id"]!.GetValue<string>());
            var staged = Path.Combine(registry.StagingPath, Guid.NewGuid().ToString("N")); File.WriteAllBytes(staged, bytes);
            await registry.PublishAsync(new ContentPublishCandidate {
                Type = row["type"]!.GetValue<string>(), Id = row["id"]!.GetValue<string>(),
                Metadata = row["metadata"]!.Deserialize<Dictionary<string, JsonElement>>()!,
                Variants = new() { new() { Platform = "WebGL", RuntimeProfile = "unity6000-content1", EntryAsset = "main",
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Size = bytes.Length, StagedPath = staged } }
            });
        }
    }

    [Test]
    public async Task OptionsExposeOnlyCampProjectionAndPublishedCompatibleAppearance()
    {
        await using var f = await Fixture.Create();
        var response = await f.Client.GetAsync(Root + "/options");
        Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
        var options = (await response.Content.ReadFromJsonAsync<JsonElement>());
        foreach (var camp in options.GetProperty("camps").EnumerateArray())
            Assert.That(camp.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "id", "nameKey", "descriptionKey", "hasSpace" }));
        var text = options.GetRawText();
        Assert.That(text.ToLowerInvariant(), Does.Not.Contain("npcid").And.Not.Contain("residents").And.Not.Contain("characters"));
        var names = f.Worlds.Host.Read(w => w.Entities.Npcs.Values.Select(n => n.DisplayName).ToArray());
        foreach (var name in names.Where(n => n != "Marta" && n != "Kshishtof" && n != "Molly" && n != "Jana" && n != "Jolly" && n != "Tonny" && n != "Masha" && n != "Rita"))
            Assert.That(text, Does.Not.Contain('"' + name + '"'));
        var bodies = options.GetProperty("bodies").EnumerateArray().ToArray();
        Assert.That(bodies.Select(b => b.GetProperty("id").GetString()), Is.EquivalentTo(new[] { "Marta", "Kshishtof" }));
        Assert.That(options.GetProperty("skins").GetArrayLength(), Is.EqualTo(8));
        Assert.That(options.GetProperty("eyes").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "" }));
        Assert.That(options.GetProperty("attributeBudget").GetSingle(), Is.EqualTo(4.2f).Within(.0001));
        Assert.That(options.GetProperty("skillBudget").GetSingle(), Is.EqualTo(3f));
        var me = await f.Client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
        Assert.That(me.GetProperty("character").ValueKind, Is.EqualTo(JsonValueKind.Null), "first login never appropriates a resident");
        f.Client.DefaultRequestHeaders.Authorization = null;
        Assert.That((await f.Client.GetAsync(Root + "/options")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await f.Client.PostAsJsonAsync(Root, Request())).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase("Marta", "Colony2")]
    [TestCase("Kshishtof", "Colony")]
    public async Task CreatesInCampWithExactUnderwearOwnershipAndPersistentReceipt(string body, string camp)
    {
        await using var f = await Fixture.Create();
        var request = Request(body, camp);
        var count = f.Worlds.Host.Read(w => w.Entities.Npcs.Count);
        var reply = await f.CreateCharacter(request);
        var id = reply.GetProperty("npcId").GetInt32();
        Assert.That(id, Is.GreaterThanOrEqualTo(PlayerCharacterCreation.FirstNpcId));
        f.Worlds.Host.Read(w => {
            var npc = w.Entities.Npcs[new EntityId(id)];
            Assert.That(npc.Faction.ToString(), Is.EqualTo(camp));
            Assert.That(npc.ActorMesh, Is.EqualTo(body)); Assert.That(npc.SkinSet, Is.EqualTo(request.Skin));
            Assert.That(npc.WornItems.Select(i => i.DefinitionId), Is.EquivalentTo(PlayerCharacterCreation.StartingClothing(body)));
            Assert.That(npc.WornItems.All(i => i.OwnerId == id), Is.True);
            Assert.That(npc.Traits.Bits, Is.EqualTo((1UL << (int)TraitKind.Brave) | (1UL << (int)TraitKind.Diligent)));
            Assert.That(AttributeSet.All.Sum(npc.Attributes.Get), Is.EqualTo(4.2f).Within(.0001));
            Assert.That(SkillSet.All.Sum(npc.Skills.Get), Is.EqualTo(2.7f).Within(.0001));
            Assert.That(HexSpatialMath.HexDistance(npc.Tile, w.FactionHomes[npc.Faction]), Is.LessThanOrEqualTo(1));
            Assert.That(w.Occupancy.JunctionOwner[npc.CurrentJunction!.Value], Is.EqualTo(npc.Id));
            Assert.That(w.PlayerControlledNpcs, Does.Contain(id));
            return true;
        });
        Assert.That(f.Worlds.Assignments.StillAssigned(f.Session.PlayerId, id), Is.True);
        Assert.That(f.Worlds.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(count + 1));
        var again = await f.CreateCharacter(request);
        Assert.That(again.GetRawText(), Is.EqualTo(reply.GetRawText()));
        var second = await f.Client.PostAsJsonAsync(Root, Request(body));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), Is.EqualTo("character_alive"));
        await f.Restart();
        Assert.That((await f.CreateCharacter(request)).GetRawText(), Is.EqualTo(reply.GetRawText()));
        var me = await f.Client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
        Assert.That(me.GetProperty("character").GetProperty("npcId").GetInt32(), Is.EqualTo(id));
        Assert.That(f.Worlds.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(count + 1));
        // Simulate a crash between the committed world and its ownership sidecar.
        var path = f.Worlds.Library.AssignmentPath(f.Worlds.Library.ActiveId);
        f.Worlds.Dispose(); File.Delete(path); await f.Restart(disposed: true);
        me = await f.Client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
        Assert.That(me.GetProperty("character").GetProperty("npcId").GetInt32(), Is.EqualTo(id));
        Assert.That(PlayerCharacterAssignments.Load(path, true).StillAssigned(f.Session.PlayerId, id), Is.True);
    }

    [Test]
    public async Task DeathAllowsNewRequestButOldRequestNeverRevivesAndAccountNamespacesAreSeparate()
    {
        await using var f = await Fixture.Create();
        var request = Request(); var original = await f.CreateCharacter(request); var id = original.GetProperty("npcId").GetInt32();
        f.Worlds.Host.Read(w => { var npc = w.Entities.Npcs[new EntityId(id)]; w.Entities.Npcs.Remove(npc.Id); w.Entities.Corpses[npc.Id] = npc; return true; });
        var me = await f.Client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
        Assert.That(me.GetProperty("needsNewCharacter").GetBoolean(), Is.True);
        Assert.That((await f.CreateCharacter(request)).GetRawText(), Is.EqualTo(original.GetRawText()));
        var replacement = await f.CreateCharacter(Request());
        Assert.That(replacement.GetProperty("npcId").GetInt32(), Is.Not.EqualTo(id));
        me = await f.Client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
        Assert.That(me.GetProperty("needsNewCharacter").GetBoolean(), Is.False);
        var other = f.Store.CreateSession(new("other", "apple.com"));
        f.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.SessionToken);
        request.Camp = "Colony2";
        Assert.That((await f.CreateCharacter(request)).GetProperty("npcId").GetInt32(), Is.Not.EqualTo(id));
    }

    [TestCase("attributes", "budget")]
    [TestCase("skills", "budget")]
    [TestCase("incomplete", "budgetIncomplete")]
    [TestCase("alias", "range")]
    [TestCase("skin", "unknown")]
    [TestCase("hair", "unknown")]
    [TestCase("hairColour", "unknown")]
    [TestCase("eyes", "incompatible")]
    [TestCase("traits", "unknown")]
    [TestCase("voice", "unknown")]
    public async Task InvalidChoicesCannotMutateWorld(string field, string code)
    {
        await using var f = await Fixture.Create(); var request = Request();
        switch (field)
        {
            case "attributes": request.Attributes.ForEach(v => v.Value = 1); break;
            case "skills": request.Skills.ForEach(v => v.Value = 1); break;
            case "incomplete": request.Skills.RemoveAt(0); break;
            case "alias": request.Attributes[1].Id = "0"; break;
            case "skin": request.Skin = "no-such-skin"; break;
            case "hair": request.Hair = "no-such-hair"; break;
            case "hairColour": request.HairColour = "no-such-colour"; break;
            case "eyes": request.Eyes = "blue"; break;
            case "traits": request.Traits.Add("FakeTrait"); break;
            case "voice": request.Voice = "FakeVoice"; break;
        }
        var count = f.Worlds.Host.Read(w => w.Entities.Npcs.Count);
        var response = await f.Client.PostAsJsonAsync(Root, request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(code));
        Assert.That(f.Worlds.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(count));
    }

    [TestCase("clothing")]
    [TestCase("ClOtHiNg")]
    [TestCase("attributeBudget")]
    [TestCase("npcId")]
    public async Task ClientCannotSendClothesOrServerFieldsEvenNull(string field)
    {
        await using var f = await Fixture.Create();
        var json = JsonSerializer.SerializeToNode(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject(); json[field] = null;
        Assert.That((await f.Client.PostAsJsonAsync(Root, json)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(f.Worlds.Host.Read(w => w.PlayerCharacterCreations.Count), Is.Zero);
    }

    [Test]
    public async Task ConcurrentRequestsOnlyCreateOneAndAttemptsAreRateLimited()
    {
        await using var f = await Fixture.Create(); var request = Request();
        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Client.PostAsJsonAsync(Root, request)));
        var texts = await Task.WhenAll(replies.Select(r => r.Content.ReadAsStringAsync()));
        Assert.That(replies.All(r => r.IsSuccessStatusCode), Is.True); Assert.That(texts.Distinct().Count(), Is.EqualTo(1));
        Assert.That(f.Worlds.Host.Read(w => w.PlayerCharacterCreations.Count), Is.EqualTo(1));
        for (var i = 4; i < 10; i++) Assert.That((await f.Client.PostAsJsonAsync(Root, Request())).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await f.Client.PostAsJsonAsync(Root, request)).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [TestCase(false, "camp_full")]
    [TestCase(true, "no_free_spawn")]
    public async Task UnavailableSpaceHasExplicitCodeAndOptionsFlag(bool blockNodes, string code)
    {
        await using var f = await Fixture.Create();
        f.Worlds.Host.Read(w => {
            if (blockNodes) foreach (var tile in w.Tiles.Items.Values.Where(t => HexSpatialMath.HexDistance(t.Coord, w.FactionHomes[Faction.Colony]) <= 1))
                foreach (var id in tile.Junctions) w.Junctions.Items[id].Blocked = true;
            else {
                w.CreationConfig = new WorldCreationConfig { PopulationLimit = 256 };
                w.CreationConfig.Camps.Add(new CampCreationConfig { Faction = Faction.Colony, PopulationLimit = 0 });
            }
            return true;
        });
        var options = await f.Client.GetFromJsonAsync<JsonElement>(Root + "/options");
        Assert.That(options.GetProperty("camps").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "Colony").GetProperty("hasSpace").GetBoolean(), Is.False);
        var response = await f.Client.PostAsJsonAsync(Root, Request());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), Is.EqualTo(code));
    }

    [Test]
    public async Task FailedWorldSaveRollsBackButFailedSidecarRecoversWithoutDuplicate()
    {
        await using var f = await Fixture.Create();
        await f.Client.GetAsync(AccountEndpoints.Prefix + "/me");
        var request = Request(); var before = f.Worlds.Host.Read(w => w.Entities.Npcs.Count);
        var temporary = f.Worlds.Library.SavePath(f.Worlds.Library.ActiveId) + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            var failed = await f.Client.PostAsJsonAsync(Root, request);
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(f.Worlds.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(before));
            Assert.That(f.Worlds.Host.Read(w => w.PlayerCharacterCreations.Count), Is.Zero);
            Assert.That(f.Worlds.Host.Read(w => w.PlayerControlledNpcs.Contains(PlayerCharacterCreation.FirstNpcId)), Is.False);
        }
        finally { Directory.Delete(temporary); }
        var assignments = f.Worlds.Library.AssignmentPath(f.Worlds.Library.ActiveId);
        File.Move(assignments, assignments + ".fixture-backup"); Directory.CreateDirectory(assignments);
        try
        {
            var failed = await f.Client.PostAsJsonAsync(Root, request);
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(f.Worlds.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(before + 1));
        }
        finally { Directory.Delete(assignments); File.Move(assignments + ".fixture-backup", assignments); }
        await f.Restart();
        var reply = await f.CreateCharacter(request);
        Assert.That(f.Worlds.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(before + 1));
        Assert.That(f.Worlds.Assignments.StillAssigned(f.Session.PlayerId, reply.GetProperty("npcId").GetInt32()), Is.True);
    }

    [Test]
    public async Task NonCreatorAccountCanCreateInActivatedLobbyAndKeepsOwnership()
    {
        await using var f = await Fixture.Create();
        var config = f.Worlds.Host.Read(WorldCreation.Defaults);
        config.Name = "Other player's lobby"; config.CreatorPlayerId = new string('a', 32);
        f.Worlds.CreateLibraryWorld(config.Seed, config.Mode, config, Guid.NewGuid().ToString("N"));
        f.Worlds.Host.PauseAsOperator();
        var result = await f.CreateCharacter(Request("Kshishtof", "Colony2"));
        var id = result.GetProperty("npcId").GetInt32();
        Assert.That(f.Worlds.Assignments.StillAssigned(f.Session.PlayerId, id), Is.True);
        var captured = f.Worlds.CaptureViewerSession();
        Assert.That(f.Worlds.Assignments.Reconcile(captured.Host, f.Session.PlayerId, 1, captured.Lifetime,
            captured.WorldGeneration, accountCharacter: true), Is.EqualTo(new[] { id }));
        Assert.That(f.Worlds.Assignments.Reconcile(captured.Host, config.CreatorPlayerId, 8, captured.Lifetime,
            captured.WorldGeneration), Does.Not.Contain(id), "legacy lobby creator cannot take another account's body");
    }

    [Test]
    public void PrimalSkinContractMatchesEmbeddedMaterials()
    {
        foreach (var body in new[] { "Marta", "Kshishtof" })
        {
            var prefab = File.ReadAllText(AccountHttpTests.RepoFile("Assets/HexLiveContent/People/Prefabs/Actors/" + body + ".prefab"))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            var skins = prefab.Split("  skins:\n", StringSplitOptions.None)[1].Split("--- !u!", StringSplitOptions.None)[0];
            var ids = System.Text.RegularExpressions.Regex.Matches(skins, @"  - id: (.+)").Select(m => m.Groups[1].Value.Trim());
            Assert.That(body == "Marta" ? CharacterAppearanceValidation.PrimalFemaleSkins : CharacterAppearanceValidation.PrimalMaleSkins, Is.EquivalentTo(ids));
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("character-api-");
        private readonly FirebaseFixture _firebase = new();
        private readonly string _originalSimData = SimDataFile.ExportJson();
        private WebApplication _app = null!;
        private AssetGarmentCatalog _catalog = null!;
        public WorldSupervisor Worlds = null!;
        public AccountStore Store = null!;
        public GameSession Session = null!;
        public HttpClient Client = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await PublishPeople(Path.Combine(f._directory.FullName, "assets"));
            f._catalog = new AssetGarmentCatalog(new AssetRegistryStore(Path.Combine(f._directory.FullName, "assets")), AccountHttpTests.RepoFile("SimData/simdata.json"));
            f.Store = new AccountStore(Path.Combine(f._directory.FullName, "accounts.json"), FirebaseFixture.Project);
            f.Session = f.Store.CreateSession(new("player", "google.com"));
            await f.Start(); return f;
        }
        private async Task Start()
        {
            Worlds = new WorldSupervisor(12345, GameMode.BigIsland, Path.Combine(_directory.FullName, "world.sav"), _catalog, false, false, new LlmHostOptions(), null, default, startPaused: true);
            _app = AccountHttpTests.BuildApp(); AccountEndpoints.Map(_app, Store, _firebase.Verifier, Worlds); await _app.StartAsync();
            Client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Session.SessionToken);
        }
        public async Task Restart(bool disposed = false)
        {
            Client.Dispose(); await _app.StopAsync(); await _app.DisposeAsync();
            if (!disposed) Worlds.Dispose(); await Start();
        }
        public async Task<JsonElement> CreateCharacter(CharacterCreateRequest request)
        {
            var response = await Client.PostAsJsonAsync(Root, request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); Worlds.Dispose(); _firebase.Dispose(); _directory.Delete(true); SimDataFile.ApplyJson(_originalSimData);
        }
    }
}
