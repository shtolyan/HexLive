using System;
using System.Collections.Generic;
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
using HexLive.Server.Voice;
using HexLive.Server.Tests.Accounts;
using HexLive.Simulation.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
namespace HexLive.Server.Tests.Voice;
[NonParallelizable]
public sealed class AccountBalanceApiTests
{
 [Test] public async Task OnlyAccountOwnerSeesBalanceAndDepletedCreditStopsBeforeProviderCall()
 {
  var dir=Directory.CreateTempSubdirectory("balance-api-");var old=new Dictionary<string,string?>();
  var values=new Dictionary<string,string>{["OPENAI_API_KEY"]="fixture",["ELEVENLABS_API_KEY"]="fixture",["VOICEBRAIN_SECRET"]="fixture",["VOICEBRAIN_AGENT_ID"]="fixture",["VOICEBRAIN_STATE_DIR"]=dir.FullName,["VOICEBRAIN_USAGE_DIR"]=Path.Combine(dir.FullName,"usage"),["VOICEBRAIN_TARIFFS"]="",["VOICEBRAIN_VOICES"]="",["VOICEBRAIN_TICKET_KEY"]=""};
  try {
   foreach(var p in values){old[p.Key]=Environment.GetEnvironmentVariable(p.Key);Environment.SetEnvironmentVariable(p.Key,p.Value);}
   var registry=new AssetRegistryStore(Path.Combine(dir.FullName,"assets"));
   using var worlds=new WorldSupervisor(12345,GameMode.Feud,Path.Combine(dir.FullName,"world.sav"),new AssetGarmentCatalog(registry,AccountHttpTests.RepoFile("SimData/simdata.json")),false,false,new LlmHostOptions(),null,CancellationToken.None,startPaused:true);
   var accounts=new AccountStore(Path.Combine(dir.FullName,"accounts.json"),"fixture");
   var owner=accounts.CreateSession(new("owner","password"));var other=accounts.CreateSession(new("other","password"));worlds.Assignments!.AdminAssign(owner.PlayerId,1);
   var ledger=new JsonlUsageLedger(values["VOICEBRAIN_USAGE_DIR"]);
   ledger.Append(new UsageEvent{AtUtc=DateTimeOffset.UtcNow,OwnerUserId="ws:"+owner.PlayerId,Cost=1.25m,Currency="USD",CostSource=UsageCostSource.Reconciled});
   await using var app=AccountHttpTests.BuildApp();
   VoiceEndpoints.Map(app,worlds,new ControlLeases(45),new AgentSessionRegistry(),new PlayerHttpIdentity(s=>s=="fixture",accounts),NullLogger.Instance,accounts);
   await app.StartAsync();
   try {
    using var client=new HttpClient{BaseAddress=new Uri(app.Urls.First())};const string route="/api/accounts/v1/balance";
    Assert.That((await client.GetAsync(route)).StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
    client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",owner.SessionToken);
    var result=await client.GetAsync(route+"?playerId="+other.PlayerId);
    var json=System.Text.Json.JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement;
    Assert.That(json.GetProperty("balanceUsd").GetDecimal(),Is.EqualTo(3.75m));
    Assert.That(result.Headers.CacheControl!.NoStore,Is.True);
    client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",other.SessionToken);
    json=System.Text.Json.JsonDocument.Parse(await (await client.GetAsync(route)).Content.ReadAsStringAsync()).RootElement;
    Assert.That(json.GetProperty("balanceUsd").GetDecimal(),Is.EqualTo(5m));
    ledger.Append(new UsageEvent{AtUtc=DateTimeOffset.UtcNow,OwnerUserId="ws:"+owner.PlayerId,Cost=4m,Currency="USD",CostSource=UsageCostSource.Reconciled});
    client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",owner.SessionToken);
    var denied=await client.PostAsync("/api/voice/v1/session",new StringContent("{\"npcId\":1}",System.Text.Encoding.UTF8,"application/json"));
    Assert.That((int)denied.StatusCode,Is.EqualTo(402),"fixture provider key must never be called on depleted credit");
    accounts.Revoke(owner.SessionToken);
    Assert.That((await client.GetAsync(route)).StatusCode,Is.EqualTo(HttpStatusCode.Unauthorized));
   }finally{await app.StopAsync();}
  }finally{foreach(var p in old)Environment.SetEnvironmentVariable(p.Key,p.Value);dir.Delete(true);}
 }
}
