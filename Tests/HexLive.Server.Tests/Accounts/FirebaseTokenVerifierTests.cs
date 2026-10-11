using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;

namespace HexLive.Server.Tests.Accounts;

public sealed class FirebaseTokenVerifierTests
{
    [TestCase("google.com")]
    [TestCase("apple.com")]
    [TestCase("password")]
    public async Task SupportedProviderHasVerifiedIdentity(string provider)
    {
        using var fixture = new FirebaseFixture();
        var result = await fixture.Verifier.VerifyAsync(fixture.Token(provider));
        Assert.That(result.Identity?.Uid, Is.EqualTo("firebase-user-1"));
        Assert.That(result.Identity?.Provider, Is.EqualTo(provider));
    }

    [TestCase("aud")]
    [TestCase("iss")]
    [TestCase("expired")]
    [TestCase("future")]
    [TestCase("exp-missing")]
    [TestCase("iat-missing")]
    [TestCase("sub-empty")]
    [TestCase("sub-long")]
    [TestCase("sub-type")]
    [TestCase("exp-type")]
    [TestCase("auth-future")]
    public async Task InvalidClaimsAreIndistinguishable(string problem)
    {
        using var f = new FirebaseFixture();
        var token = f.Token(change: c => {
            switch (problem)
            {
                case "aud": c["aud"] = "other-project"; break;
                case "iss": c["iss"] = "https://attacker.test/"; break;
                case "expired": c["iat"] = f.Clock.Now.AddHours(-2).ToUnixTimeSeconds(); c["exp"] = f.Clock.Now.AddMinutes(-3).ToUnixTimeSeconds(); break;
                case "future": c["iat"] = f.Clock.Now.AddMinutes(3).ToUnixTimeSeconds(); break;
                case "exp-missing": c.Remove("exp"); break;
                case "iat-missing": c.Remove("iat"); break;
                case "sub-empty": c["sub"] = ""; break;
                case "sub-long": c["sub"] = new string('x', 129); break;
                case "sub-type": c["sub"] = 123; break;
                case "exp-type": c["exp"] = "forever"; break;
                case "auth-future": c["auth_time"] = f.Clock.Now.AddMinutes(3).ToUnixTimeSeconds(); break;
            }
        });
        var result = await f.Verifier.VerifyAsync(token);
        Assert.That(result.Identity, Is.Null);
        Assert.That(result.Error, Is.EqualTo("invalid_credentials"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnverifiedPasswordIsActionableOnlyWithValidSignature(bool forged)
    {
        using var f = new FirebaseFixture(); using var other = RSA.Create(2048);
        var result = await f.Verifier.VerifyAsync(f.Token("password", c => c.Remove("email_verified"), forged ? other : null));
        Assert.That(result.Identity, Is.Null);
        Assert.That(result.Error, Is.EqualTo(forged ? "invalid_credentials" : "email_not_verified"));
    }

    [Test]
    public async Task UnverifiedPasswordPlaysByDefault()
    {
        // §171.9 (Толя 11.10): the confirmation mail landed in spam and its link broke — a new player must not be locked out.
        using var f = new FirebaseFixture(requireVerifiedEmail: false);
        var result = await f.Verifier.VerifyAsync(f.Token("password", c => c.Remove("email_verified")));
        Assert.That(result.Identity?.Provider, Is.EqualTo("password"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnsignedEmulatorTokenIsAcceptedOnlyInEmulatorMode(bool emulator)
    {
        using var f = new FirebaseFixture(requireVerifiedEmail: false, emulator: emulator);
        var signed = f.Token("password");
        var parts = signed.Split('.');
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsigned = B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + parts[1] + ".";
        var result = await f.Verifier.VerifyAsync(unsigned);
        Assert.That(result.Identity != null, Is.EqualTo(emulator));
    }

    [TestCase("anonymous")]
    [TestCase("custom")]
    [TestCase("github.com")]
    public async Task OtherProvidersAreRejected(string provider)
    {
        using var f = new FirebaseFixture();
        Assert.That((await f.Verifier.VerifyAsync(f.Token(provider))).Identity, Is.Null);
    }

    [TestCase("none")]
    [TestCase("HS256")]
    public async Task AlgorithmCannotBeConfused(string algorithm)
    {
        using var f = new FirebaseFixture();
        Assert.That((await f.Verifier.VerifyAsync(f.Token(algorithm: algorithm))).Identity, Is.Null);
        Assert.That(f.Requests, Is.Zero);
    }

    [TestCase("")]
    [TestCase("abc.def.ghi")]
    [TestCase("e30.e30.")]
    [TestCase("[].null.signature")]
    public async Task MalformedTokensFailClosed(string token)
    {
        using var f = new FirebaseFixture();
        Assert.That((await f.Verifier.VerifyAsync(token)).Identity, Is.Null);
    }

    [Test]
    public async Task CacheHonorsMaxAgeAgeAndRefreshesAtExpiry()
    {
        using var f = new FirebaseFixture { MaxAge = TimeSpan.FromSeconds(10), Age = TimeSpan.FromSeconds(5) };
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Not.Null);
        f.Clock.Now += TimeSpan.FromSeconds(4);
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Not.Null);
        Assert.That(f.Requests, Is.EqualTo(1));
        f.Clock.Now += TimeSpan.FromSeconds(2);
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Not.Null);
        Assert.That(f.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task NoStoreDoesNotReuseKeyAndExpiredKeyCannotSurviveOutage()
    {
        using var f = new FirebaseFixture { NoStore = true };
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Not.Null);
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Not.Null);
        Assert.That(f.Requests, Is.EqualTo(2));
        f.Offline = true;
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Null);
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Null);
        Assert.That(f.Requests, Is.EqualTo(3), "failed refresh has bounded retry rate");
    }

    [Test]
    public async Task RotationRefreshIsBoundedAndConcurrentReadsShareCache()
    {
        using var f = new FirebaseFixture();
        var tasks = new List<Task>();
        for (var i = 0; i < 20; i++) tasks.Add(f.Verifier.VerifyAsync(f.Token()));
        await Task.WhenAll(tasks);
        Assert.That(f.Requests, Is.EqualTo(1));
        for (var i = 0; i < 20; i++) Assert.That((await f.Verifier.VerifyAsync(f.Token(kid: "unknown-" + i))).Identity, Is.Null);
        Assert.That(f.Requests, Is.EqualTo(1));
        f.Clock.Now += TimeSpan.FromSeconds(31); f.Kid = "rotated";
        Assert.That((await f.Verifier.VerifyAsync(f.Token())).Identity, Is.Not.Null);
        Assert.That(f.Requests, Is.EqualTo(2));
    }

    [Test]
    public async Task TwoMinuteSkewAllowsSmallClockDifference()
    {
        using var f = new FirebaseFixture();
        Assert.That((await f.Verifier.VerifyAsync(f.Token(change: c => c["iat"] = f.Clock.Now.AddSeconds(60).ToUnixTimeSeconds()))).Identity, Is.Not.Null);
    }
}
