using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using HexLive.AgentCore.Studio;
using HexLive.Server.Accounts;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Server.Tests.Accounts;
using HexLive.Server.Voice;
using HexLive.Simulation.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.Server.Tests.Voice;
[NonParallelizable]
public sealed class VoiceSessionIssuanceTests
{
    private sealed class Provider(string outcome) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (outcome == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(outcome == "upstream" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(outcome switch
                {
                    "malformed" => "{", "missing" => "{}", "wrong-type" => "[]", _ => "{\"token\":\"fixture\"}"
                })
            };
        }
    }

    [TestCase("timeout", 504)]
    [TestCase("upstream", 502)]
    [TestCase("malformed", 502)]
    [TestCase("missing", 502)]
    [TestCase("wrong-type", 502)]
    [TestCase("success", 200)]
    public async Task ProviderFailuresAreBoundedAndNeverCreateSuccessfulSessionReceipt(string outcome, int status)
    {
        var dir = Directory.CreateTempSubdirectory("voice-issuance-");
        var old = new Dictionary<string, string?>();
        var values = new Dictionary<string, string>
        {
            ["OPENAI_API_KEY"]="fixture", ["ELEVENLABS_API_KEY"]="fixture", ["VOICEBRAIN_SECRET"]="fixture",
            ["VOICEBRAIN_AGENT_ID"]="fixture", ["VOICEBRAIN_STATE_DIR"]=dir.FullName,
            ["VOICEBRAIN_USAGE_DIR"]=Path.Combine(dir.FullName,"usage"), ["VOICEBRAIN_TARIFFS"]="",
            ["VOICEBRAIN_VOICES"]="", ["VOICEBRAIN_TICKET_KEY"]=""
        };
        try
        {
            foreach (var pair in values) { old[pair.Key]=Environment.GetEnvironmentVariable(pair.Key); Environment.SetEnvironmentVariable(pair.Key,pair.Value); }
            var registry = new AssetRegistryStore(Path.Combine(dir.FullName, "assets"));
            using var worlds = new WorldSupervisor(12345, GameMode.Feud, Path.Combine(dir.FullName,"world.sav"),
                new AssetGarmentCatalog(registry, AccountHttpTests.RepoFile("SimData/simdata.json")), false, false,
                new LlmHostOptions(), null, CancellationToken.None, startPaused:true);
            var accounts = new AccountStore(Path.Combine(dir.FullName,"accounts.json"), "fixture");
            var owner = accounts.CreateSession(new("owner", "password")); worlds.Assignments!.AdminAssign(owner.PlayerId,1);
            using var handler = new Provider(outcome);
            using var provider = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            await using var app = AccountHttpTests.BuildApp();
            VoiceEndpoints.Map(app, worlds, new ControlLeases(45), new AgentSessionRegistry(),
                new PlayerHttpIdentity(s=>s=="fixture",accounts), NullLogger.Instance, accounts, provider);
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",owner.SessionToken);
                var elapsed = Stopwatch.StartNew();
                var response = await client.PostAsync("/api/voice/v1/session",
                    new StringContent("{\"npcId\":1}",System.Text.Encoding.UTF8,"application/json"));
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(handler.Calls, Is.EqualTo(1));
                if (outcome == "timeout") Assert.That(elapsed.Elapsed.TotalSeconds, Is.InRange(14, 22),
                    "The endpoint must cancel a hung provider before its default 60-second HttpClient deadline.");
                var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                if (status != 200) Assert.That(body.GetProperty("error").GetString(),
                    Is.EqualTo(status == 504 ? "voice_provider_timeout" : "voice_provider_unavailable"));
                Assert.That(JsonlUsageLedger.ReadAll(values["VOICEBRAIN_USAGE_DIR"]).Count,
                    Is.EqualTo(status == 200 ? 1 : 0));
                Assert.That(accounts.OpeningCreditUsd(owner.PlayerId), Is.EqualTo(5m));
            }
            finally { await app.StopAsync(); }
        }
        finally { foreach (var pair in old) Environment.SetEnvironmentVariable(pair.Key,pair.Value); dir.Delete(true); }
    }
}
