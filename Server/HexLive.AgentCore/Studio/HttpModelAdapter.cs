using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentCore.Studio;

/// <summary>Independent provider transports. Responses expose final text, never reasoning blocks.</summary>
public sealed class HttpModelAdapter : IModelAdapter, IModelCatalog, IDisposable
{
    private readonly ModelProviderKind _kind;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _key;
    private readonly string _integrationId;
    public HttpModelAdapter(ModelProviderKind kind, string integrationId,
        Func<CancellationToken, Task<string>> key, HttpMessageHandler? handler = null)
    {
        if (kind == ModelProviderKind.Codex) throw new ArgumentException("CodexRequiresSubscriptionAdapter");
        _kind = kind; _key = key; _integrationId = integrationId;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.BaseAddress = new Uri(kind switch
        {
            ModelProviderKind.OpenAI => "https://api.openai.com/v1/",
            ModelProviderKind.Grok => "https://api.x.ai/v1/",
            ModelProviderKind.Claude => "https://api.anthropic.com/v1/",
            ModelProviderKind.DeepSeek => "https://api.deepseek.com/",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    }

    public async Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        var models = new List<ModelDescriptor>();
        var path = "models";
        do
        {
            using var request = await RequestAsync(HttpMethod.Get, path, cancellationToken);
            using var response = await SendAsync(request, cancellationToken);
            foreach (var item in response.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                // Model catalogs do not universally advertise reasoning settings.
                // Unknown capabilities stay hidden instead of guessing across providers.
                IReadOnlyList<string> modes = _kind == ModelProviderKind.DeepSeek && id.StartsWith("deepseek-v4-", StringComparison.Ordinal)
                    ? new[] { "disabled", "enabled" } : Array.Empty<string>();
                models.Add(new(id, modes));
            }
            if (_kind == ModelProviderKind.Claude && response.RootElement.TryGetProperty("has_more", out var more) && more.GetBoolean())
            {
                if (models.Count >= 2048) throw new InvalidDataException("ModelCatalogTooLarge");
                path = "models?after_id=" + Uri.EscapeDataString(response.RootElement.GetProperty("last_id").GetString()!);
            }
            else break;
        } while (true);
        return models.DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<ModelAnswer> CompleteAsync(ModelSelection selection, ModelRequest input, CancellationToken token)
    {
        if (selection.Provider != _kind || selection.IntegrationId != _integrationId || string.IsNullOrWhiteSpace(selection.ModelId))
            throw new InvalidDataException("ModelIntegrationMismatch");
        var instructions = input.Instructions + "\nReturn only the requested JSON object.\n" + input.Context;
        var payload = new Dictionary<string, object?> { ["model"] = selection.ModelId };
        string endpoint;
        if (_kind == ModelProviderKind.OpenAI)
        {
            endpoint = "responses";
            payload["instructions"] = instructions;
            payload["input"] = input.Input;
            payload["store"] = false;
            payload["max_output_tokens"] = 8192;
            payload["text"] = new { format = new { type = "json_object" } };
            if (selection.Reasoning != null) payload["reasoning"] = new { effort = selection.Reasoning };
        }
        else if (_kind == ModelProviderKind.Claude)
        {
            endpoint = "messages";
            payload["system"] = instructions;
            payload["messages"] = new[] { new { role = "user", content = input.Input } };
            payload["max_tokens"] = 8192;
            if (selection.Reasoning != null) throw new InvalidDataException("UnverifiedReasoningCapability");
        }
        else
        {
            endpoint = "chat/completions";
            payload["messages"] = new[] { new { role = "system", content = instructions }, new { role = "user", content = input.Input } };
            payload["max_tokens"] = 8192;
            payload["response_format"] = new { type = "json_object" };
            if (_kind == ModelProviderKind.Grok && input.ResponseSchema != null)
                payload["response_format"] = new { type = "json_schema", json_schema = new
                { name = "agent_decision", strict = true, schema = JsonSerializer.Deserialize<JsonElement>(input.ResponseSchema) } };
            if (_kind == ModelProviderKind.DeepSeek)
            {
                if (selection.Reasoning is not (null or "enabled" or "disabled"))
                    throw new InvalidDataException("InvalidDeepSeekThinkingMode");
                payload["thinking"] = new { type = selection.Reasoning ?? "disabled" };
            }
            else if (selection.Reasoning != null) throw new InvalidDataException("UnverifiedReasoningCapability");
        }
        using var request = await RequestAsync(HttpMethod.Post, endpoint, token);
        request.Content = JsonContent.Create(payload);
        using var response = await SendAsync(request, token);
        token.ThrowIfCancellationRequested();
        var root = response.RootElement;
        string text;
        if (_kind == ModelProviderKind.OpenAI)
        {
            if (root.GetProperty("status").GetString() != "completed") throw new InvalidDataException("IncompleteModelResponse");
            text = string.Concat(root.GetProperty("output").EnumerateArray()
                .Where(x => x.GetProperty("type").GetString() == "message")
                .SelectMany(x => x.GetProperty("content").EnumerateArray())
                .Where(x => x.GetProperty("type").GetString() == "output_text")
                .Select(x => x.GetProperty("text").GetString()));
        }
        else if (_kind == ModelProviderKind.Claude)
        {
            if (root.GetProperty("stop_reason").GetString() != "end_turn") throw new InvalidDataException("IncompleteModelResponse");
            text = string.Concat(root.GetProperty("content").EnumerateArray()
                .Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("text").GetString()));
        }
        else
        {
            var choice = root.GetProperty("choices")[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop") throw new InvalidDataException("IncompleteModelResponse");
            text = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        if (text.Length > 32768) throw new InvalidDataException("ModelDecisionTooLarge");
        using var decision = JsonDocument.Parse(text);
        if (decision.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ModelDecisionMustBeObject");
        long? incoming = null, outgoing = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            var inputKey = _kind is ModelProviderKind.Grok or ModelProviderKind.DeepSeek ? "prompt_tokens" : "input_tokens";
            var outputKey = _kind is ModelProviderKind.Grok or ModelProviderKind.DeepSeek ? "completion_tokens" : "output_tokens";
            if (usage.TryGetProperty(inputKey, out var i)) incoming = i.GetInt64();
            if (usage.TryGetProperty(outputKey, out var o)) outgoing = o.GetInt64();
        }
        return new(text, incoming, outgoing);
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken token)
    {
        var key = await _key(token);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("MissingProviderCredential");
        var request = new HttpRequestMessage(method, path);
        if (_kind == ModelProviderKind.Claude)
        { request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01"); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // Deliberately discard error bodies, which may echo user input or credentials.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("ModelProviderRequestFailed", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("ProviderResponseTooLarge");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    public void Dispose() => _http.Dispose();
}
