using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class PromptFilesTests
{
    [Test]
    public void AuthoredPromptsArePackagedAndLanguageRuleWinsOverExamples()
    {
        var prompt = AgentPromptBuilder.Build(DialogueStyles.Masha, "", "Hello there");
        Assert.That(prompt, Does.Contain("<conversation_language>"));
        Assert.That(prompt, Does.Not.Contain("говори по-русски").And.Not.Contain("разговорным русским"));
        Assert.That(prompt.LastIndexOf("<conversation_language>"), Is.GreaterThan(prompt.IndexOf("<authored_voice")));
        Assert.That(prompt, Does.Contain("new memory values").And.Contain("never as a new request"));
        Assert.That(AgentPromptFiles.Read("soul.md"), Does.Contain("{0}"));
        Assert.That(AgentPromptFiles.Text("MashaMemoryStore.01"), Is.EqualTo("Маша"));
    }

    [Test]
    public void MissingAndEscapingFilesFailWithoutHiddenFallback()
    {
        Assert.Throws<InvalidDataException>(() => AgentPromptFiles.Read("../rules.md"));
        Assert.Throws<InvalidDataException>(() => AgentPromptFiles.Read("missing.md"));
        Assert.Throws<InvalidDataException>(() => AgentPromptFiles.Text("missing.key"));
    }

    [Test]
    public void LatestMessageSwitchesLanguageAndHeartbeatDoesNotConsumeOldRequest()
    {
        var previous = AgentConversationLanguage.LastMessage("Hello", []);
        Assert.That(AgentConversationLanguage.LastMessage("", [], previous), Is.EqualTo("Hello"));
        Assert.That(AgentConversationLanguage.LastMessage("Привет", [], previous), Is.EqualTo("Привет"));
        Assert.That(AgentConversationLanguage.LastMessage("", ["Игрок: Hello", "Маша: Привет"]), Is.EqualTo("Hello"));
        Assert.That(AgentConversationLanguage.LastMessage("", ["Player: Good morning", "Masha: Hi"]), Is.EqualTo("Good morning"));
        Assert.That(AgentConversationLanguage.LastMessage("", []), Is.Empty);
        Assert.That(AgentConversationLanguage.SpeechTag("Hello!"), Is.EqualTo("en"));
        Assert.That(AgentConversationLanguage.SpeechTag("Привет!"), Is.EqualTo("ru"));
        Assert.That(AgentConversationLanguage.SpeechTag("你好"), Is.EqualTo("und"));
    }
}
