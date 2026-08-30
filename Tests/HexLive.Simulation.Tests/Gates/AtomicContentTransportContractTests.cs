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
        var session = Read("Assets", "HexLive", "UnityPresentation", "Bootstrap",
            "SessionConfig.cs");
        var service = Read("Assets", "HexLive", "UnityPresentation", "Content",
            "ContentAssetService.cs");
        var fmod = Read("Assets", "HexLive", "UnityPresentation", "Audio", "FmodSfx.cs");
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
            Assert.That(endpoint, Does.Contain(
                "return EditorFriendly(FromGameServer(ServerBook.ProductionUrl));"),
                "до выбора мира реестр и музыка обязаны идти с prod, а не localhost");
            // Подмена HTTPS→legacy-HTTP живёт СТРОГО под #if UNITY_EDITOR:
            // редакторский UnityTls не проходит цепочку прод-сертификата
            // (Curl 35), но Player этой ветки иметь не должен.
            Assert.That(endpoint, Does.Contain("#if UNITY_EDITOR"));
            Assert.That(endpoint, Does.Contain(
                "return FromGameServer(ServerBook.LegacyProductionUrl);"));
            Assert.That(endpoint, Does.Not.Contain("DefaultLocal"));
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
            Assert.That(session, Does.Contain("public static event Action? ServerChanged;"));
            Assert.That(service, Does.Contain("RefreshRegistryRoutine(endpoint)"),
                "registry request обязан фиксировать endpoint на время async операции");
            Assert.That(service, Does.Contain("_priorityBlobDownloadQueue"));
            Assert.That(service, Does.Contain("record.IsLoadable"));
            Assert.That(service, Does.Contain("legacy.IsLoadable"),
                "retired object from an old save must remain usable from verified offline cache");
            Assert.That(service, Does.Contain("_pinned[record.Key] = record;"));
            Assert.That(fmod, Does.Contain("Application.streamingAssetsPath"));
            Assert.That(fmod, Does.Contain("EnsureVoiceGroup(id)"));
            Assert.That(fmod, Does.Not.Contain("ContentAssetService.Instance"),
                "общий звук Player не должен ждать live-content registry");
        });
    }

    [Test]
    public void SharedAudioShipsInPlayerAndIsNotPublishedAsAtomicContent()
    {
        var root = RepoPaths.Root;
        var fmod = Read("Assets", "HexLive", "UnityPresentation", "Audio", "FmodSfx.cs");
        var setup = Read("Assets", "Editor", "Fmod", "HexLiveFmodSetup.cs");
        var builder = Read("Assets", "HexLive", "UnityDebug", "Editor",
            "HexLiveReleaseBuilder.cs");
        var publisher = Read("Tools", "content.py");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(root, "Assets", "StreamingAssets",
                "HexLive", "Sfx", "loop_rain_0.wav")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Assets", "StreamingAssets",
                "HexLive", "Music", "hex_music.mp3")), Is.True);
            Assert.That(File.Exists(Path.Combine(root, "Assets", "StreamingAssets",
                "FMODBanks", "Master.bank")), Is.True);
            Assert.That(fmod, Does.Contain("Path.Combine(Application.streamingAssetsPath"));
            Assert.That(setup, Does.Contain("ImportType.StreamingAssets"));
            Assert.That(setup, Does.Contain("BankLoadType.All"));
            Assert.That(builder, Does.Contain(
                "Assets/StreamingAssets/HexLive/Sfx/"));
            Assert.That(builder, Does.Contain(
                "Assets/StreamingAssets/HexLive/Music/"));
            Assert.That(builder, Does.Contain(
                "Assets/StreamingAssets/FMODBanks/"));
            Assert.That(publisher, Does.Not.Contain(
                "write_raw_candidate(\n            output, \"audio\""));
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
