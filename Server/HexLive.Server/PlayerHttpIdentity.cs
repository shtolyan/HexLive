using System;
using Microsoft.AspNetCore.Http;

namespace HexLive.Server;

/// <summary>§171.8: a signed-in player account session (Firebase, §171.9) mapped to a stable playerId.</summary>
public interface IPlayerSessionResolver
{
    bool TryResolve(string bearer, out string playerId);
}

/// <summary>
/// §171.8: who is calling a player HTTP API (voice session, character creation).
/// The same two proofs /watch accepts (§145.3): an account session, or — until
/// accounts ship everywhere — the shared player token plus the client's own id.
/// One seam, so every player endpoint changes identity rules in one place.
/// </summary>
public sealed class PlayerHttpIdentity(Func<string, bool>? sharedTokenMatches, IPlayerSessionResolver? accounts = null)
{
    private const string BearerPrefix = "Bearer ";

    public bool TryResolve(HttpContext context, out string playerId)
    {
        playerId = "";
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.Ordinal)) return false;
        var bearer = authorization[BearerPrefix.Length..].Trim();
        if (bearer.Length == 0) return false;
        // An account session names the player itself; a client-supplied id is never trusted over it.
        if (accounts != null && accounts.TryResolve(bearer, out var accountPlayer))
        {
            playerId = accountPlayer;
            return true;
        }
        if (sharedTokenMatches == null || !sharedTokenMatches(bearer)) return false;
        return PlayerCharacterAssignments.TryNormalizePlayerId(
            context.Request.Headers["X-HexLive-Client-Id"].ToString(), out playerId);
    }
}
