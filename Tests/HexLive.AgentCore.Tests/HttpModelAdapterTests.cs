using System.Net;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class HttpModelAdapterTests
{
    [TestCase("enabled")]
    [TestCase("disabled")]
    public async Task DeepSeekThinkingIsExplicitAndOnlyFinalContentEscapes(string mode)
    {
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.That(body.RootElement.GetProperty("thinking").GetProperty("type").GetString(), Is.EqualTo(mode));
            Assert.That(request.RequestUri!.Host, Is.EqualTo("api.deepseek.com"));
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"choices":[{"finish_reason":"stop","message":{"reasoning_content":"PRIVATE_REASONING","content":"{\"action\":null}"}}],"usage":{"prompt_tokens":12,"completion_tokens":15}}
                """) };
        });
        using var adapter = new HttpModelAdapter(ModelProviderKind.DeepSeek, "deep", _ => Task.FromResult("test-key"), handler);
        var answer = await adapter.CompleteAsync(new(ModelProviderKind.DeepSeek, "deep", "deepseek-v4-flash", mode),
            new("instructions", "context", "input"), CancellationToken.None);
        Assert.That(answer.DecisionJson, Does.Not.Contain("PRIVATE_REASONING"));
        Assert.That(answer.InputTokens, Is.EqualTo(12));
    }
    [Test]
    public async Task ListingDoesNotGenerateAndErrorsDoNotLeakProviderBody()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/models"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("SECRET_WITH_TRANSCRIPT") });
        });
        using var adapter = new HttpModelAdapter(ModelProviderKind.DeepSeek, "deep", _ => Task.FromResult("test-key"), handler);
        var error = Assert.ThrowsAsync<HttpRequestException>(async () => await adapter.ListAsync(CancellationToken.None));
        Assert.That(error!.ToString(), Does.Not.Contain("SECRET_WITH_TRANSCRIPT"));
        Assert.That(calls, Is.EqualTo(1));
        await Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
