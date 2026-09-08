using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class NativeSecretsSmokeTests
{
    [Test, Explicit("Writes and removes a temporary test-only entry in the OS credential store")]
    public async Task NativeCredentialRoundTrip()
    {
        var id = "smoke." + Guid.NewGuid().ToString("N");
        var secret = Guid.NewGuid().ToString("N");
        ISecretStore store = new OperatingSystemSecretStore();
        try
        {
            Assert.That(await store.ReadAsync(id, CancellationToken.None), Is.Null);
            await store.WriteAsync(id, secret, CancellationToken.None);
            Assert.That(await store.ReadAsync(id, CancellationToken.None) == secret, Is.True, "Credential round trip mismatch");
            await store.DeleteAsync(id, CancellationToken.None);
            Assert.That(await store.ReadAsync(id, CancellationToken.None), Is.Null);
        }
        finally { await store.DeleteAsync(id, CancellationToken.None); }
    }
}
