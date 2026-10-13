using System.Text.Json;
using HexLive.AgentCore.Studio;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HexLive.VoiceBrain.Tests;

public sealed class VoiceRelationshipViewTests
{
    [Test]
    public void LearnedNameAndMetricsReachUiAndRemainScopedToOwnerAndCharacter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hexlive-bond-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RelationshipStore(Path.Combine(directory, "bonds"));
            Assert.That(store.Apply("world:1", "owner", "turn", new(true, RelationshipDirection.Increase,
                RelationshipDirection.Increase, false, "Познакомились", "Толя", "Голос представился")), Is.True);
            Assert.That(store.Apply("world:1", "owner", "turn", new(true, RelationshipDirection.Increase,
                RelationshipDirection.Increase, false, "Повтор")), Is.False);
            var service = new VoiceBrainService(new BrainOptions { OpenAiKey = "fixture", ElevenLabsKey = "fixture",
                BrainSecret = "fixture", TicketKey = new byte[32], StateDirectory = directory },
                _ => throw new InvalidOperationException(), NullLogger.Instance);
            var view = JsonSerializer.SerializeToElement(service.RelationshipView("world:1", "owner"));
            Assert.Multiple(() =>
            {
                Assert.That(view.GetProperty("voiceName").GetString(), Is.EqualTo("Толя"));
                Assert.That(view.GetProperty("familiarity").GetSingle(), Is.EqualTo(.02f));
                Assert.That(view.GetProperty("trust").GetSingle(), Is.EqualTo(.03f));
                Assert.That(view.GetProperty("affinity").GetSingle(), Is.EqualTo(.02f));
            });
            foreach (var pair in new[] { ("world:1", "other"), ("world:2", "owner") })
            {
                var other = JsonSerializer.SerializeToElement(service.RelationshipView(pair.Item1, pair.Item2));
                Assert.That(other.GetProperty("trust").GetSingle(), Is.Zero);
                Assert.That(other.GetProperty("voiceName").GetString(), Is.EqualTo("Голос"));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
