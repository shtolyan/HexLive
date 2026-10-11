using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexLive.VoiceBrain;

public sealed record StreamedToolCall(string Id, string Name, string Arguments);
public sealed record StreamResult(string Text, IReadOnlyList<StreamedToolCall> ToolCalls, long? InputTokens,
    long? OutputTokens, long? CachedTokens, string FinishReason, TimeSpan FirstToken);

/// <summary>Minimal OpenAI Chat Completions streaming client: text deltas are forwarded as they arrive.</summary>
public sealed class OpenAiStream(HttpClient http, string apiKey)
{
    public async Task<StreamResult> RunAsync(JsonObject body, Func<string, Task> onText, CancellationToken token)
    {
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw await Failure(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        long? input = null, output = null, cached = null;
        var finish = "";
        var first = TimeSpan.Zero;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : null;
                output = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt64() : null;
                if (usage.TryGetProperty("prompt_tokens_details", out var det) && det.TryGetProperty("cached_tokens", out var ct))
                    cached = ct.GetInt64();
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) finish = fr.GetString()!;
            if (!choice.TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } piece)
            {
                if (first == TimeSpan.Zero) first = started.Elapsed;
                text.Append(piece);
                await onText(piece);
            }
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                foreach (var tc in tcs.EnumerateArray())
                {
                    if (first == TimeSpan.Zero) first = started.Elapsed;
                    var index = tc.GetProperty("index").GetInt32();
                    if (!calls.TryGetValue(index, out var acc)) acc = ("", "", new StringBuilder());
                    if (tc.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idText) acc.Id = idText;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } name) acc.Name = name;
                        if (fn.TryGetProperty("arguments", out var ar) && ar.GetString() is { } args) acc.Args.Append(args);
                    }
                    calls[index] = acc;
                }
        }
        return new StreamResult(text.ToString(), calls.Values.Select(c => new StreamedToolCall(c.Id, c.Name, c.Args.ToString())).ToList(),
            input, output, cached, finish, first);
    }

    /// <summary>The provider's error message (never contains our key) — the type alone hides fixable request mistakes.</summary>
    private static async Task<HttpRequestException> Failure(HttpResponseMessage response, CancellationToken token)
    {
        var text = await response.Content.ReadAsStringAsync(token);
        var message = "";
        try { using var d = JsonDocument.Parse(text); message = d.RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; }
        catch (Exception) { }
        return new HttpRequestException("OpenAI HTTP " + (int)response.StatusCode + ": " + (message.Length > 300 ? message[..300] : message),
            null, response.StatusCode);
    }

    public async Task<(string Json, long? Input, long? Output)> CompleteJsonAsync(JsonObject body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw await Failure(response, token);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
        long? input = null, output = null;
        if (root.TryGetProperty("usage", out var u))
        { input = u.GetProperty("prompt_tokens").GetInt64(); output = u.GetProperty("completion_tokens").GetInt64(); }
        return (content, input, output);
    }
}
