using System.Net;
using HexLive.AgentCore.Studio;
using HexLive.VoiceBrain;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;
public sealed class VoiceReconciliationTests
{
    private sealed class Provider : HttpMessageHandler
    {
        public int Calls;
        public bool Fail = true, Foreign, Ambiguous, Missing, Malformed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/conversations"))
            {
                Assert.That(request.RequestUri.Query, Does.Contain("user_id=session-1").And.Contain("agent_id=fixture-agent"));
                body = Malformed ? "[]" : Missing ? "{\"conversations\":[],\"has_more\":false}" :
                    "{\"conversations\":[{\"agent_id\":\"fixture-agent\",\"conversation_id\":\"conversation-1\"}],\"has_more\":" + (Ambiguous ? "true" : "false") + "}";
            }
            else body = "{\"status\":\"done\",\"user_id\":\"" + (Foreign ? "foreign-session" : "session-1") +
                "\",\"metadata\":{\"call_duration_secs\":79,\"cost\":584,\"cost_fiat\":0.10523577872020277}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private static VoiceBrainService Service(string directory, Provider handler) => new(new BrainOptions
    {
        OpenAiKey="fixture", ElevenLabsKey="fixture", BrainSecret="fixture", TicketKey=new byte[32],
        AgentId="fixture-agent", UsageDirectory=directory, StateDirectory=directory
    }, _=>null!, NullLogger.Instance, new HttpClient(handler));
    private static SessionTicket Ticket => new("session-1","ws:owner","w:7","w",7,DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());

    [Test]
    public async Task LostBrowserReceiptRetriesAfterRestartAndUpdatesSameBalanceSnapshotWithoutDoubleCharge()
    {
        var dir = Directory.CreateTempSubdirectory("voice-reconcile-");
        try
        {
            var handler = new Provider(); var voice = Service(dir.FullName,handler); voice.RecordSessionStarted(Ticket);
            var ledger = new JsonlUsageLedger(dir.FullName);
            ledger.Append(new UsageEvent { EventId="llm", OwnerUserId="ws:owner", Cost=.0033712m, Currency="USD", CostSource=UsageCostSource.Estimate });
            Assert.That(await voice.ReconcilePendingSessionsAsync(default), Is.Zero);
            Assert.That(voice.Balance("ws:owner",5m).SpentUsd, Is.EqualTo(.0033712m));
            handler.Fail=false; voice=Service(dir.FullName,handler);
            Assert.That(await voice.ReconcilePendingSessionsAsync(default), Is.EqualTo(1));
            await voice.RecordSessionEndAsync("conversation-1",Ticket,default);
            var calls=handler.Calls;
            Assert.That(await voice.ReconcilePendingSessionsAsync(default), Is.Zero);
            Assert.That(handler.Calls, Is.EqualTo(calls));
            var balance=voice.Balance("ws:owner",5m);
            Assert.Multiple(()=>
            {
                Assert.That(balance.SpentUsd, Is.EqualTo(.10860697872020277m));
                Assert.That(balance.BalanceUsd, Is.EqualTo(4.89139302127979723m));
                Assert.That(balance.OpeningUsd, Is.EqualTo(balance.BalanceUsd+balance.SpentUsd));
                Assert.That(balance.EstimatedUsd, Is.EqualTo(.0033712m));
                Assert.That(balance.PendingRequests, Is.Zero);
                Assert.That(JsonlUsageLedger.ReadAll(dir.FullName).Count, Is.EqualTo(2));
            });
        }
        finally { dir.Delete(true); }
    }

    [TestCase("foreign")]
    [TestCase("ambiguous")]
    [TestCase("missing")]
    [TestCase("malformed")]
    public async Task UnverifiedOrAbsentProviderConversationNeverChargesCredit(string situation)
    {
        var dir=Directory.CreateTempSubdirectory("voice-reconcile-reject-");
        try
        {
            var handler=new Provider { Fail=false, Foreign=situation=="foreign", Ambiguous=situation=="ambiguous", Missing=situation=="missing", Malformed=situation=="malformed" };
            var voice=Service(dir.FullName,handler); voice.RecordSessionStarted(Ticket);
            Assert.That(await voice.ReconcilePendingSessionsAsync(default), Is.Zero);
            var balance=voice.Balance("ws:owner",5m);
            Assert.Multiple(()=> { Assert.That(balance.BalanceUsd, Is.EqualTo(5m)); Assert.That(balance.PendingRequests, Is.EqualTo(1)); });
            handler.Foreign=handler.Ambiguous=handler.Missing=handler.Malformed=false;
            Assert.That(await voice.ReconcilePendingSessionsAsync(default), Is.EqualTo(1), "A deferred receipt remains recoverable on the next sweep.");
        }
        finally { dir.Delete(true); }
    }

    private sealed class EmptyProvider : HttpMessageHandler
    {
        public readonly List<string> Sessions = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sessions.Add(Uri.UnescapeDataString(request.RequestUri!.Query.Split('&').Single(p => p.StartsWith("user_id="))[8..]));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"conversations\":[],\"has_more\":false}") });
        }
    }

    [Test]
    public async Task OldUnusedTokensCannotStarveLaterReceiptsAndHostShutdownCancelsTheLoop()
    {
        var dir=Directory.CreateTempSubdirectory("voice-reconcile-fairness-");
        try
        {
            var handler=new EmptyProvider();
            var voice=new VoiceBrainService(new BrainOptions { OpenAiKey="fixture", ElevenLabsKey="fixture", BrainSecret="fixture",
                TicketKey=new byte[32], AgentId="fixture-agent", UsageDirectory=dir.FullName, StateDirectory=dir.FullName },
                _=>null!,NullLogger.Instance,new HttpClient(handler));
            for(var i=0;i<10;i++)voice.RecordSessionStarted(Ticket with {SessionId="queued-"+i});
            for(var i=0;i<3;i++)Assert.That(await voice.ReconcilePendingSessionsAsync(default),Is.Zero);
            Assert.That(handler.Sessions.Count,Is.EqualTo(12));
            Assert.That(handler.Sessions.Distinct().Count(),Is.EqualTo(10));
            using var cancel=new CancellationTokenSource();
            var loop=voice.ReconcilePendingContinuouslyAsync(cancel.Token);
            cancel.Cancel(); await loop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(voice.Balance("ws:owner",5m).BalanceUsd,Is.EqualTo(5m));
        }
        finally {dir.Delete(true);}
    }
}
