using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class CodexExecutableTests
{
    private const string Nested = "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex";
    private const string Legacy = "/Applications/ChatGPT.app/Contents/Resources/codex";

    [Test]
    public void UpdatedDesktopInstallationTakesPrecedence() =>
        Assert.That(CodexExecutable.ResolveMacOS(path => path == Nested || path == Legacy), Is.EqualTo(Nested));

    [Test]
    public void OlderDesktopInstallationRemainsSupported() =>
        Assert.That(CodexExecutable.ResolveMacOS(path => path == Legacy), Is.EqualTo(Legacy));

    [Test]
    public void StandaloneCliUsesPathWhenDesktopIsAbsent() =>
        Assert.That(CodexExecutable.ResolveMacOS(_ => false), Is.EqualTo("codex"));
}
