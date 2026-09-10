using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace HexLive.Server.Tests;

public sealed class RetiredBugTrackerTests
{
    [Test]
    public async Task LegacyRoutesRejectEveryVerbWhileGameRoutesStillWork()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        RetiredBugTracker.Map(app);
        app.MapGet("/", () => Results.Ok("world"));
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient { BaseAddress = new System.Uri(System.Linq.Enumerable.Single(addresses.Addresses)) };
        foreach (var path in new[] { "/api/bugs/v1", "/api/bugs/v1/reports", "/api/bugs/v1/reports/1/comments", "/api/bugs/v1/commits/deadbeef", "/admin/bugs", "/admin/bugs/1" })
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Head })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, path));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Gone), $"{method} {path}");
        }
        Assert.That(await client.GetStringAsync("/"), Does.Contain("world"));
    }
}
