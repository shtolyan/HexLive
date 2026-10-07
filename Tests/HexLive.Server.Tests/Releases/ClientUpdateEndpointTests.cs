using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using HexLive.Server.Releases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NUnit.Framework;

namespace HexLive.Server.Tests.Releases;
public sealed class ClientUpdateEndpointTests
{
    [TestCase("windows", "x64")]
    [TestCase("macos", "arm64")]
    [TestCase("macos", "x64")]
    public async Task MetadataEtagAndArchiveRangeAreServedWithoutGoogleCredentials(string platform, string architecture)
    {
        var root = Path.Combine(Path.GetTempPath(), "hexlive-feed-" + Guid.NewGuid());
        var directory = Path.Combine(root,platform,architecture); Directory.CreateDirectory(Path.Combine(directory,"blobs"));
        var sha = new string('a',64);
        File.WriteAllBytes(Path.Combine(directory,"blobs",sha),Encoding.UTF8.GetBytes("0123456789"));
        File.WriteAllText(Path.Combine(directory,"latest.json"),"{\"payload\":\"signed\"}");
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); ClientUpdateEndpoints.Map(app,root);
        try
        {
            await app.StartAsync();
            var address = System.Linq.Enumerable.First(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var feed = $"/api/releases/v2/client/{platform}/{architecture}/stable";
            using var latest = await client.GetAsync(feed+"/latest"); Assert.That(latest.StatusCode,Is.EqualTo(HttpStatusCode.OK));
            using var cached = new HttpRequestMessage(HttpMethod.Get,feed+"/latest"); cached.Headers.IfNoneMatch.Add(latest.Headers.ETag!);
            using var unchanged = await client.SendAsync(cached); Assert.That(unchanged.StatusCode,Is.EqualTo(HttpStatusCode.NotModified));
            using var request = new HttpRequestMessage(HttpMethod.Get,feed+"/blobs/"+sha); request.Headers.Range = new RangeHeaderValue(4,null);
            using var response = await client.SendAsync(request); Assert.That(response.StatusCode,Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(await response.Content.ReadAsStringAsync(),Is.EqualTo("456789"));
            Assert.That((await client.GetAsync("/api/releases/v2/client/linux/x64/stable/latest")).StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await client.GetAsync(feed+"/blobs/invalid")).StatusCode,Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally { await app.StopAsync(); Directory.Delete(root,true); }
    }
}
