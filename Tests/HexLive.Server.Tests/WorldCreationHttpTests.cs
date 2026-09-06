using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Server.Admin;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[NonParallelizable]
public sealed class WorldCreationHttpTests
{
    [Test]
    public async Task AdministrativeApiRejectsPlayerTokenAndValidatesBeforeMutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hexlive-lobby-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = PrototypeWorldDefinitionFactory.Override;
        var catalogBefore = SimDataFile.ExportJson();
        PrototypeWorldDefinitionFactory.Override = seed =>
        {
            var b = new WorldBootstrapDefinition(); b.Simulation.Seed = seed;
            var fragment = new FragmentBootstrap { Id = 1 }; b.Fragments.Add(fragment);
            for (var q = -2; q <= 2; q++) for (var r = -2; r <= 2; r++) fragment.Tiles.Add(new TileBootstrap { Q = q, R = r });
            b.FactionHomes.Add(new FactionHomeBootstrap { Faction = Faction.Colony, StakeCampfireSite = false });
            return b;
        };
        try
        {
            var basePath = Path.Combine(directory, "base.json"); File.WriteAllText(basePath, catalogBefore);
            var registry = new AssetRegistryStore(Path.Combine(directory, "assets"));
            var bytes = System.Text.Encoding.UTF8.GetBytes("test actor payload");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var staged = Path.Combine(registry.StagingPath, "actor-test"); File.WriteAllBytes(staged, bytes);
            await registry.PublishAsync(new ContentPublishCandidate
            {
                Type = "actor", Id = "Molly",
                Variants = new() { new() { Platform = "StandaloneOSX", RuntimeProfile = "unity6000-content1", Sha256 = hash, Size = bytes.Length, PayloadType = "assetBundle", EntryAsset = "main", StagedPath = staged } },
            });
            var catalog = new AssetGarmentCatalog(registry, basePath);
            using var supervisor = new WorldSupervisor(7, GameMode.Feud, Path.Combine(directory, "save.sav"), catalog, false, false, new LlmHostOptions(), null, default);
            var account = AdminAccount.LoadOrCreate(Path.Combine(directory, "admin.json")); account.ChangePassword("Lobby-test-password-123!");
            var sessions = new AdminSessions();
            var tokenPath = Path.Combine(directory, "player.txt"); File.WriteAllText(tokenPath, "hexplay_test_only_not_a_real_secret");
            var playerToken = AccessTokenFile.LoadOrCreate(tokenPath, "test", "hexplay_");
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Services.AddRouting(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            WorldCreationEndpoints.Map(app, supervisor, account, sessions, registry, null, playerToken);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(System.Linq.Enumerable.First(app.Urls)), Timeout = TimeSpan.FromSeconds(30) };
                const string root = "/api/worlds/v1";
                Assert.That((await client.GetAsync(root)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", playerToken.Value);
                Assert.That((await client.GetAsync(root)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                using var login = await client.PostAsJsonAsync(root + "/login", new { password = "Lobby-test-password-123!" });
                login.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.RootElement.GetProperty("token").GetString());
                Assert.That((await client.GetAsync(root)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                var active = supervisor.Library.ActiveId;
                using var rejected = await client.PostAsJsonAsync(root, new WorldCreateRequest { RequestId = Guid.NewGuid().ToString("N"), Config = new WorldCreationConfig() });
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(supervisor.Library.ActiveId, Is.EqualTo(active));
                // A rejected creation must release the operation slot for subsequent calls.
                using var preview = await client.PostAsJsonAsync(root + "/preview", new WorldPreviewRequest { Seed = 7, PlayerId = "11111111111111111111111111111111" });
                Assert.That(preview.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(supervisor.Library.ActiveId, Is.EqualTo(active));
                var config = new WorldCreationConfig { Name = "HTTP world", CreatorPlayerId = "11111111111111111111111111111111", Seed = 7, PopulationLimit = 10, CatalogRevision = supervisor.CatalogRegistryRevision };
                config.Camps.Add(new CampCreationConfig { Faction = Faction.Colony });
                config.Characters.Add(new CharacterCreationConfig { Id = 1, Name = "Hero", Controlled = true });
                var create = new WorldCreateRequest { RequestId = Guid.NewGuid().ToString("N"), Config = config };
                var simultaneous = await Task.WhenAll(client.PostAsJsonAsync(root, create), client.PostAsJsonAsync(root, create));
                foreach (var response in simultaneous)
                {
                    Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.Accepted, HttpStatusCode.OK, HttpStatusCode.Conflict));
                    response.Dispose();
                }
                WorldOperation? operation = null;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    operation = await client.GetFromJsonAsync<WorldOperation>(root + "/operations/" + create.RequestId);
                    if (operation!.State != "running") break;
                    await Task.Delay(100);
                }
                Assert.That(operation!.State, Is.EqualTo("complete"), operation.Error);
                Assert.That(supervisor.Library.List().Count, Is.EqualTo(2), "Concurrent requests created exactly one new world.");
                using var activation = await client.PostAsJsonAsync(root + "/" + active + "/activate", new { });
                activation.EnsureSuccessStatusCode();
                using var replay = await client.PostAsJsonAsync(root, create); replay.EnsureSuccessStatusCode();
                Assert.That(supervisor.Library.ActiveId, Is.EqualTo(active), "Completed replay does not switch the active world.");
                config.Name = "Different configuration";
                using var conflict = await client.PostAsJsonAsync(root, create);
                Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            PrototypeWorldDefinitionFactory.Override = previous;
            SimDataFile.ApplyJson(catalogBefore);
            Directory.Delete(directory, true);
        }
    }
}
