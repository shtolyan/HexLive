using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Llm;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Llm
{

public sealed class LlmHttpControlProviderTests
{
    [Test]
    public void RequestAndResponse_UseTheLockedVersionOneContract()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            "{\"contractVersion\":1,\"commandKind\":\"MoveTo\"," +
            "\"targetPosition\":{\"x\":3.5,\"y\":-2},\"reason\":\"fixture\"}"));
        using var http = new HttpClient(handler);
        using var provider = new LlmHttpControlProvider(Options(apiKey: "fixture-secret"), http);
        var context = new LlmDecisionContext(
            new EntityId(7),
            tick: 42,
            new Float2(1.5f, -2f),
            stateSummary: "state",
            perceptionSummary: "perception",
            memorySummary: "memory");

        Assert.That(provider.TryRequest(Request(context)), Is.True);
        var result = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Completed));
            Assert.That(result.Decision.CommandKind, Is.EqualTo(LlmCommandKind.MoveTo));
            Assert.That(result.Decision.TargetPosition, Is.EqualTo(new Float2(3.5f, -2f)));
            Assert.That(result.Decision.Reason, Is.EqualTo("fixture"));
            Assert.That(handler.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.RequestUri, Is.EqualTo(new Uri("http://127.0.0.1:11434/decision")));
            Assert.That(handler.ContentType, Is.EqualTo("application/json; charset=utf-8"));
            Assert.That(handler.AuthorizationScheme, Is.EqualTo("Bearer"));
            Assert.That(handler.AuthorizationParameter, Is.EqualTo("fixture-secret"));
            Assert.That(handler.ContractVersionHeader, Is.EqualTo("1"));
            Assert.That(handler.ModelHeader, Is.EqualTo("fixture-model"));
        });

        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[]
            {
                "contractVersion", "model", "npcId", "tick", "position",
                "stateSummary", "perceptionSummary", "memorySummary", "allowedCommands",
            }));
            Assert.That(root.GetProperty("contractVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("model").GetString(), Is.EqualTo("fixture-model"));
            Assert.That(root.GetProperty("npcId").GetInt32(), Is.EqualTo(7));
            Assert.That(root.GetProperty("tick").GetInt32(), Is.EqualTo(42));
            Assert.That(root.GetProperty("position").GetProperty("x").GetSingle(), Is.EqualTo(1.5f));
            Assert.That(root.GetProperty("position").GetProperty("y").GetSingle(), Is.EqualTo(-2f));
            Assert.That(root.GetProperty("stateSummary").GetString(), Is.EqualTo("state"));
            Assert.That(root.GetProperty("perceptionSummary").GetString(), Is.EqualTo("perception"));
            Assert.That(root.GetProperty("memorySummary").GetString(), Is.EqualTo("memory"));
            Assert.That(root.GetProperty("allowedCommands").EnumerateArray()
                    .Select(value => value.GetString()),
                Is.EqualTo(new[]
                {
                    "None", "Stop", "MoveTo", "Interact", "AttackNpc", "AttackMob",
                    "SetManualControl",
                }));
        });
    }

    [Test]
    public async Task CallerOwnedHttpClient_RemainsUsableAfterProviderDisposal()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            "{\"contractVersion\":1,\"commandKind\":\"None\"}"));
        using var http = new HttpClient(handler);
        var provider = new LlmHttpControlProvider(Options(), http);

        provider.Dispose();

        using var response = await http.GetAsync(
            "http://127.0.0.1:11434/after-provider-disposal");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase(
        "{\"commandKind\":\"None\"}",
        "response-json",
        TestName = "Response requires contractVersion")]
    [TestCase(
        "{\"contractVersion\":2,\"commandKind\":\"None\"}",
        "response-contract",
        TestName = "Response rejects an unsupported contractVersion")]
    [TestCase(
        "{\"contractVersion\":1,\"commandkind\":\"None\"}",
        "response-json",
        TestName = "Response property names are case-sensitive")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"none\"}",
        "response-contract",
        TestName = "Response enum values are case-sensitive")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"1\"}",
        "response-contract",
        TestName = "Response rejects a numeric command ordinal")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"None, Stop\"}",
        "response-contract",
        TestName = "Response rejects a comma-combined command")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\" None\"}",
        "response-contract",
        TestName = "Response rejects leading command whitespace")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"None \"}",
        "response-contract",
        TestName = "Response rejects trailing command whitespace")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"Interact\",\"interaction\":\"NotAnInteraction\"}",
        "response-contract",
        TestName = "Response rejects an unsupported interaction")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"Interact\",\"interaction\":\"0\"}",
        "response-contract",
        TestName = "Response rejects a numeric interaction ordinal")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"Interact\",\"interaction\":\"Eat, Drink\"}",
        "response-contract",
        TestName = "Response rejects a comma-combined interaction")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"Interact\",\"interaction\":\" Harvest\"}",
        "response-contract",
        TestName = "Response rejects leading interaction whitespace")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"Interact\",\"interaction\":\"Harvest \"}",
        "response-contract",
        TestName = "Response rejects trailing interaction whitespace")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"None\",\"extra\":true}",
        "response-json",
        TestName = "Response rejects unknown fields")]
    [TestCase(
        "{\"contractVersion\":1,\"commandKind\":\"MoveTo\",\"targetPosition\":{\"x\":1}}",
        "response-json",
        TestName = "Response targetPosition requires both coordinates")]
    public void MalformedContractResponse_FailsClosed(
        string body,
        string expectedCategory)
    {
        var warnings = new List<string>();
        var diagnostics = new LlmProviderDiagnostics(warnings.Add);
        using var handler = new RecordingHandler(_ => JsonResponse(body));
        using var http = new HttpClient(handler);
        using var provider = new LlmHttpControlProvider(Options(), http, diagnostics);

        Assert.That(provider.TryRequest(Request()), Is.True);
        var result = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Failed));
            Assert.That(result.Decision, Is.Null);
            Assert.That(result.ErrorType, Is.EqualTo("LlmProviderFailureException"));
            Assert.That(result.ErrorMessage,
                Is.EqualTo($"LLM provider failure category={expectedCategory}."));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain($"category={expectedCategory}"));
            Assert.That(diagnostics.DrainSummary(),
                Is.EqualTo($"[llm] provider failures {expectedCategory}=1"));
        });
    }

    [Test]
    public void NonJsonOrOversizedResponse_FailsBeforeDeserialization()
    {
        using var nonJsonHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"contractVersion\":1,\"commandKind\":\"None\"}",
                Encoding.UTF8,
                "text/plain"),
        });
        using var nonJsonHttp = new HttpClient(nonJsonHandler);
        using var nonJsonProvider = new LlmHttpControlProvider(Options(), nonJsonHttp);

        using var oversizedHandler = new RecordingHandler(_ => JsonResponse(
            "{\"contractVersion\":1,\"commandKind\":\"None\",\"reason\":\"" +
            new string('x', LlmHttpControlProvider.MaxResponseBytes) + "\"}"));
        using var oversizedHttp = new HttpClient(oversizedHandler);
        using var oversizedProvider = new LlmHttpControlProvider(Options(), oversizedHttp);

        Assert.That(nonJsonProvider.TryRequest(Request()), Is.True);
        Assert.That(oversizedProvider.TryRequest(Request()), Is.True);
        var nonJson = WaitForResult(nonJsonProvider);
        var oversized = WaitForResult(oversizedProvider);

        Assert.Multiple(() =>
        {
            Assert.That(nonJson.Status, Is.EqualTo(LlmControlResultStatus.Failed));
            Assert.That(nonJson.ErrorMessage,
                Is.EqualTo("LLM provider failure category=response-media-type."));
            Assert.That(oversized.Status, Is.EqualTo(LlmControlResultStatus.Failed));
            Assert.That(oversized.ErrorMessage,
                Is.EqualTo("LLM provider failure category=response-too-large."));
        });
    }

    [Test]
    public void StreamedOversizedResponseWithoutContentLength_FailsBeforeDeserialization()
    {
        using var content = new UnknownLengthJsonContent(
            "{\"contractVersion\":1,\"commandKind\":\"None\",\"reason\":\"" +
            new string('x', LlmHttpControlProvider.MaxResponseBytes) + "\"}");
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var http = new HttpClient(handler);
        using var provider = new LlmHttpControlProvider(Options(), http);

        Assert.That(content.Headers.ContentLength, Is.Null,
            "The fixture must exercise the streamed response path without a length header.");
        Assert.That(provider.TryRequest(Request()), Is.True);
        var result = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Failed));
            Assert.That(result.Decision, Is.Null);
            Assert.That(result.ErrorMessage,
                Is.EqualTo("LLM provider failure category=response-too-large."));
        });
    }

    [Test]
    public void ResponseAtMaximumSize_IsAccepted()
    {
        const string prefix =
            "{\"contractVersion\":1,\"commandKind\":\"None\",\"reason\":\"";
        const string suffix = "\"}";
        var reasonLength = LlmHttpControlProvider.MaxResponseBytes -
            Encoding.UTF8.GetByteCount(prefix + suffix);
        var body = prefix + new string('x', reasonLength) + suffix;
        using var handler = new RecordingHandler(_ => JsonResponse(body));
        using var http = new HttpClient(handler);
        using var provider = new LlmHttpControlProvider(Options(), http);

        Assert.That(Encoding.UTF8.GetByteCount(body),
            Is.EqualTo(LlmHttpControlProvider.MaxResponseBytes));
        Assert.That(provider.TryRequest(Request()), Is.True);
        var result = WaitForResult(provider);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Completed));
            Assert.That(result.Decision, Is.Not.Null);
            Assert.That(result.Decision!.Reason, Has.Length.EqualTo(reasonLength));
        });
    }

    [TestCase(LlmProviderFailureCategory.Timeout)]
    [TestCase(LlmProviderFailureCategory.Transport)]
    [TestCase(LlmProviderFailureCategory.HttpStatus)]
    [TestCase(LlmProviderFailureCategory.ResponseMediaType)]
    [TestCase(LlmProviderFailureCategory.ResponseTooLarge)]
    [TestCase(LlmProviderFailureCategory.ResponseEncoding)]
    [TestCase(LlmProviderFailureCategory.ResponseJson)]
    [TestCase(LlmProviderFailureCategory.ResponseContract)]
    [TestCase(LlmProviderFailureCategory.Unexpected)]
    public void FailureTaxonomy_IsOperatorVisibleRedactedAndBounded(
        LlmProviderFailureCategory category)
    {
        const string endpointSecret = "endpoint-secret";
        const string apiSecret = "api-secret";
        const string responseSecret = "response-secret";
        var warnings = new List<string>();
        var diagnostics = new LlmProviderDiagnostics(warnings.Add);
        using var handler = new RecordingHandler(_ => FailureResponse(category, responseSecret));
        using var http = new HttpClient(handler);
        using var provider = new LlmHttpControlProvider(
            Options(apiSecret, $"http://127.0.0.1:11434/decision/{endpointSecret}"),
            http,
            diagnostics);

        Assert.That(provider.TryRequest(Request()), Is.True);
        var result = WaitForResult(provider);
        for (var i = 0; i < 100; i++)
        {
            diagnostics.RecordFailure(category);
        }

        var categoryName = DiagnosticCategoryName(category);
        var summary = diagnostics.DrainSummary();
        var diagnosticOutput = string.Join("\n", warnings) + "\n" + summary + "\n" +
            result.ErrorType + "\n" + result.ErrorMessage;

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LlmControlResultStatus.Failed));
            Assert.That(result.ErrorType, Is.EqualTo("LlmProviderFailureException"));
            Assert.That(result.ErrorMessage,
                Is.EqualTo($"LLM provider failure category={categoryName}."));
            Assert.That(warnings, Has.Count.EqualTo(1),
                "Only the first failure in a category may warn immediately.");
            Assert.That(warnings[0].Length,
                Is.LessThanOrEqualTo(LlmProviderDiagnostics.MaxDiagnosticLineLength));
            Assert.That(summary,
                Is.EqualTo($"[llm] provider failures {categoryName}=101"));
            Assert.That(summary!.Length,
                Is.LessThanOrEqualTo(LlmProviderDiagnostics.MaxDiagnosticLineLength));
            Assert.That(diagnostics.DrainSummary(), Is.Null,
                "A drained interval must not emit an empty periodic line.");
            Assert.That(diagnosticOutput, Does.Not.Contain(endpointSecret));
            Assert.That(diagnosticOutput, Does.Not.Contain(apiSecret));
            Assert.That(diagnosticOutput, Does.Not.Contain(responseSecret));
            Assert.That(diagnosticOutput.Length,
                Is.LessThanOrEqualTo(LlmProviderDiagnostics.MaxDiagnosticLineLength * 3));
        });
    }

    private static LlmHostOptions Options(
        string apiKey = "",
        string endpoint = "http://127.0.0.1:11434/decision")
    {
        var options = new LlmHostOptions();
        options.SetEndpoint(endpoint);
        options.SetSelectedNpcIds("7");
        options.SetModel("fixture-model");
        options.SetApiKey(apiKey);
        options.SetBackoffSeconds("0");
        options.Validate();
        return options;
    }

    private static HttpResponseMessage FailureResponse(
        LlmProviderFailureCategory category,
        string responseSecret)
    {
        switch (category)
        {
            case LlmProviderFailureCategory.Timeout:
                throw new OperationCanceledException(responseSecret);
            case LlmProviderFailureCategory.Transport:
                throw new HttpRequestException(responseSecret);
            case LlmProviderFailureCategory.HttpStatus:
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent(responseSecret),
                };
            case LlmProviderFailureCategory.ResponseMediaType:
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseSecret, Encoding.UTF8, "text/plain"),
                };
            case LlmProviderFailureCategory.ResponseTooLarge:
                return JsonResponse(
                    "{\"contractVersion\":1,\"commandKind\":\"None\",\"reason\":\"" +
                    responseSecret + new string('x', LlmHttpControlProvider.MaxResponseBytes) +
                    "\"}");
            case LlmProviderFailureCategory.ResponseEncoding:
                var invalidUtf8 = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 0xff, 0xfe, 0xfd }),
                };
                invalidUtf8.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");
                return invalidUtf8;
            case LlmProviderFailureCategory.ResponseJson:
                return JsonResponse("{\"response-secret\":");
            case LlmProviderFailureCategory.ResponseContract:
                return JsonResponse(
                    "{\"contractVersion\":2,\"commandKind\":\"None\",\"reason\":\"" +
                    responseSecret + "\"}");
            case LlmProviderFailureCategory.Unexpected:
                throw new ApplicationException(responseSecret);
            default:
                throw new ArgumentOutOfRangeException(nameof(category));
        }
    }

    private static string DiagnosticCategoryName(LlmProviderFailureCategory category) =>
        category switch
        {
            LlmProviderFailureCategory.Timeout => "timeout",
            LlmProviderFailureCategory.Transport => "transport",
            LlmProviderFailureCategory.HttpStatus => "http-status",
            LlmProviderFailureCategory.ResponseMediaType => "response-media-type",
            LlmProviderFailureCategory.ResponseTooLarge => "response-too-large",
            LlmProviderFailureCategory.ResponseEncoding => "response-encoding",
            LlmProviderFailureCategory.ResponseJson => "response-json",
            LlmProviderFailureCategory.ResponseContract => "response-contract",
            LlmProviderFailureCategory.Unexpected => "unexpected",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

    private static LlmControlRequest Request(LlmDecisionContext? context = null) => new(
        requestId: 1,
        context ?? new LlmDecisionContext(new EntityId(7), tick: 42, Float2.Zero),
        CancellationToken.None);

    private static LlmControlResult WaitForResult(ILlmControlProvider provider)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (provider.TryDequeueResult(out var result))
            {
                return result;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("Provider did not publish a result within three seconds.");
        throw new InvalidOperationException("Unreachable test failure.");
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _body;

        public UnknownLengthJsonContent(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(_body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? ContentType { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ContractVersionHeader { get; private set; }
        public string? ModelHeader { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ContractVersionHeader = request.Headers.TryGetValues(
                "X-HexLive-LLM-Contract-Version", out var contractValues)
                ? contractValues.Single()
                : null;
            ModelHeader = request.Headers.TryGetValues(
                "X-HexLive-LLM-Model", out var modelValues)
                ? modelValues.Single()
                : null;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _respond(request);
        }
    }
}

}
