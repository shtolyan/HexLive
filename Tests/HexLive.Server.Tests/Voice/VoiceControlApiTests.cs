using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.AgentCore.Studio;
using HexLive.Server.Accounts;
using HexLive.Server.Assets;
using HexLive.Server.Llm;
using HexLive.Server.Mcp;
using HexLive.Server.Tests.Accounts;
using HexLive.Server.Voice;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.Server.Tests.Voice;

[NonParallelizable]
public sealed class VoiceControlApiTests
{
    private sealed class Provider : HttpMessageHandler
    {
        public bool HangIssuance;
        public bool HangBrain;
        public int Issuances;
        public int BrainCalls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var issuance = request.RequestUri!.AbsolutePath.Contains("conversation/token", StringComparison.Ordinal);
            if (issuance) Issuances++; else BrainCalls++;
            if (issuance ? HangIssuance : HangBrain)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(issuance ? "{\"token\":\"fixture\"}" :
                "data: {\"choices\":[{\"delta\":{\"content\":\"Привет.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                Encoding.UTF8, issuance ? "application/json" : "text/event-stream") };
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Provider Provider = new();
        public readonly ControlLeases Leases = new(45);
        public readonly AgentSessionRegistry Agents = new();
        public readonly string ClientId = Guid.NewGuid().ToString("N");
        public readonly DirectoryInfo Dir = Directory.CreateTempSubdirectory("voice-control-api-");
        private readonly Dictionary<string,string?> _environment = new();
        private HttpClient _provider = null!;
        private WebApplication _app = null!;
        public WorldSupervisor Worlds = null!;
        public HttpClient Client = null!;
        public string Owner = "";

        public static async Task<Fixture> Start()
        {
            var f = new Fixture();
            try
            {
                var values = new Dictionary<string,string> {
                    ["OPENAI_API_KEY"]="fixture", ["ELEVENLABS_API_KEY"]="fixture", ["VOICEBRAIN_SECRET"]="fixture",
                    ["VOICEBRAIN_AGENT_ID"]="fixture", ["VOICEBRAIN_STATE_DIR"]=f.Dir.FullName,
                    ["VOICEBRAIN_USAGE_DIR"]=Path.Combine(f.Dir.FullName,"usage"), ["VOICEBRAIN_TARIFFS"]="",
                    ["VOICEBRAIN_VOICES"]="", ["VOICEBRAIN_TICKET_KEY"]="" };
                foreach(var pair in values) { f._environment[pair.Key]=Environment.GetEnvironmentVariable(pair.Key); Environment.SetEnvironmentVariable(pair.Key,pair.Value); }
                var registry = new AssetRegistryStore(Path.Combine(f.Dir.FullName,"assets"));
                f.Worlds = new(12345, GameMode.Feud, Path.Combine(f.Dir.FullName,"world.sav"),
                    new AssetGarmentCatalog(registry,AccountHttpTests.RepoFile("SimData/simdata.json")),false,false,
                    new LlmHostOptions(),null,CancellationToken.None,startPaused:true);
                var accounts = new AccountStore(Path.Combine(f.Dir.FullName,"accounts.json"),"fixture");
                var owner = accounts.CreateSession(new("owner","password"));
                f.Owner="ws:"+owner.PlayerId; f.Worlds.Assignments!.AdminAssign(owner.PlayerId,1);
                f._provider = new(f.Provider);
                f._app = AccountHttpTests.BuildApp();
                VoiceEndpoints.Map(f._app,f.Worlds,f.Leases,f.Agents,new PlayerHttpIdentity(null,accounts),NullLogger.Instance,accounts,f._provider);
                await f._app.StartAsync();
                f.Client = new(){BaseAddress=new Uri(f._app.Urls.First())};
                f.Client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",owner.SessionToken);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<HttpResponseMessage> Post(string path, object value, bool brain=false)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/voice/v1/"+path) {
                Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json") };
            if(brain) request.Headers.Authorization=new("Bearer","fixture");
            return await Client.SendAsync(request);
        }
        public Task<HttpResponseMessage> Mode(string mode,long seq) => Post("control",new {npcId=1,mode,clientInstance=ClientId,clientSequence=seq});
        public static async Task<JsonElement> Body(HttpResponseMessage response) => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        public async Task<long> Prepare(long seq)
        {
            using var response=await Mode("call",seq); Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.OK));
            return (await Body(response)).GetProperty("controlEpoch").GetInt64();
        }
        public async Task<string> StartCall(long epoch)
        {
            using var response=await Post("session",new {npcId=1,controlEpoch=epoch});
            Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.OK));
            return (await Body(response)).GetProperty("ticket").GetString()!;
        }
        public Task<HttpResponseMessage> Brain(string ticket) => Post("brain/chat/completions",
            new {elevenlabs_extra_body=new {ticket}, messages=new[]{new {role="user",content="Привет"}}},true);
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if(_app!=null) {await _app.StopAsync();await _app.DisposeAsync();}
            Worlds?.Dispose(); _provider?.Dispose(); Provider.Dispose();
            foreach(var pair in _environment) Environment.SetEnvironmentVariable(pair.Key,pair.Value);
            Dir.Delete(true);
        }
    }

    [Test]
    public async Task SwitchingToManualReleasesOnlyVoiceAgentAndRejectsLateTicketOrIntent()
    {
        await using var f=await Fixture.Start();
        var epoch=await f.Prepare(1); var ticket=await f.StartCall(epoch);
        var tools=new McpTools(()=>f.Worlds.Host,()=>f.Worlds.CaptureViewerSession().WorldGeneration,f.Leases,f.Agents);
        var gateway=new InProcessWorldGateway(tools,f.Owner,n=>n==1,()=>f.Worlds.Host);
        await gateway.CallAsync("acquire_control",new {npcId=1},default);
        Assert.That(f.Agents.HasAttachment(1),Is.True);
        using var manual=await f.Mode("manual",2); Assert.That(manual.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        Assert.That(f.Agents.HasAttachment(1),Is.False);
        Assert.That(f.Leases.HolderOf(1),Is.EqualTo(f.Owner));
        Assert.That(f.Worlds.Host.Read(w=>w.Entities.Npcs[new EntityId(1)].Mind.ManualControl),Is.True);
        using var lateBrain=await f.Brain(ticket); Assert.That(lateBrain.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        using var lateStart=await f.Post("session",new {npcId=1,controlEpoch=epoch}); Assert.That(lateStart.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        using var stale=await f.Mode("call",1); Assert.That(stale.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(f.Provider.Issuances,Is.EqualTo(1)); Assert.That(f.Provider.BrainCalls,Is.Zero);
        using var foreign=await f.Post("control",new {npcId=2,mode="manual",clientInstance=f.ClientId,clientSequence=3});
        Assert.That(foreign.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        using var ai=await f.Mode("ai",3); Assert.That(ai.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        // A Studio attachment is deliberately not owned by the phone mode.
        tools.Call("attach_agent",JsonSerializer.SerializeToElement(new {npcId=1,displayName="Studio",capabilities=new[]{"worldActions"},ttlSeconds=120}),"studio",out var error,n=>n==1);
        Assert.That(error,Is.False);
        using var refused=await f.Mode("manual",4); Assert.That(refused.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(f.Agents.HasAttachment(1),Is.True);
    }

    [Test]
    public async Task SelectingAiCancelsConnectingProviderAndCreatesNoSuccessfulReceipt()
    {
        await using var f=await Fixture.Start(); f.Provider.HangIssuance=true;
        var epoch=await f.Prepare(1);
        var pending=f.Post("session",new {npcId=1,controlEpoch=epoch});
        await f.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var ai=await f.Mode("ai",2); Assert.That(ai.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        await f.Provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var response=await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(JsonlUsageLedger.ReadAll(Path.Combine(f.Dir.FullName,"usage")),Is.Empty);
        Assert.That(f.Leases.HolderOf(1),Is.Empty);
    }

    [Test]
    public async Task OldHangupCannotCancelNewCallAndModeSwitchCancelsItsInFlightBrain()
    {
        await using var f=await Fixture.Start();
        var old=await f.StartCall(await f.Prepare(1));
        using var ai=await f.Mode("ai",2); Assert.That(ai.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        var current=await f.StartCall(await f.Prepare(3));
        using var oldCancel=await f.Post("session/cancel",new {ticket=old}); Assert.That(oldCancel.StatusCode,Is.EqualTo(HttpStatusCode.NoContent));
        using var dead=await f.Brain(old); Assert.That(dead.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        f.Provider.HangBrain=true;
        var brain=f.Brain(current); await f.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var manual=await f.Mode("manual",4); Assert.That(manual.StatusCode,Is.EqualTo(HttpStatusCode.OK));
        await f.Provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var finished=await brain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(await finished.Content.ReadAsStringAsync(),Does.Not.Contain("мысль сбилась"));
        using var late=await f.Brain(current); Assert.That(late.StatusCode,Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(f.Provider.BrainCalls,Is.EqualTo(1));
    }
}
