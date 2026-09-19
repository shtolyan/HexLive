using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HexLive.Launcher;
using NUnit.Framework;

namespace HexLive.Launcher.Tests;

public sealed class UpdateTests
{
    private string _root = null!;
    [SetUp] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "hexlive-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TearDown] public void Cleanup() { Directory.Delete(_root, true); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }

    [Test]
    public void BundledAudioDoesNotBlockUpdateButMissingWindowsModelsDo()
    {
        var index = new ContentIndex { PlatformMissing = [new ContentKey { Type = "audio", Id = "bank.master" }] };
        Assert.DoesNotThrow(() => LauncherService.ValidateRuntimeContent(index));
        index.PlatformMissing.Add(new ContentKey { Type = "wear", Id = "clothing.helmet_m1" });
        Assert.Throws<InvalidDataException>(() => LauncherService.ValidateRuntimeContent(index));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownloadClosesFileBeforeHashAndResumes(bool resume)
    {
        var bytes = Enumerable.Range(0, 1024).Select(x => (byte)x).ToArray();
        var file = Path.Combine(_root, "archive.part");
        if (resume) File.WriteAllBytes(file, bytes[..300]);
        var service = new LauncherService(new Handler(request => {
            Assert.That(request.Headers.Range?.Ranges.First().From, Is.EqualTo(resume ? (long?)300 : null));
            var response = new HttpResponseMessage(resume ? HttpStatusCode.PartialContent : HttpStatusCode.OK) {
                Content = new ByteArrayContent(resume ? bytes[300..] : bytes) };
            if (resume) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(300, 1023, 1024);
            return response;
        }));
        await service.DownloadVerifiedAsync("https://test/archive", file, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, _ => { }, default);
        Assert.That(File.ReadAllBytes(file), Is.EqualTo(bytes));
    }

    [Test]
    public void CorruptArchiveIsNeverAccepted()
    {
        var service = new LauncherService(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }));
        Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadVerifiedAsync("https://test/archive", Path.Combine(_root, "bad.part"), new string('0', 64), 3, _ => { }, default));
    }

    [Test]
    public void ResumeRejectsWrongContentRange()
    {
        var file = Path.Combine(_root, "bad-range.part"); File.WriteAllBytes(file, new byte[] { 1 });
        var service = new LauncherService(new Handler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[] { 2, 3 }) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 3); return response;
        }));
        Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadVerifiedAsync("https://test/archive", file, new string('0', 64), 3, _ => { }, default));
        Assert.That(File.ReadAllBytes(file), Is.EqualTo(new byte[] { 1 }));
    }

    [Test]
    public void UpdateRequiresCompatibleProtocolAndChecksHashNotOnlyVersion()
    {
        var release = new RemoteRelease { Version = "1", PlayerRelease = new() { ProtocolVersion = 19, ArchiveSha256 = new string('a', 64) } };
        Assert.DoesNotThrow(() => LauncherService.RequireProtocol(release, 19));
        Assert.Throws<InvalidDataException>(() => LauncherService.RequireProtocol(release, 20));
        Assert.That(LauncherService.NeedsUpdate(new InstallState { Version = "1", ArchiveSha256 = new string('b', 64) }, release), Is.True);
        Assert.That(LauncherService.NeedsUpdate(new InstallState { Version = "1", ArchiveSha256 = new string('a', 64) }, release), Is.False);
    }

    [Test]
    public void UpdateHandoffRejectsInvalidArguments()
    {
        Assert.That(UpdateRequest.Parse(new[] { "--update", "--wait-pid", "123", "--required-protocol", "19" }), Is.EqualTo(new UpdateRequest(123, 19)));
        Assert.Throws<ArgumentException>(() => UpdateRequest.Parse(new[] { "--update" }));
        Assert.Throws<ArgumentException>(() => UpdateRequest.Parse(new[] { "--update", "--wait-pid", "-1", "--required-protocol", "19" }));
    }
}
