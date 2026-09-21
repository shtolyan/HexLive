using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace HexLive.Server.Tests;

public sealed class CentralAccessHttpTests
{
    private sealed class Authority : HttpMessageHandler
    {
        public string[] Rights = ["server.admin", "game.play"];
        public bool Revoked;
        public readonly string Account = Guid.NewGuid().ToString("N");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(Revoked ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { accountId = Account, permissions = Rights })) });
    }
    [Test]
    public async Task AdminRevalidatesRightsRejectsLegacyLoginAndUsesSameKeyForCommands()
    {
        var authority = new Authority();
        using var identity = new IdentityClient("https://keys.example", authority);
        var key = "hexlive_" + new string('a', 64);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting(); builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        CentralAdminAccess.Use(app, identity);
        app.MapPost("/admin/pause", (HttpContext c) => c.Items[CentralAdminAccess.Marker] is true ? Results.Ok() : Results.Unauthorized());
        app.MapPost("/api/worlds/v1/login", () => Results.Ok("legacy password accepted"));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            Assert.That((await client.PostAsync("/admin/pause", null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await client.PostAsync("/api/worlds/v1/login", null)).StatusCode, Is.EqualTo(HttpStatusCode.Gone));
            authority.Rights = ["game.play"];
            Assert.That((await client.PostAsync("/admin/pause", null)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(await identity.ResolveAsync(key), Is.EqualTo(authority.Account));
            var access = new GodMode.AdminAccess(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json")) {
                CentralAuthorize = (id, token) => identity.Can(token, id, "server.admin") };
            Assert.That(access.Authorized(authority.Account, key), Is.False);
            authority.Rights = ["server.admin"];
            Assert.That(access.Authorized(authority.Account, key), Is.True);
            Assert.That(access.Authorized(Guid.NewGuid().ToString("N"), key), Is.False);
            Assert.That(await identity.ResolveAsync(key), Is.Null);
            authority.Revoked = true;
            Assert.That(access.Authorized(authority.Account, key), Is.False);
            Assert.That((await client.PostAsync("/admin/pause", null)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally { await app.StopAsync(); }
    }
}
