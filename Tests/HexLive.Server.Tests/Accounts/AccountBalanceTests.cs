using System;
using System.IO;
using HexLive.AgentCore.Studio;
using HexLive.Server.Accounts;
using NUnit.Framework;
namespace HexLive.Server.Tests.Accounts;
public sealed class AccountBalanceTests
{
 [Test] public void NewAccountGetsFiveOnceAcrossLoginProviderAndRestart()
 {
  var dir=Directory.CreateTempSubdirectory("account-credit-");
  try {
   var path=Path.Combine(dir.FullName,"accounts.json");var store=new AccountStore(path,"fixture");
   var first=store.CreateSession(new("uid","password"));
   Assert.That(store.OpeningCreditUsd(first.PlayerId),Is.EqualTo(5m));
   store=new AccountStore(path,"fixture");var second=store.CreateSession(new("uid","google.com"));
   Assert.That(second.PlayerId,Is.EqualTo(first.PlayerId));
   Assert.That(store.OpeningCreditUsd(second.PlayerId),Is.EqualTo(5m));
   Assert.That(store.OpeningCreditUsd("foreign"),Is.Zero);
   // Old schema accounts never receive a grant simply by logging in again.
   var text=File.ReadAllText(path).Replace(",\n      \"openingCreditUsd\": 5","");
   using var doc=System.Text.Json.JsonDocument.Parse(text);
   var node=System.Text.Json.Nodes.JsonNode.Parse(text)!;
   node["accounts"]![0]!.AsObject().Remove("openingCreditUsd");
   File.WriteAllText(path,node.ToJsonString());
   store=new AccountStore(path,"fixture");store.CreateSession(new("uid","password"));
   Assert.That(store.OpeningCreditUsd(first.PlayerId),Is.Zero);
  } finally {dir.Delete(true);}
 }
 [Test] public void SpendingIsDollarForDollarLatestRevisionOnlyAndOwnerScoped()
 {
  var event1=new UsageEvent{EventId="llm",OwnerUserId="ws:owner",Cost=.123456m,Currency="USD",CostSource=UsageCostSource.Estimate,InputTokens=100,OutputTokens=20};
  var corrected=event1 with {Revision=1,Cost=.12m,CostSource=UsageCostSource.Reconciled};
  var voice=new UsageEvent{EventId="voice",OwnerUserId="ws:owner",Cost=.25m,Currency="USD",CostSource=UsageCostSource.Reconciled,AudioSeconds=60};
  var pending=new UsageEvent{EventId="unknown",OwnerUserId="ws:owner",ProviderCredits=99999};
  var result=UsageBalance.Calculate(5m,"ws:owner",[event1,event1,voice,pending,corrected,voice with {EventId="foreign",OwnerUserId="ws:foreign",Cost=90m}]);
  Assert.Multiple(()=>{Assert.That(result.BalanceUsd,Is.EqualTo(4.63m));Assert.That(result.EstimatedUsd,Is.Zero);Assert.That(result.PendingRequests,Is.EqualTo(1));Assert.That(result.AudioSeconds,Is.EqualTo(60));Assert.That(result.InputTokens,Is.EqualTo(100));});
 }
 [Test] public void LedgerRestartAndReconciliationDoNotDuplicateDebits()
 {
  var dir=Directory.CreateTempSubdirectory("credit-usage-");
  try {
   var first=new UsageEvent{EventId="invoice",AtUtc=DateTimeOffset.UtcNow,OwnerUserId="ws:owner",Cost=.02m,Currency="USD",CostSource=UsageCostSource.Estimate};
   new JsonlUsageLedger(dir.FullName).Append(first);
   new JsonlUsageLedger(dir.FullName).Append(first with {Revision=1,Cost=.03m,CostSource=UsageCostSource.Reconciled});
   Assert.That(UsageBalance.Calculate(5m,"ws:owner",JsonlUsageLedger.ReadAll(dir.FullName)).BalanceUsd,Is.EqualTo(4.97m));
  } finally {dir.Delete(true);}
 }
}
