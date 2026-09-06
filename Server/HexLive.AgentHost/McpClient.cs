using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

/// <summary>Small stateful MCP JSON-RPC client; authorization values are never logged.</summary>
public sealed class McpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _sessionId = string.Empty;
    private long _requestId;
    private long _lastSuccessUtcTicks;

    public bool Healthy => _lastSuccessUtcTicks > 0 &&
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastSuccessUtcTicks), DateTimeKind.Utc)
            < TimeSpan.FromSeconds(10);

    public McpClient(AgentProviderOptions options, HttpMessageHandler? handler = null)
    {
        _endpoint = options.McpUri;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.McpToken);
    }

    public async Task<JsonElement> CallToolAsync(
        string name, object arguments, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId.Length == 0)
                await InitializeAsync(cancellationToken).ConfigureAwait(false);

            var response = await SendAsync("tools/call", new
            {
                name,
                arguments
            }, cancellationToken).ConfigureAwait(false);

            var result = response.GetProperty("result");
            if (result.TryGetProperty("isError", out var errorFlag) && errorFlag.GetBoolean())
                throw new McpToolRejectedException(ReadToolText(result));

            var text = ReadToolText(result);
            using var payload = JsonDocument.Parse(text);
            Interlocked.Exchange(ref _lastSuccessUtcTicks, DateTime.UtcNow.Ticks);
            return payload.RootElement.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlySet<string>> ReadToolNamesAsync(CancellationToken cancellationToken)
        => (await ReadToolCatalogAsync(cancellationToken)).GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString() ?? "").ToHashSet(StringComparer.Ordinal);

    public async Task<JsonElement> ReadToolCatalogAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionId.Length == 0) await InitializeAsync(cancellationToken);
            var response = await SendAsync("tools/list", new { }, cancellationToken);
            return response.GetProperty("result").Clone();
        }
        finally { _gate.Release(); }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "hexlive-agent-host", version = "1" }
        }, cancellationToken).ConfigureAwait(false);
        _ = response.GetProperty("result");

        // Complete the MCP handshake. Notifications intentionally have no id.
        await SendNotificationAsync("notifications/initialized", new { }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAsync(
        string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        using var request = BuildRequest(new { jsonrpc = "2.0", id, method, @params = parameters });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        CaptureSession(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetProperty("message").GetString() ?? "MCP error");
        return document.RootElement.Clone();
    }

    private async Task SendNotificationAsync(
        string method, object parameters, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(new { jsonrpc = "2.0", method, @params = parameters });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Accepted)
            response.EnsureSuccessStatusCode();
        CaptureSession(response);
    }

    private HttpRequestMessage BuildRequest(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (_sessionId.Length > 0) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        return request;
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            _sessionId = values.FirstOrDefault() ?? _sessionId;
    }

    private static string ReadToolText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var text)) return text.GetString() ?? "{}";
            }
        }

        throw new InvalidOperationException("MCP tool returned no text content.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

public sealed class McpToolRejectedException : InvalidOperationException
{
    public string ReasonCode { get; }
    public McpToolRejectedException(string payload) : base("MCP tool rejected the request.")
    {
        ReasonCode = "InvalidToolArgumentsOrRejected";
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String &&
                System.Text.RegularExpressions.Regex.IsMatch(reason.GetString() ?? "", @"^[A-Za-z][A-Za-z0-9_]{0,63}$"))
                ReasonCode = reason.GetString()!;
        }
        catch (JsonException) { }
    }
}
