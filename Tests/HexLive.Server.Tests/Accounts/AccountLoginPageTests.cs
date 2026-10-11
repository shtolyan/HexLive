using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HexLive.Server.Accounts;
using NUnit.Framework;

namespace HexLive.Server.Tests.Accounts;

public sealed class AccountLoginPageTests
{
    [TestCase("/login")]
    [TestCase("/login/")]
    [TestCase("/api/accounts/v1/client-config")]
    public async Task WithoutProjectDoesNotMapPublicRoutes(string path)
    {
        // Supplying web options alone must never enable account routes.
        var options = ServerOptions.Parse(new[] { "--firebase-api-key", "public-test-key", "--firebase-providers", "apple" }, _ => null)!;
        await using var app = AccountHttpTests.BuildApp();
        AccountLoginPage.Map(app, options); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally { await app.StopAsync(); }
    }

    [TestCase("google,password", "configured-login.example.com")]
    [TestCase("google,password,apple", null)]
    [TestCase("password", null)]
    public async Task ConfigurationIsPublicExplicitProjectionAndHtmlIsProjectIndependent(string providers, string? domain)
    {
        var args = new[] { "--firebase-project", "fixture-login-project", "--firebase-api-key", "public-fixture-key",
            "--firebase-providers", providers };
        if (domain != null) args = args.Concat(new[] { "--firebase-auth-domain", domain }).ToArray();
        var options = ServerOptions.Parse(args, _ => null)!;
        await using var app = AccountHttpTests.BuildApp();
        AccountLoginPage.Map(app, options); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var config = await client.GetAsync(AccountEndpoints.Prefix + "/client-config");
            Assert.That(config.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(config.Headers.CacheControl!.NoStore, Is.True);
            var json = await config.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(json.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "projectId", "apiKey", "authDomain", "providers", "emulator", "requireVerifiedEmail" }));
            Assert.That(json.GetProperty("emulator").ValueKind, Is.EqualTo(JsonValueKind.Null), "no emulator unless asked for");
            Assert.That(json.GetProperty("requireVerifiedEmail").GetBoolean(), Is.False);
            Assert.That(json.GetProperty("projectId").GetString(), Is.EqualTo(options.FirebaseProject));
            Assert.That(json.GetProperty("apiKey").GetString(), Is.EqualTo(options.FirebaseApiKey));
            Assert.That(json.GetProperty("authDomain").GetString(), Is.EqualTo(domain ?? "fixture-login-project.firebaseapp.com"));
            Assert.That(json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()), Is.EqualTo(providers.Split(',')));

            string? firstHtml = null;
            foreach (var path in new[] { "/login", "/login/" })
            {
                using var response = await client.GetAsync(path);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/html"));
                Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
                var html = await response.Content.ReadAsStringAsync();
                if (firstHtml != null) Assert.That(html, Is.EqualTo(firstHtml));
                firstHtml = html;
                Assert.That(html, Does.Not.Contain(options.FirebaseApiKey).And.Not.Contain(options.FirebaseProject!).And.Not.Contain(options.FirebaseAuthDomain!));
                Assert.That(html, Does.Not.Contain("AIza").And.Not.Contain("hexlive-game").And.Not.Contain("innerHTML"));
                Assert.That(html, Does.Contain("api('/client-config'").And.Contain("hexlive.session").And.Contain("/play/"));
                var csp = response.Headers.GetValues("Content-Security-Policy").Single();
                Assert.That(csp, Does.Contain("default-src 'none'").And.Contain("frame-ancestors 'none'").And.Contain("https://" + options.FirebaseAuthDomain));
                Assert.That(csp, Does.Not.Contain("unsafe-inline").And.Not.Contain("unsafe-eval").And.Not.Contain("*"));
                foreach (var tag in new[] { "script", "style" })
                {
                    var blocks = Regex.Matches(html, $"<{tag}(?:\\s[^>]*)?>([\\s\\S]*?)</{tag}>");
                    Assert.That(blocks, Is.Not.Empty);
                    foreach (Match block in blocks)
                    {
                        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(block.Groups[1].Value)));
                        Assert.That(csp, Does.Contain("'sha256-" + hash + "'"), "CSP must authorize the exact served inline bytes");
                    }
                }
                Assert.That(response.Headers.GetValues("Cross-Origin-Opener-Policy").Single(), Is.EqualTo("same-origin-allow-popups"));
                Assert.That(response.Headers.GetValues("Referrer-Policy").Single(), Is.EqualTo("no-referrer"));
            }
        }
        finally { await app.StopAsync(); }
    }

    [Test]
    public void DefaultsPreserveProjectOnlyOptInAndDoNotInventAWebKey()
    {
        var options = ServerOptions.Parse(new[] { "--firebase-project", "fixture-project" }, _ => null)!;
        Assert.That(options.FirebaseApiKey, Is.Empty);
        Assert.That(options.FirebaseAuthDomain, Is.EqualTo("fixture-project.firebaseapp.com"));
        Assert.That(options.FirebaseProviders, Is.EqualTo(new[] { "google", "password" }));
    }

    [TestCase("https://example.com")]
    [TestCase("example.com/path")]
    [TestCase("example.com; script-src *")]
    [TestCase("example.com\r\nX-Injected: yes")]
    [TestCase("*.firebaseapp.com")]
    public void AuthDomainCannotInjectOriginsOrCsp(string domain) =>
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--firebase-auth-domain", domain }, _ => null));

    [TestCase("google,unknown")]
    [TestCase("google,,password")]
    [TestCase("")]
    public void UnknownProvidersFailAtStartup(string providers) =>
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--firebase-providers", providers }, _ => null));
}
