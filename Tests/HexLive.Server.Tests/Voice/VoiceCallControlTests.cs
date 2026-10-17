using System;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Voice;
using HexLive.VoiceBrain;
using NUnit.Framework;

namespace HexLive.Server.Tests.Voice;
public sealed class VoiceCallControlTests
{
    private static SessionTicket Ticket(string id="first",string owner="ws:owner",int npc=1) => new(id,owner,"world:"+npc,"world",npc,long.MaxValue);

    [Test]
    public void ModeSwitchCancelsLifetimeRejectsLateStartAndOldHangupCannotStopNewCall()
    {
        var gate=new VoiceCallControl(_=>{}); var first=Ticket();
        var epoch=gate.Select(first.OwnerUserId,first.WorldId,1,true,()=>{});
        Assert.That(gate.Begin(first,epoch),Is.True); var token=gate.Lifetime(first)!.Value;
        gate.Select(first.OwnerUserId,first.WorldId,1,false,()=>{});
        Assert.That(token.IsCancellationRequested,Is.True);
        Assert.That(gate.Begin(first,epoch),Is.False);
        Assert.Throws<WorldToolRejectedException>(()=>gate.Call(first,()=>Task.FromResult(true)));
        var second=Ticket("second"); epoch=gate.Select(second.OwnerUserId,second.WorldId,1,true,()=>{});
        Assert.That(gate.Begin(second,epoch),Is.True);
        Assert.That(gate.Stop(first),Is.False); Assert.That(gate.Lifetime(second),Is.Not.Null);
        Assert.That(gate.Stop(second),Is.True); Assert.That(gate.Stop(second),Is.False);
    }

    [Test]
    public void LateControlIntentCannotUndoMoreRecentManualSelection()
    {
        var gate=new VoiceCallControl(_=>{});var ticket=Ticket();var mode="";
        gate.Select(ticket.OwnerUserId,ticket.WorldId,1,false,()=>mode="manual","client",2);
        Assert.Throws<WorldToolRejectedException>(()=>gate.Select(ticket.OwnerUserId,ticket.WorldId,1,true,()=>mode="call","client",1));
        Assert.That(mode,Is.EqualTo("manual"));
        // Page reloads must not permanently exhaust the control client cache.
        for(var i=0;i<100;i++)gate.Select(ticket.OwnerUserId,ticket.WorldId,1,false,()=>{},"client"+i,1);
    }

    [Test]
    public void TicketOwnershipAndExpiryRemainScoped()
    {
        var now=DateTimeOffset.UtcNow; var gate=new VoiceCallControl(_=>{},()=>now);
        var ticket=Ticket() with{ExpiresUnix=now.AddSeconds(1).ToUnixTimeSeconds()};
        var epoch=gate.Select(ticket.OwnerUserId,ticket.WorldId,1,true,()=>{});gate.Begin(ticket,epoch);
        Assert.That(gate.Lifetime(ticket with{OwnerUserId="ws:foreign"}),Is.Null);
        Assert.That(gate.Lifetime(ticket with{NpcId=2}),Is.Null);
        var token=gate.Lifetime(ticket)!.Value;now=now.AddSeconds(2);
        Assert.That(gate.Lifetime(ticket),Is.Null);Assert.That(token.IsCancellationRequested,Is.True);
    }

    [Test]
    public async Task StopSerializesWithActualInProcessWorldOperation()
    {
        var gate=new VoiceCallControl(_=>{});var ticket=Ticket();
        gate.Begin(ticket,gate.Select(ticket.OwnerUserId,ticket.WorldId,1,true,()=>{}));
        using var entered=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();
        var count=0;
        var action=Task.Run(async()=>await gate.Call(ticket,()=>{entered.Set();resume.Wait();count++;return Task.FromResult(true);}));
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)),Is.True);
        var stopping=Task.Run(()=>gate.Stop(ticket));
        try { Assert.That(stopping.Wait(50),Is.False,"Mode change cannot interleave with an admitted world operation"); }
        finally { resume.Set(); }
        await action;await stopping;
        Assert.Throws<WorldToolRejectedException>(()=>gate.Call(ticket,()=>{count++;return Task.FromResult(true);}));
        Assert.That(count,Is.EqualTo(1));
    }
}
