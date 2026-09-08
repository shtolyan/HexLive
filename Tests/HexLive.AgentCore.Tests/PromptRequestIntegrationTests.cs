using System.Text;
using System.Text.Json;
using HexLive.AgentHost;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class PromptRequestIntegrationTests
{
    private static AgentProviders Provider(CaptureModel model) => new(new AgentProviderOptions
    {
        McpUri = new Uri("https://example.invalid/mcp"), McpToken = "fixture-only",
        XaiKey = "", ElevenLabsKey = "", XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "",
        DialogueStyleId = DialogueStyles.Masha
    }, model, new(ModelProviderKind.Grok, "fixture", "fixture"));

    [Test]
    public async Task ModelBoundaryKeepsLanguageAnchorWithoutReplayingPlayerSpeech()
    {
        var model = new CaptureModel();
        using var provider = Provider(model);
        const string memory = "# Память\nЯ помню старый остров.";
        await provider.DecideAsync("voice", "{}", memory, "Hello, how are you?", [], default);
        await provider.DecideAsync("heartbeat", "{}", memory, "", [], default);
        await provider.DecideAsync("voice", "{}", memory, "Теперь говорим по-русски", [], default);
        await provider.DecideAsync("heartbeat", "{}", memory, "", [], default);
        Assert.That(model.Requests.Select(Anchor), Is.EqualTo(new[]
        { "Hello, how are you?", "Hello, how are you?", "Теперь говорим по-русски", "Теперь говорим по-русски" }));
        foreach (var index in new[] { 1, 3 })
        {
            using var input = JsonDocument.Parse(model.Requests[index].Input);
            Assert.That(input.RootElement.GetProperty("playerSpeech").GetString(), Is.Empty);
            Assert.That(input.RootElement.GetProperty("trigger").GetString(), Is.EqualTo("heartbeat"));
        }
        foreach (var request in model.Requests)
        {
            Assert.That(request.Context, Is.EqualTo(memory));
            Assert.That(request.Instructions, Does.Contain("<conversation_language>"));
            Assert.That(request.Instructions, Does.Not.Contain("говори по-русски"));
        }
    }

    [Test]
    public async Task RecreatedProviderRestoresAnchorAndTwoAgentsStayIndependent()
    {
        var first = new CaptureModel(); var second = new CaptureModel();
        using var a = Provider(first); using var b = Provider(second);
        await a.DecideAsync("heartbeat", "{}", "", "", ["Игрок: Speak English", "Маша: Хорошо"], default);
        await b.DecideAsync("voice", "{}", "", "Привет", [], default);
        await a.DecideAsync("heartbeat", "{}", "", "", [], default);
        Assert.That(first.Requests.Select(Anchor), Is.EqualTo(new[] { "Speak English", "Speak English" }));
        Assert.That(Anchor(second.Requests.Single()), Is.EqualTo("Привет"));
    }

    [Test]
    public void PromptFilesRejectInvalidUtf8OversizeAndLinksAndReadEdits()
    {
        var name = "fixture-" + Guid.NewGuid().ToString("N") + ".md";
        var path = Path.Combine(AgentPromptFiles.DirectoryPath, name);
        var link = path + ".link";
        try
        {
            File.WriteAllText(path, "before");
            Assert.That(AgentPromptFiles.Read(name), Is.EqualTo("before"));
            File.WriteAllText(path, "after");
            Assert.That(AgentPromptFiles.Read(name), Is.EqualTo("after"));
            File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, 0x61]);
            Assert.That(AgentPromptFiles.Read(name), Is.EqualTo("a"));
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(link, path);
                Assert.Throws<InvalidDataException>(() => AgentPromptFiles.Read(name + ".link"));
            }
            File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
            Assert.Throws<DecoderFallbackException>(() => AgentPromptFiles.Read(name));
            File.WriteAllText(path, new string('x', 262145));
            Assert.Throws<InvalidDataException>(() => AgentPromptFiles.Read(name));
        }
        finally { File.Delete(link); File.Delete(path); }
    }

    private static string Anchor(ModelRequest request)
    {
        using var input = JsonDocument.Parse(request.Input);
        return input.RootElement.GetProperty("lastPlayerMessageForLanguage").GetString()!;
    }

    private sealed class CaptureModel : IModelAdapter
    {
        public List<ModelRequest> Requests { get; } = new();
        public Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(new ModelAnswer("""
            {"speech":"","emotion":"neutral","action":null,"reaction":"None","intentSummary":"Fixture",
             "memoryUpserts":[],"journalText":"","relationshipAssessment":{
             "learnedSomethingSignificant":false,"trust":"Unchanged","sympathy":"Unchanged",
             "seriousHarm":false,"reason":"Fixture","voiceName":null,"namingReason":null}}
            """));
        }
    }
}
