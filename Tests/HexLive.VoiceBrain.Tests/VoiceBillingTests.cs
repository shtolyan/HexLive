using System.Net;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.VoiceBrain;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
namespace HexLive.VoiceBrain.Tests;
public sealed class VoiceBillingTests
{
 [Test] public async Task FinalProviderUsdIsPersistedOnceAndForeignSessionCannotClaimIt()
 {
  var dir=Directory.CreateTempSubdirectory("voice-invoice-");
  try {
   var handler=new FactsHandler();
   var options=new BrainOptions{OpenAiKey="fixture",ElevenLabsKey="fixture",BrainSecret="fixture",TicketKey=new byte[32],UsageDirectory=dir.FullName,StateDirectory=dir.FullName};
   var voice=new VoiceBrainService(options,_=>null!,NullLogger.Instance,new HttpClient(handler));
   var ticket=new SessionTicket("session-1","ws:owner","w:7","w",7,DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
   voice.RecordSessionStarted(ticket);
   Assert.That(voice.Balance("ws:owner",5m).PendingRequests,Is.EqualTo(1),"a lost browser leaves an unresolved bill, never a zero-cost call");
   await voice.RecordSessionEndAsync("conversation-1",ticket,default);
   await voice.RecordSessionEndAsync("conversation-1",ticket,default);
   var events=JsonlUsageLedger.ReadAll(dir.FullName);
   Assert.Multiple(()=>{Assert.That(handler.Calls,Is.EqualTo(1));Assert.That(events.Count,Is.EqualTo(1));Assert.That(events[0].Cost,Is.EqualTo(.123456m));Assert.That(events[0].ProviderCredits,Is.EqualTo(999m));Assert.That(events[0].Currency,Is.EqualTo("USD"));Assert.That(voice.Balance("ws:owner",5m).BalanceUsd,Is.EqualTo(4.876544m));});
   Assert.ThrowsAsync<InvalidDataException>(()=>voice.RecordSessionEndAsync("conversation-1",ticket with{OwnerUserId="ws:foreign"},default));
   Assert.ThrowsAsync<InvalidDataException>(()=>voice.RecordSessionEndAsync("conversation-2",ticket with{SessionId="foreign-session"},default));
   Assert.ThrowsAsync<InvalidDataException>(()=>voice.RecordSessionEndAsync("different-conversation",ticket,default));
  }finally{dir.Delete(true);}
 }
 private sealed class FactsHandler:HttpMessageHandler
 {
  public int Calls;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"status":"done","user_id":"session-1","metadata":{"call_duration_secs":20,"cost":999,"cost_fiat":0.123456}}""")});}
 }
}
