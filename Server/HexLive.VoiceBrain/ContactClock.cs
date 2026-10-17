using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.VoiceBrain;

/// <summary>What one call knows about the pause before it: fixed once, at the session's first look.</summary>
public sealed record SessionContact(bool First, long? PauseTicks, long? SinceTick, int DayLengthTicks);

/// <summary>
/// §171.26: when this character last heard this voice, in the world's own ticks.
/// The pause is captured once per session, so every turn of one call sees the
/// same «тебя не было три дня» even though each turn moves the contact forward.
/// </summary>
public sealed class ContactClock(string directory)
{
    private readonly object _gate = new();

    private sealed record Saved(string WorldId, long Tick, int DayLengthTicks, DateTimeOffset AtUtc,
        string SessionId, bool SessionFirst, long? SessionPauseTicks, long? SessionSinceTick);

    public SessionContact Begin(SessionTicket ticket, long tick, int dayLengthTicks)
    {
        lock (_gate)
        {
            var saved = Load(ticket);
            if (saved != null && saved.SessionId == ticket.SessionId)
                return new(saved.SessionFirst, saved.SessionPauseTicks, saved.SessionSinceTick, saved.DayLengthTicks);
            // A different world, a rolled-back tick or no clock: unknown, never a UTC guess.
            var known = saved != null && saved.WorldId == ticket.WorldId && tick >= saved.Tick && dayLengthTicks > 0;
            var first = saved == null;
            var contact = new SessionContact(first, known ? tick - saved!.Tick : null, known ? saved!.Tick : null, dayLengthTicks);
            Save(ticket, new Saved(ticket.WorldId, saved?.Tick ?? tick, dayLengthTicks, saved?.AtUtc ?? DateTimeOffset.UtcNow,
                ticket.SessionId, contact.First, contact.PauseTicks, contact.SinceTick));
            return contact;
        }
    }

    /// <summary>The voice was heard or answered: the next session measures its pause from here.</summary>
    public void Touch(SessionTicket ticket, long tick, int dayLengthTicks)
    {
        lock (_gate)
        {
            var saved = Load(ticket);
            if (saved != null && saved.WorldId == ticket.WorldId && tick < saved.Tick) return;
            Save(ticket, saved == null
                ? new Saved(ticket.WorldId, tick, dayLengthTicks, DateTimeOffset.UtcNow, ticket.SessionId, true, null, null)
                : saved with { WorldId = ticket.WorldId, Tick = tick, DayLengthTicks = dayLengthTicks, AtUtc = DateTimeOffset.UtcNow });
        }
    }

    private Saved? Load(SessionTicket ticket)
    {
        var path = PathFor(ticket);
        return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) : null;
    }

    private void Save(SessionTicket ticket, Saved state)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(ticket);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
        File.Move(path + ".tmp", path, true);
    }

    private string PathFor(SessionTicket ticket) => Path.Combine(directory, "contact-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(ticket.CharacterId + "\n" + ticket.OwnerUserId)))[..32] + ".json");
}
