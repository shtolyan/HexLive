using System.Collections.Concurrent;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class AgentFleetTests
{
    [Test] public async Task TwoAgentsUseSeparateSelectionsAndStoppingOneDoesNotStopTheOther()
    {
        var root = Directory.CreateTempSubdirectory("studio-fleet-");
        try
        {
            var server = new ServerProfile(Guid.NewGuid(), "Test", new Uri("http://127.0.0.1/mcp"), "server-key");
            var masha = new AgentProfile(Guid.NewGuid(), "Masha", Path.Combine(root.FullName, "masha"), server.Id, "world", 901,
                new(ModelProviderKind.Grok, "grok-key", "grok-model"), new("voice-key-a", "masha-voice", "tts-a"));
            var nika = new AgentProfile(Guid.NewGuid(), "Nika", Path.Combine(root.FullName, "nika"), server.Id, "world", 902,
                new(ModelProviderKind.DeepSeek, "deepseek-key", "deepseek-model", "enabled"), new("voice-key-b", "nika-voice", "tts-b"));
            var seen = new ConcurrentDictionary<Guid, AgentProfile>();
            var sessions = new ConcurrentDictionary<Guid, Session>();
            await using var fleet = new AgentFleet((profile, selectedServer, _) =>
            {
                Assert.That(selectedServer, Is.EqualTo(server));
                seen[profile.Id] = profile;
                var session = new Session(); sessions[profile.Id] = session;
                return Task.FromResult<IAgentSession>(session);
            });
            Assert.That(await fleet.SnapshotAsync(), Is.Empty, "Opening the application does not connect");
            await Task.WhenAll(fleet.StartAsync(masha, server), fleet.StartAsync(nika, server));
            Assert.That((await fleet.SnapshotAsync()).All(x => x.State == AgentRunState.Running), Is.True);
            Assert.That(seen[masha.Id].Model, Is.EqualTo(masha.Model));
            Assert.That(seen[nika.Id].Voice, Is.EqualTo(nika.Voice));
            sessions[masha.Id].DiagnosticsErrorCode = "DiagnosticsUnavailable";
            var warning = (await fleet.SnapshotAsync()).Single(x => x.ProfileId == masha.Id);
            Assert.That(warning.DiagnosticsErrorCode, Is.EqualTo("DiagnosticsUnavailable"));
            Assert.That(warning.State, Is.EqualTo(AgentRunState.Running), "Lost diagnostics must not stop the agent");
            Assert.That((await fleet.SnapshotAsync()).Single(x => x.ProfileId == nika.Id).DiagnosticsErrorCode, Is.Empty);
            Assert.ThrowsAsync<InvalidOperationException>(() => fleet.StartAsync(masha with { Model = nika.Model }, server));
            await fleet.StopAsync(masha.Id);
            Assert.That(sessions[masha.Id].Detached, Is.True);
            Assert.That(sessions[nika.Id].Detached, Is.False);
            Assert.That((await fleet.SnapshotAsync()).Single(x => x.ProfileId == nika.Id).State, Is.EqualTo(AgentRunState.Running));
            await fleet.StopAllAsync();
            Assert.That(sessions[nika.Id].Detached, Is.True);
            await fleet.StartAsync(masha with { Model = nika.Model, Voice = null }, server);
            Assert.That(seen[masha.Id].Model, Is.EqualTo(nika.Model));
            Assert.That(seen[masha.Id].Voice, Is.Null);
            await fleet.StopAllAsync();
        }
        finally { root.Delete(true); }
    }
    private sealed class Session : IAgentSession, IAgentSessionStatus
    {
        public bool Detached;
        public AgentRunState State => AgentRunState.Running;
        public string IntentSummary => "";
        public string DiagnosticsErrorCode { get; set; } = "";
        public Task RunAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public Task DetachAsync(CancellationToken token) { Detached = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
