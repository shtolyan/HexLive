using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace HexLive.Server.Admin
{

/// <summary>
/// Who is signed in, and how hard it is to guess your way in.
/// <para>
/// Sessions live in memory only: a restart signs everyone out, which for a
/// single-operator server is a feature rather than a limitation. Tokens are 256
/// bits of cryptographic randomness — long enough that guessing is not a threat
/// model, so the interesting protection is the one below it.
/// </para>
/// <para>
/// <b>Login throttling matters more than password rules here.</b> One account
/// and an internet-facing port is exactly the shape a credential-stuffing script
/// likes, so failures back off per source address, quickly reaching a delay that
/// makes an online guessing attack pointless.
/// </para>
/// </summary>
public sealed class AdminSessions
{
    /// <summary>Signed out after this long without a request.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromHours(12);

    /// <summary>Free attempts before the delay starts biting.</summary>
    private const int FreeAttempts = 3;

    /// <summary>Cap: long enough to be useless to a script, short enough to be survivable after a typo.</summary>
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int failures, DateTime until)> _throttle = new(StringComparer.Ordinal);

    /// <summary>Reset tokens: one-shot, short-lived, and invalidated by use.</summary>
    private readonly Dictionary<string, DateTime> _resetTokens = new(StringComparer.Ordinal);

    private static readonly TimeSpan ResetWindow = TimeSpan.FromMinutes(30);

    public string CreateSession()
    {
        var token = NewToken();
        lock (_gate)
        {
            Sweep();
            _sessions[token] = DateTime.UtcNow;
        }

        return token;
    }

    public bool IsSignedIn(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var lastSeen))
            {
                return false;
            }

            if (DateTime.UtcNow - lastSeen > Idle)
            {
                _sessions.Remove(token);
                return false;
            }

            _sessions[token] = DateTime.UtcNow;
            return true;
        }
    }

    public void SignOut(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        lock (_gate)
        {
            _sessions.Remove(token);
        }
    }

    /// <summary>Ends every session — used after a password change, so an old browser cannot linger.</summary>
    public void SignOutEverywhere()
    {
        lock (_gate)
        {
            _sessions.Clear();
        }
    }

    /// <summary>Seconds the caller must wait before another attempt, or 0.</summary>
    public int LockoutSeconds(string source)
    {
        lock (_gate)
        {
            if (!_throttle.TryGetValue(source, out var entry))
            {
                return 0;
            }

            var remaining = entry.until - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
        }
    }

    public void RecordFailure(string source)
    {
        lock (_gate)
        {
            _throttle.TryGetValue(source, out var entry);
            var failures = entry.failures + 1;

            // Doubling from one second once the free attempts are used up.
            var penalty = failures <= FreeAttempts
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Min(MaxLockout.TotalSeconds, Math.Pow(2, failures - FreeAttempts)));

            _throttle[source] = (failures, DateTime.UtcNow + penalty);
        }
    }

    public void RecordSuccess(string source)
    {
        lock (_gate)
        {
            _throttle.Remove(source);
        }
    }

    public string CreateResetToken()
    {
        var token = NewToken();
        lock (_gate)
        {
            _resetTokens[token] = DateTime.UtcNow + ResetWindow;
        }

        return token;
    }

    /// <summary>Consumes the token: valid at most once, whatever happens next.</summary>
    public bool RedeemResetToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_resetTokens.TryGetValue(token, out var expires))
            {
                return false;
            }

            _resetTokens.Remove(token);
            return DateTime.UtcNow <= expires;
        }
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        var stale = new List<string>();
        foreach (var pair in _sessions)
        {
            if (now - pair.Value > Idle)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var token in stale)
        {
            _sessions.Remove(token);
        }
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

}
