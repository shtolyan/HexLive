extern alias IdentityService;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using KeyStore = IdentityService::HexLive.Identity.KeyStore;
using Validation = IdentityService::HexLive.Identity.IdentityValidation;

namespace HexLive.Server.Tests;

public sealed class IdentityValidationHttpTests
{
    [Test]
    public async Task AgentPollingDoesNotExhaustOtherAccountsOrShareAnonymousBudget()
    {
        await using var fixture = await Fixture.Start();
        var one = fixture.Keys.Issue("One");
        var two = fixture.Keys.Issue("Two");
        for (var i = 0; i < Validation.AccountRequestsPerMinute; i++)
            Assert.That(await fixture.Check(one.Key), Is.EqualTo(HttpStatusCode.OK), $"Agent request {i}");
        Assert.That(await fixture.Check(one.Key), Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await fixture.Check(two.Key), Is.EqualTo(HttpStatusCode.OK));
        // Changing invalid keys must not allocate fresh budgets or consume valid accounts' permits.
        for (var i = 0; i < Validation.AnonymousRequestsPerMinute; i++)
            Assert.That(await fixture.Check("hexlive_" + i.ToString("x64")), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await fixture.Check("invalid"), Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await fixture.Check(two.Key), Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task AdmissionNeverCachesPermissionsAndRotationKeepsTheAccountBudget()
    {
        await using var fixture = await Fixture.Start();
        var account = fixture.Keys.Issue("Player", permissions: ["game.play", "server.admin"]);
        Assert.That(await fixture.Check(account.Key), Is.EqualTo(HttpStatusCode.OK));
        fixture.Keys.SetPermissions(account.Account.Id, ["game.play"], 1, "test");
        using (var response = await fixture.Send(account.Key))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { "game.play" }));
        }
        for (var i = 2; i < Validation.AccountRequestsPerMinute; i++)
            Assert.That(await fixture.Check(account.Key), Is.EqualTo(HttpStatusCode.OK));
        var rotated = fixture.Keys.Issue("Player", account.Account.Id, expectedRevision: 2);
        Assert.That(await fixture.Check(rotated.Key), Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await fixture.Check(account.Key), Is.EqualTo(HttpStatusCode.Unauthorized));
        fixture.Keys.Revoke(account.Account.Id);
        Assert.That(await fixture.Check(rotated.Key), Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("identity-rate-test-").FullName;
        public KeyStore Keys = null!;
        private WebApplication _app = null!;
        private HttpClient _client = null!;

        public static async Task<Fixture> Start()
        {
            var fixture = new Fixture();
            fixture.Keys = new KeyStore(Path.Combine(fixture._root, "accounts.json"));
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Services.AddRouting(); builder.Logging.ClearProviders();
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                Validation.Configure(options, fixture.Keys);
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            fixture._app = builder.Build();
            fixture._app.UseRouting(); fixture._app.UseRateLimiter();
            Validation.Map(fixture._app, fixture.Keys);
            await fixture._app.StartAsync();
            fixture._client = new HttpClient { BaseAddress = new Uri(fixture._app.Urls.First()) };
            return fixture;
        }

        public async Task<HttpResponseMessage> Send(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/identity/v1/validate");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await _client.SendAsync(request);
        }
        public async Task<HttpStatusCode> Check(string key)
        {
            using var response = await Send(key);
            return response.StatusCode;
        }
        public async ValueTask DisposeAsync()
        {
            _client.Dispose(); await _app.StopAsync(); await _app.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}
