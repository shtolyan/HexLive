using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentRoutePromptTests
{
    [TestCase(null)]
    [TestCase(DialogueStyles.Masha)]
    public void OrdinaryDialoguePreservesAnAcceptedRouteForEveryStyle(string? style)
    {
        var prompt = AgentPromptBuilder.Build(style, "current action: move_to, InProgress", "Как дела?");
        Assert.That(prompt, Does.Contain("обычный разговор с игроком сохраняет action=null"));
        Assert.That(prompt, Does.Contain("только новым явным приказом игрока"));
        Assert.That(prompt, Does.Contain("существенной угрозы или препятствия"));
        Assert.That(prompt, Does.Contain("не означает отмену прежнего приказа"));
    }
}
