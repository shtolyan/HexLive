using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class SessionSecretStoreTests
{
    [Test]
    public async Task StartupReadsEachKeyOnceAndContinuesAfterDenial()
    {
        var native = new FakeStore();
        var store = new SessionSecretStore(native);
        Assert.That(await store.InitializeAsync(["model", "denied", "voice", "model", "missing"], default), Is.False);
        Assert.That(await store.ReadAsync("voice", default), Is.EqualTo("voice-value"));
        Assert.That(await store.ReadAsync("missing", default), Is.Null);
        Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("denied", default));
        Assert.That(native.Reads, Is.EqualTo(4));
    }

    [Test]
    public async Task ConcurrentConsumersShareOneOsRead()
    {
        var native = new FakeStore();
        var store = new SessionSecretStore(native);
        var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.ReadAsync("model", default)));
        Assert.That(native.Reads, Is.EqualTo(1));
        Assert.That(values, Has.All.EqualTo("model-value"));
    }

    [Test]
    public async Task ExplicitWriteRecoversDenialAndDeleteInvalidatesCachedValue()
    {
        var native = new FakeStore();
        var store = new SessionSecretStore(native);
        Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("denied", default));
        await store.WriteAsync("denied", "replacement", default);
        Assert.That(await store.ReadAsync("denied", default), Is.EqualTo("replacement"));
        await store.DeleteAsync("denied", default);
        Assert.That(await store.ReadAsync("denied", default), Is.Null);
        Assert.That(native.Reads, Is.EqualTo(1));
    }

    [Test]
    public async Task CancellationDoesNotBecomePermanentDenial()
    {
        var native = new FakeStore { CancelNext = true };
        var store = new SessionSecretStore(native);
        Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync("model", default));
        Assert.That(await store.ReadAsync("model", default), Is.EqualTo("model-value"));
        Assert.That(native.Reads, Is.EqualTo(2));
    }

    [Test]
    public async Task FailedWriteDoesNotReplaceKnownGoodValue()
    {
        var native = new FakeStore();
        var store = new SessionSecretStore(native);
        await store.ReadAsync("model", default);
        native.FailWrite = true;
        Assert.ThrowsAsync<IOException>(() => store.WriteAsync("model", "replacement", default));
        Assert.That(await store.ReadAsync("model", default), Is.EqualTo("model-value"));
    }

    private sealed class FakeStore : ISecretStore
    {
        public int Reads;
        public bool CancelNext, FailWrite;
        public async Task<string?> ReadAsync(string id, CancellationToken token)
        {
            Reads++;
            await Task.Yield();
            if (CancelNext) { CancelNext = false; throw new OperationCanceledException(); }
            if (id == "denied") throw new InvalidOperationException("Denied");
            return id == "missing" ? null : id + "-value";
        }
        public Task WriteAsync(string id, string value, CancellationToken token) =>
            FailWrite ? Task.FromException(new IOException("WriteFailed")) : Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }
}
