using System.Net;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class VoiceCatalogTests
{
    [Test] public async Task IndependentKeysReturnIndependentVoicesWithoutSynthesis()
    {
        using var handler = new Handler(request =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/v2/voices"));
            var masha = request.Headers.GetValues("xi-api-key").Single() == "masha-key";
            return new(HttpStatusCode.OK) { Content = new StringContent(masha
                ? "{\"voices\":[{\"voice_id\":\"masha\",\"name\":\"Masha\"}],\"has_more\":false}"
                : "{\"voices\":[{\"voice_id\":\"nika\",\"name\":\"Nika\"}],\"has_more\":false}") };
        });
        using var masha = new ElevenLabsVoiceCatalog(_ => Task.FromResult("masha-key"), handler);
        using var nika = new ElevenLabsVoiceCatalog(_ => Task.FromResult("nika-key"), handler);
        var results = await Task.WhenAll(masha.ListAsync(CancellationToken.None), nika.ListAsync(CancellationToken.None));
        Assert.That(results[0].Single().Id, Is.EqualTo("masha"));
        Assert.That(results[1].Single().Id, Is.EqualTo("nika"));
    }
    [Test] public void RepeatedCursorIsRejectedInsteadOfLooping()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(
            "{\"voices\":[],\"has_more\":true,\"next_page_token\":\"same\"}") });
        using var catalog = new ElevenLabsVoiceCatalog(_ => Task.FromResult("key"), handler);
        Assert.ThrowsAsync<InvalidDataException>(async () => await catalog.ListAsync(CancellationToken.None));
        Assert.That(handler.Calls, Is.EqualTo(2));
    }
    [Test] public void ProviderErrorsNeverExposeResponseBody()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Unauthorized)
            { Content = new StringContent("SECRET_AND_PRIVATE_TEXT") });
        using var catalog = new ElevenLabsVoiceCatalog(_ => Task.FromResult("key"), handler);
        var error = Assert.ThrowsAsync<HttpRequestException>(async () => await catalog.ListAsync(CancellationToken.None));
        Assert.That(error!.ToString(), Does.Not.Contain("SECRET_AND_PRIVATE_TEXT"));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(respond(request)); }
    }
}
