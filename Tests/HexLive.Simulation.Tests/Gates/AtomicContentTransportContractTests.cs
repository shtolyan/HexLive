using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§152.4: production is wss/https; only local development may retain
/// plain HTTP while saved legacy production addresses migrate automatically.</summary>
public sealed class AtomicContentTransportContractTests
{
    [Test]
    public void ProductionUsesTlsWhileLocalDevelopmentKeepsHttpCompatibility()
    {
        var settings = Read("ProjectSettings", "ProjectSettings.asset");
        var endpoint = Read("Assets", "HexLive", "UnityPresentation", "Content",
            "ContentEndpoint.cs");
        var serverBook = Read("Assets", "HexLive", "UnityPresentation", "Bootstrap",
            "ServerBook.cs");
        var proxy = Read("Server", "Caddyfile");
        var spec = Read("Spec", "152.md");

        Assert.Multiple(() =>
        {
            // UnityEditor.InsecureHttpOption.DevelopmentOnly: localhost still
            // works in Editor/dev builds, but a release Player cannot use HTTP.
            Assert.That(settings, Does.Contain("insecureHttpOption: 1"));
            Assert.That(endpoint, Does.Contain(
                "Scheme = websocket.Scheme == \"wss\" ? \"https\" : \"http\""));
            Assert.That(endpoint, Does.Contain("-hexlive-assets"));
            Assert.That(serverBook, Does.Contain(
                "wss://vmi3529459.contaboserver.net/watch"));
            Assert.That(serverBook, Does.Contain("LegacyProductionUrl"));
            Assert.That(serverBook, Does.Contain("return ProductionUrl;"));
            Assert.That(proxy, Does.Contain("vmi3529459.contaboserver.net"));
            Assert.That(proxy, Does.Contain("reverse_proxy 127.0.0.1:5123"));
            Assert.That(spec, Does.Contain(
                "https://vmi3529459.contaboserver.net/api/assets/v1"));
            Assert.That(spec, Does.Contain(
                "PlayerSettings.insecureHttpOption=DevelopmentOnly"));
        });
    }

    private static string Read(params string[] path)
    {
        var fullPath = RepoPaths.Root;
        foreach (var part in path)
        {
            fullPath = Path.Combine(fullPath, part);
        }

        return File.ReadAllText(fullPath);
    }
}

}
