using System.Diagnostics;
using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

/// <summary>Official stdio app-server model/list. Never starts a thread or model turn.</summary>
public sealed class CodexModelCatalog(string executable) : IModelCatalog
{
    public async Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await CodexDecisionRunner.CheckLoginAsync(executable, token);
        var start = CodexDecisionRunner.CreateStartInfo(executable, Path.GetTempPath());
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("CodexStartFailed");
        using var stop = token.Register(() => Kill(process));
        var drain = DrainAsync(process.StandardError);
        try
        {
            await Send(new { method = "initialize", id = 1, @params = new { clientInfo = new { name = "hexlive_agent_studio", version = "1" } } });
            _ = await Receive(1);
            await Send(new { method = "initialized", @params = new { } });
            var models = new List<ModelDescriptor>();
            string? cursor = null;
            var requestId = 2;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                await Send(new { method = "model/list", id = requestId, @params = new { limit = 100, includeHidden = false, cursor } });
                var result = await Receive(requestId++);
                models.AddRange(ParsePage(result));
                if (models.Count > 2048) throw new InvalidDataException("CodexCatalogTooLarge");
                cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (cursor != null && !cursors.Add(cursor)) throw new InvalidDataException("CodexCatalogCursorLoop");
            } while (cursor != null);
            return models.DistinctBy(x => x.Id).ToArray();
        }
        finally
        {
            process.StandardInput.Close(); Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await drain;
        }

        async Task Send(object payload)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        async Task<JsonElement> Receive(int id)
        {
            for (var count = 0; count < 256; count++)
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line == null) throw new IOException("CodexCatalogClosed");
                if (line.Length > 1024 * 1024) throw new InvalidDataException("CodexCatalogResponseTooLarge");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var n) || n != id) continue;
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("CodexCatalogRejected");
                return root.GetProperty("result").Clone();
            }
            throw new InvalidDataException("CodexCatalogProtocolLimit");
        }
    }
    public static IReadOnlyList<ModelDescriptor> ParsePage(JsonElement result) =>
        result.GetProperty("data").EnumerateArray()
            .Where(x => !x.TryGetProperty("hidden", out var hidden) || !hidden.GetBoolean())
            .Select(x => new ModelDescriptor(x.GetProperty("model").GetString()!,
                x.GetProperty("supportedReasoningEfforts").EnumerateArray()
                    .Select(e => e.GetProperty("reasoningEffort").GetString()!).ToArray())).ToArray();
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0) { }
    }
}
