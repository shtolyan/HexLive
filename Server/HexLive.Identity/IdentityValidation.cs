using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace HexLive.Identity;

public static class IdentityValidation
{
    public const int AccountRequestsPerMinute = 1200;
    public const int AnonymousRequestsPerMinute = 120;
    private const string Policy = "identity-validation";

    private static KeyStore.Account? Authenticate(HttpContext context, KeyStore keys)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? keys.Authenticate(header[7..]) : null;
    }

    public static void Configure(RateLimiterOptions options, KeyStore keys)
    {
        options.AddPolicy(Policy, context =>
        {
            // Partition only by authenticated account, never attacker-supplied keys or IDs.
            // Invalid/revoked keys share a bounded bucket without consuming a player's budget.
            var account = Authenticate(context, keys);
            return RateLimitPartition.GetFixedWindowLimiter(account?.Id ?? "anonymous", _ => new()
            {
                PermitLimit = account == null ? AnonymousRequestsPerMinute : AccountRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            });
        });
    }

    public static void Map(WebApplication app, KeyStore keys)
    {
        app.MapPost("/api/identity/v1/validate", (HttpContext context) =>
        {
            // Re-read after admission: rate limiting never caches rights or extends a revoked key.
            var identity = Authenticate(context, keys);
            return identity == null ? Results.Unauthorized() : Results.Ok(new
            {
                accountId = identity.Id, name = identity.Name, permissions = identity.Permissions,
                revision = identity.Revision, subjectType = identity.SubjectType
            });
        }).RequireRateLimiting(Policy);
    }
}
