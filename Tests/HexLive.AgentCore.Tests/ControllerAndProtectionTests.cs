using System.Security.Cryptography;
using System.Text;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class ControllerAndProtectionTests
{
    [Test]
    public void AuthenticatedEncryptionRejectsTamperingAndWrongKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var encrypted = ProtectedPayload.Encrypt("private-memory"u8, key);
        Assert.That(Encoding.UTF8.GetString(ProtectedPayload.Decrypt(encrypted, key)), Is.EqualTo("private-memory"));
        Assert.Catch<CryptographicException>(() => ProtectedPayload.Decrypt(encrypted, RandomNumberGenerator.GetBytes(32)));
        encrypted[^1] ^= 1;
        Assert.Catch<CryptographicException>(() => ProtectedPayload.Decrypt(encrypted, key));
    }
    [Test]
    public void PortableExportRequiresCorrectPassword()
    {
        var encrypted = ProtectedPayload.Export("memory"u8, "long-test-password");
        Assert.That(Encoding.UTF8.GetString(ProtectedPayload.Import(encrypted, "long-test-password")), Is.EqualTo("memory"));
        Assert.Catch<CryptographicException>(() => ProtectedPayload.Import(encrypted, "wrong-password"));
    }
    [Test]
    public async Task StartingIsExplicitAndStopDetachesBeforeReleasingWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("studio-controller-");
        try
        {
            var session = new Session();
            var connects = 0;
            await using var controller = new AgentController((_, _) => { connects++; return Task.FromResult<IAgentSession>(session); });
            Assert.That(controller.State, Is.EqualTo(AgentRunState.Stopped));
            Assert.That(connects, Is.Zero);
            var profile = new AgentProfile(Guid.NewGuid(), "Agent", root.FullName, Guid.NewGuid(), "world", 1,
                new(ModelProviderKind.DeepSeek, "integration", "model"));
            await controller.StartAsync(profile);
            await using var second = new AgentController((_, _) => Task.FromResult<IAgentSession>(new Session()));
            Assert.ThrowsAsync<IOException>(() => second.StartAsync(profile));
            await controller.StopAsync();
            Assert.That(session.Detached, Is.True);
            Assert.That(controller.State, Is.EqualTo(AgentRunState.Stopped));
            await second.StartAsync(profile);
            await second.StopAsync();
        }
        finally { root.Delete(true); }
    }
    private sealed class Session : IAgentSession
    {
        public bool Detached;
        public Task RunAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public Task DetachAsync(CancellationToken token) { Detached = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
