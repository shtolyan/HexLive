using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Server.Releases;
using HexLive.Simulation.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HexLive.Server.Tests.Releases;

public sealed class ReleaseHttpTests
{
    [Test]
    public async Task CompatibilityAndPublishedArchiveWorkWithoutGameAuthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), "hexlive-release-http-" + Guid.NewGuid().ToString("N"));
        var store = new PlayerReleaseStore(root);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(store.RootPath, "blobs", sha), bytes);
        File.WriteAllText(Path.Combine(store.RootPath, "latest.json"), JsonSerializer.Serialize(new {
            version = "test", playerRelease = new { archiveSha256 = sha, archiveSize = bytes.Length, protocolVersion = Handshake.ProtocolVersion }
        }));
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ClientCompatibilityEndpoints.Map(app); PlayerReleaseEndpoints.Map(app, store);
        try
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var compatibility = await client.GetAsync("/api/client/v1/compatibility");
            Assert.That(compatibility.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(compatibility.Headers.CacheControl!.NoStore, Is.True);
            using var json = JsonDocument.Parse(await compatibility.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.GetProperty("protocolVersion").GetInt32(), Is.EqualTo(Handshake.ProtocolVersion));
            using var latest = await client.GetAsync("/api/releases/v1/windows/latest");
            Assert.That(latest.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/releases/v1/windows/blobs/" + sha);
            request.Headers.Range = new RangeHeaderValue(2, null);
            using var partial = await client.SendAsync(request);
            Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await partial.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 3, 4, 5 }));
            Assert.That(partial.Content.Headers.ContentRange!.Length, Is.EqualTo(5));
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
    }
}
