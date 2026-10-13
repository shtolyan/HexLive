using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Accounts;
using HexLive.Server.Tests.Accounts;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Server.Voice;
using HexLive.Simulation.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.Server.Tests.Voice;

[NonParallelizable]
public sealed class VoiceRelationshipApiTests
{
    [Test]
    public async Task PublishedPhoneRelationshipRouteExistsAndIsScopedToAssignedOwner()
    {
        var dir = Directory.CreateTempSubdirectory("voice-api-");
        var env = new Dictionary<string,string?>();
        var values = new Dictionary<string,string> { ["OPENAI_API_KEY"]="fixture", ["ELEVENLABS_API_KEY"]="fixture",
            ["VOICEBRAIN_SECRET"]="fixture", ["VOICEBRAIN_AGENT_ID"]="fixture", ["VOICEBRAIN_STATE_DIR"]=dir.FullName,
            ["VOICEBRAIN_USAGE_DIR"]="", ["VOICEBRAIN_VOICES"]="", ["VOICEBRAIN_TICKET_KEY"]="" };
        try
        {
            foreach (var pair in values) { env[pair.Key] = Environment.GetEnvironmentVariable(pair.Key); Environment.SetEnvironmentVariable(pair.Key,pair.Value); }
            var store = new AssetRegistryStore(Path.Combine(dir.FullName,"assets"));
            using var worlds = new WorldSupervisor(12345,GameMode.Feud,Path.Combine(dir.FullName,"world.sav"),
                new AssetGarmentCatalog(store,AccountHttpTests.RepoFile("SimData/simdata.json")),false,false,new LlmHostOptions(),null,CancellationToken.None,startPaused:true);
            var player = new string('1',32);
            worlds.Assignments!.AdminAssign(player,1);
            await using var app = AccountHttpTests.BuildApp();
            var voice = VoiceEndpoints.Map(app,worlds,new ControlLeases(45),new AgentSessionRegistry(),new PlayerHttpIdentity(s => s=="fixture"),NullLogger.Instance);
            Assert.That(voice,Is.Not.Null);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress=new Uri(app.Urls.First()) };
                const string route="/api/voice/v1/relationship/1";
                Assert.That((await client.GetAsync(route)).StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
                client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","fixture");
                client.DefaultRequestHeaders.Add("X-HexLive-Client-Id",player);
                var response = await client.GetAsync(route);
                Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Headers.CacheControl?.NoStore,Is.True,"Names and owner-scoped relationships cannot be served from a stale browser cache");
                var view = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.That(view.GetProperty("voiceName").GetString(),Is.EqualTo("Голос"));
                Assert.That(view.GetProperty("familiarity").GetSingle(),Is.Zero);
                Assert.That((await client.GetAsync("/api/voice/v1/relationship/2")).StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
                client.DefaultRequestHeaders.Remove("X-HexLive-Client-Id");
                client.DefaultRequestHeaders.Add("X-HexLive-Client-Id",new string('2',32));
                Assert.That((await client.GetAsync(route)).StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
            }
            finally { await app.StopAsync(); }
        }
        finally { foreach(var pair in env) Environment.SetEnvironmentVariable(pair.Key,pair.Value); dir.Delete(true); }
    }
}
