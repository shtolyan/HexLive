using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Accounts;

namespace HexLive.Server.Tests.Accounts;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FirebaseFixture : HttpMessageHandler
{
    public const string Project = "hexlive-test";
    public RSA Key { get; } = RSA.Create(2048);
    public TestClock Clock { get; } = new();
    public int Requests { get; private set; }
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan Age { get; set; }
    public bool NoStore { get; set; }
    public bool Offline { get; set; }
    public string Kid { get; set; } = "fixture-key";
    public HttpClient Client { get; }
    public FirebaseTokenVerifier Verifier { get; }
    public FirebaseFixture()
    {
        Client = new HttpClient(this);
        Verifier = new FirebaseTokenVerifier(Project, Client, Clock);
    }
    public string Token(string provider = "google.com", Action<Dictionary<string, object>>? change = null,
        RSA? signer = null, string? kid = null, string algorithm = "RS256")
    {
        var claims = new Dictionary<string, object> {
            ["iss"] = "https://securetoken.google.com/" + Project, ["aud"] = Project,
            ["sub"] = "firebase-user-1", ["iat"] = Clock.Now.ToUnixTimeSeconds(),
            ["exp"] = Clock.Now.AddHours(1).ToUnixTimeSeconds(), ["auth_time"] = Clock.Now.ToUnixTimeSeconds(),
            ["email_verified"] = true, ["email"] = "not-stored@example.test",
            ["firebase"] = new { sign_in_provider = provider }
        };
        change?.Invoke(claims);
        var input = Base64(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, kid = kid ?? Kid })) + "." +
                    Base64(JsonSerializer.SerializeToUtf8Bytes(claims));
        return input + "." + Base64((signer ?? Key).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri != FirebaseTokenVerifier.JwksUrl) throw new InvalidOperationException("Unexpected URL");
        Requests++;
        if (Offline) throw new HttpRequestException("fixture offline");
        var p = Key.ExportParameters(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(new { keys = new[] {
                new { kid = Kid, kty = "RSA", alg = "RS256", use = "sig", n = Base64(p.Modulus!), e = Base64(p.Exponent!) }
            } }))
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = MaxAge, NoStore = NoStore };
        response.Headers.Age = Age;
        return Task.FromResult(response);
    }
    internal static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    protected override void Dispose(bool disposing) { if (disposing) Key.Dispose(); base.Dispose(disposing); }
}
