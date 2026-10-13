using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HexLive.VoiceBrain;

namespace HexLive.Server.Voice;

/// <summary>§171.23: per-body generation fence and lifetime for telephone control.
/// In-process world calls execute synchronously while holding the same gate as
/// mode changes, so a cancelled turn cannot reacquire the character afterwards.</summary>
public sealed class VoiceCallControl(Action<SessionTicket> release, Func<DateTimeOffset>? now = null)
{
    private sealed class Slot
    {
        public readonly object Gate = new();
        public long Generation;
        public bool CallPrepared;
        public SessionTicket? Ticket;
        public CancellationTokenSource? Lifetime;
        public readonly Dictionary<string, long> ClientIntents = new();
    }
    private readonly ConcurrentDictionary<(string Owner, string World, int Npc), Slot> _slots = new();
    private DateTimeOffset Now => now?.Invoke() ?? DateTimeOffset.UtcNow;
    private static (string, string, int) Key(SessionTicket t) => (t.OwnerUserId, t.WorldId, t.NpcId);

    public long Select(string owner, string world, int npc, bool call, Action apply, string? client = null, long sequence = 0)
    {
        var slot = _slots.GetOrAdd((owner, world, npc), _ => new Slot());
        lock (slot.Gate)
        {
            if (client != null)
            {
                if (sequence <= slot.ClientIntents.GetValueOrDefault(client)) throw new WorldToolRejectedException("StaleVoiceControl");
                if (!slot.ClientIntents.ContainsKey(client) && slot.ClientIntents.Count >= 32)
                    slot.ClientIntents.Remove(slot.ClientIntents.Keys.First());
                // Refresh insertion order: keep recent live browser instances,
                // without permanently locking out the actor after page reloads.
                slot.ClientIntents.Remove(client);
                slot.ClientIntents[client] = sequence;
            }
            var released = StopLocked(slot);
            // Also cleans a voice-owned attachment left by a previous process.
            if (!released) release(new SessionTicket("", owner, world+":"+npc, world, npc, 0));
            apply();
            slot.CallPrepared = call;
            return slot.Generation;
        }
    }

    public bool Begin(SessionTicket ticket, long generation)
    {
        var slot = _slots.GetOrAdd(Key(ticket), _ => new Slot());
        lock (slot.Gate)
        {
            if (!slot.CallPrepared || slot.Generation != generation || slot.Ticket != null) return false;
            slot.Ticket = ticket;
            slot.Lifetime = new CancellationTokenSource();
            return true;
        }
    }

    public CancellationToken? Lifetime(SessionTicket ticket)
    {
        if (!_slots.TryGetValue(Key(ticket), out var slot)) return null;
        lock (slot.Gate)
        {
            if (slot.Ticket?.SessionId != ticket.SessionId) return null;
            if (ticket.ExpiresUnix <= Now.ToUnixTimeSeconds()) { StopLocked(slot); return null; }
            return slot.Lifetime!.Token;
        }
    }

    public bool Stop(SessionTicket ticket)
    {
        if (!_slots.TryGetValue(Key(ticket), out var slot)) return false;
        lock (slot.Gate)
        {
            // An old delayed hangup/billing receipt cannot stop a newer call.
            if (slot.Ticket?.SessionId != ticket.SessionId) return false;
            StopLocked(slot); return true;
        }
    }

    private bool StopLocked(Slot slot)
    {
        var old = slot.Ticket; var cancellation = slot.Lifetime;
        slot.Ticket = null; slot.Lifetime = null; slot.CallPrepared = false;
        slot.Generation++;
        cancellation?.Cancel();
        // Tokens may still be in use by a cancelled HTTP request. The source
        // is collected when that request disposes its linked registration.
        if (old != null) release(old);
        return old != null;
    }

    public Task<T> Call<T>(SessionTicket ticket, Func<Task<T>> action)
    {
        if (!_slots.TryGetValue(Key(ticket), out var slot)) throw new WorldToolRejectedException("VoiceCallEnded");
        lock (slot.Gate)
        {
            if (slot.Ticket?.SessionId != ticket.SessionId || ticket.ExpiresUnix <= Now.ToUnixTimeSeconds())
                throw new WorldToolRejectedException("VoiceCallEnded");
            return action(); // InProcessWorldGateway completes its world operation before returning.
        }
    }
}

internal sealed class ScopedVoiceWorldGateway(VoiceCallControl calls, SessionTicket ticket, IWorldGateway inner) : IWorldGateway
{
    public Task<System.Text.Json.JsonElement> CallAsync(string tool, object args, CancellationToken token) =>
        calls.Call(ticket, () => inner.CallAsync(tool, args, token));
    public Task RecordConversationAsync(int npc, string turn, CancellationToken token) =>
        calls.Call(ticket, async () => { await inner.RecordConversationAsync(npc, turn, token); return true; });
}
