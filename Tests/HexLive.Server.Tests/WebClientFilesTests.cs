using System.IO;
using HexLive.Server;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

/// <summary>
/// §168.10: <c>--web-root</c> отдаёт сборку Unity с настоящим типом внутреннего
/// файла и Content-Encoding для .br/.gz, и не выпускает запрос за корень.
/// </summary>
public sealed class WebClientFilesTests
{
    private string _root = string.Empty;

    [SetUp]
    public void CreateBuild()
    {
        _root = Path.Combine(Path.GetTempPath(), "hexlive-webroot-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "Build"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html></html>");
        File.WriteAllBytes(Path.Combine(_root, "Build", "WebGL.wasm.br"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "Build", "WebGL.framework.js.br"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "Build", "WebGL.data.gz"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_root, "Build", "WebGL.loader.js"), "x");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside-" + Path.GetFileName(_root)), "secret");
    }

    [TearDown]
    public void RemoveBuild()
    {
        Directory.Delete(_root, recursive: true);
        File.Delete(Path.Combine(Path.GetDirectoryName(_root)!, "outside-" + Path.GetFileName(_root)));
    }

    [Test]
    public void EmptyPathIsTheIndexPage()
    {
        Assert.That(WebClientFiles.TryResolve(_root, null, out var file), Is.True);
        Assert.That(file.ContentType, Does.StartWith("text/html"));
        Assert.That(file.ContentEncoding, Is.Null);
    }

    [Test]
    public void BrotliWasmKeepsTheWasmTypeForStreamingCompile()
    {
        Assert.That(WebClientFiles.TryResolve(_root, "Build/WebGL.wasm.br", out var file), Is.True);
        Assert.That(file.ContentType, Is.EqualTo("application/wasm"));
        Assert.That(file.ContentEncoding, Is.EqualTo("br"));
    }

    [Test]
    public void CompressedScriptsAndDataGetTheirInnerTypes()
    {
        Assert.That(WebClientFiles.TryResolve(_root, "Build/WebGL.framework.js.br", out var js), Is.True);
        Assert.That(js.ContentType, Is.EqualTo("application/javascript"));
        Assert.That(WebClientFiles.TryResolve(_root, "Build/WebGL.data.gz", out var data), Is.True);
        Assert.That(data.ContentType, Is.EqualTo("application/octet-stream"));
        Assert.That(data.ContentEncoding, Is.EqualTo("gzip"));
        Assert.That(WebClientFiles.TryResolve(_root, "Build/WebGL.loader.js", out var loader), Is.True);
        Assert.That(loader.ContentEncoding, Is.Null);
    }

    [Test]
    public void TraversalAndMissingFilesAreRefused()
    {
        Assert.That(WebClientFiles.TryResolve(_root, "../outside-" + Path.GetFileName(_root), out _), Is.False);
        Assert.That(WebClientFiles.TryResolve(_root, "..\\x", out _), Is.False);
        Assert.That(WebClientFiles.TryResolve(_root, "Build/missing.wasm.br", out _), Is.False);
        Assert.That(WebClientFiles.TryResolve(_root, "Build", out _), Is.False);
    }
}

}
