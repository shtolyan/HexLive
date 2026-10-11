using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Server.Accounts;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HexLive.Server.Tests.Accounts;

[NonParallelizable]
public sealed class AccountHttpTests
{
    [Test]
    public async Task SessionMeDeathAcknowledgementRestartAndLogout()
    {
        using var f = new FirebaseFixture();
        var directory = Directory.CreateTempSubdirectory("accounts-http-");
        try
        {
            var catalog = new AssetGarmentCatalog(new AssetRegistryStore(Path.Combine(directory.FullName, "assets")), RepoFile("SimData/simdata.json"));
            using var worlds = new WorldSupervisor(12345, GameMode.BigIsland, Path.Combine(directory.FullName, "world.sav"),
                catalog, false, false, new LlmHostOptions(), null, default, startPaused: true);
            var store = new AccountStore(Path.Combine(directory.FullName, "accounts.json"), FirebaseFixture.Project);
            await using var app = BuildApp();
            AccountEndpoints.Map(app, store, f.Verifier, worlds);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                Assert.That((await client.GetAsync(AccountEndpoints.Prefix + "/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                using var created = await client.PostAsJsonAsync(AccountEndpoints.Prefix + "/session", new { idToken = f.Token() });
                created.EnsureSuccessStatusCode();
                Assert.That(created.Headers.CacheControl!.NoStore, Is.True);
                var session = (await created.Content.ReadFromJsonAsync<GameSession>())!;
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.SessionToken);
                client.DefaultRequestHeaders.Add("X-HexLive-Client-Id", Guid.NewGuid().ToString("N"));
                // Existing §171.9 assignments survive the opt-in explicit-creation policy.
                worlds.Assignments.AdminAssign(session.PlayerId, worlds.Host.Read(w => w.Entities.Npcs.Values.First(n => FactionRelations.IsGirlCamp(n.Faction)).Id.Value));
                var me = await client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
                Assert.That(me.GetProperty("playerId").GetString(), Is.EqualTo(session.PlayerId));
                Assert.That(me.GetProperty("provider").GetString(), Is.EqualTo("google.com"));
                var character = me.GetProperty("character"); var npcId = character.GetProperty("npcId").GetInt32();
                Assert.That(character.GetProperty("alive").GetBoolean(), Is.True);
                Assert.That(me.GetProperty("needsNewCharacter").GetBoolean(), Is.False);
                var notice = worlds.Assignments.PendingNotices(session.PlayerId).Single();
                worlds.Assignments.AcknowledgeNotices(session.PlayerId, notice.Sequence);
                worlds.Host.Read(w => {
                    var npc = w.Entities.Npcs[new EntityId(npcId)]; w.Entities.Npcs.Remove(npc.Id);
                    w.Entities.Corpses[npc.Id] = npc; return true;
                });
                me = await client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
                Assert.That(me.GetProperty("needsNewCharacter").GetBoolean(), Is.True);
                Assert.That(me.GetProperty("messageCode").GetString(), Is.EqualTo("character_died_create_new"));
                Assert.That(me.GetProperty("character").ValueKind, Is.EqualTo(JsonValueKind.Null));
                var death = worlds.Assignments.PendingNotices(session.PlayerId).Single();
                Assert.That(death.Kind, Is.EqualTo("died")); Assert.That(death.Name, Is.EqualTo(notice.Name));
                worlds.Assignments.AcknowledgeNotices(session.PlayerId, death.Sequence);
                var loaded = PlayerCharacterAssignments.Load(worlds.Library.AssignmentPath(worlds.Library.ActiveId), true);
                Assert.That(loaded.Reconcile(worlds.Host, session.PlayerId, 8, accountCharacter: true), Is.Empty);
                Assert.That(loaded.NeedsNewCharacter(session.PlayerId), Is.True);
                Assert.That(loaded.PendingNotices(session.PlayerId), Is.Empty);
                me = await client.GetFromJsonAsync<JsonElement>(AccountEndpoints.Prefix + "/me");
                Assert.That(me.GetProperty("needsNewCharacter").GetBoolean(), Is.True);
                Assert.That((await client.DeleteAsync(AccountEndpoints.Prefix + "/session")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That((await client.GetAsync(AccountEndpoints.Prefix + "/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That((await client.DeleteAsync(AccountEndpoints.Prefix + "/session")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                using var again = await client.PostAsJsonAsync(AccountEndpoints.Prefix + "/session", new { idToken = f.Token("apple.com") });
                var newSession = (await again.Content.ReadFromJsonAsync<GameSession>())!;
                Assert.That(newSession.PlayerId, Is.EqualTo(session.PlayerId));
                worlds.CreateLibraryWorld(54321, GameMode.Feud, null, Guid.NewGuid().ToString("N"));
                Assert.That(store.TryResolve(newSession.SessionToken, out var sameId), Is.True);
                Assert.That(sameId, Is.EqualTo(session.PlayerId));
                Assert.That(worlds.Assignments.NeedsNewCharacter(sameId), Is.False);
            }
            finally { await app.StopAsync(); }
        }
        finally { directory.Delete(true); }
    }

    [Test]
    public async Task RefusalsAreGenericExceptVerifiedPasswordAndIpIsLimited()
    {
        using var f = new FirebaseFixture(); var directory = Directory.CreateTempSubdirectory("accounts-limit-");
        try
        {
            var store = new AccountStore(Path.Combine(directory.FullName, "accounts.json"), FirebaseFixture.Project);
            await using var app = BuildApp();
            AccountEndpoints.Map(app, store, f.Verifier, null!);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                for (var i = 0; i < 10; i++)
                {
                    client.DefaultRequestHeaders.Remove("X-Forwarded-For"); client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2." + i);
                    using var response = await client.PostAsJsonAsync(AccountEndpoints.Prefix + "/session", new {
                        idToken = i == 0 ? f.Token("password", c => c["email_verified"] = false) : i == 1 ? f.Token(change: c => c["aud"] = "wrong") : "broken" });
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.That(error.GetProperty("error").GetString(), Is.EqualTo(i == 0 ? "email_not_verified" : "invalid_credentials"));
                }
                Assert.That((await client.PostAsJsonAsync(AccountEndpoints.Prefix + "/session", new { idToken = f.Token() })).StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
                Assert.That(File.Exists(Path.Combine(directory.FullName, "accounts.json")), Is.False);
            }
            finally { await app.StopAsync(); }
        }
        finally { directory.Delete(true); }
    }

    [Test]
    public async Task DisabledMappingHasNoEndpoints()
    {
        Assert.That(ServerOptions.Parse(Array.Empty<string>(), _ => null)!.FirebaseProject, Is.Null);
        await using var app = BuildApp(); AccountEndpoints.Map(app, null, null, null!); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            Assert.That((await client.GetAsync(AccountEndpoints.Prefix + "/me")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await client.PostAsJsonAsync(AccountEndpoints.Prefix + "/session", new { idToken = "x" })).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await client.DeleteAsync(AccountEndpoints.Prefix + "/session")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally { await app.StopAsync(); }
    }

    [Test]
    public void AccountLimitOverridesLobbyRosterAndLegacySquadSetting()
    {
        var directory = Directory.CreateTempSubdirectory("accounts-lobby-");
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var config = new WorldCreationConfig { CreatorPlayerId = id, Seed = 12345, Name = "Accounts" };
            config.Camps.Add(new CampCreationConfig { Faction = Faction.Colony });
            config.Characters.Add(new CharacterCreationConfig { Id = 1, Name = "masha", Controlled = true });
            config.Characters.Add(new CharacterCreationConfig { Id = 2, Name = "nika", Controlled = true });
            using var host = new WorldHost(12345, GameMode.BigIsland, Path.Combine(directory.FullName, "world.sav"), RepoFile("SimData/simdata.json"), false, creationConfig: config);
            var assignments = PlayerCharacterAssignments.Load(Path.Combine(directory.FullName, "players.json"), false);
            Assert.That(assignments.Reconcile(host, id, 8, accountCharacter: true), Is.Empty);
            assignments.AdminAssign(id, 1);
            Assert.That(assignments.Reconcile(host, id, 8, accountCharacter: true), Has.Count.EqualTo(1));
            Assert.That(assignments.Reconcile(host, id, 8), Has.Count.EqualTo(1), "persistent account policy cannot be expanded by a legacy caller");
        }
        finally { directory.Delete(true); }
    }

    internal static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }
    internal static string RepoFile(string relative)
    {
        for (var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); root != null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, relative))) return Path.Combine(root.FullName, relative);
        throw new FileNotFoundException(relative);
    }
}
