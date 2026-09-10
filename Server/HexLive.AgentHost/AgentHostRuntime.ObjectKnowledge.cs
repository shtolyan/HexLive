using System.Net;
using System.Text.Json;

namespace HexLive.AgentHost;

public sealed partial class AgentHostRuntime
{
    private const string KnownObjectTool = "query_known_objects";

    // §160.6b: immediately after the first DecideAsync, before
    // TTS, the turn writer/commit, controlVersion changes or action replacement.
    private async Task<CompanionDecision> ResolveObjectKnowledgeAsync(McpClient mcp,
        int npcId, string expectedWorldId, CompanionDecision decision, string trigger,
        string physicalState, string requestContext, string playerText, string[] recent,
        ScheduledTurn? scheduled, CancellationToken cancellationToken)
    {
        if (decision.Action?.Tool != KnownObjectTool) return decision;
        EnsureCurrentTurn(scheduled, cancellationToken);
        JsonElement answer;
        try
        {
            // Reuses the advertised schema and authoritative attachment actor.
            var arguments = await ValidateActionAsync(mcp, npcId, decision.Action, cancellationToken)
                .ConfigureAwait(false);
            EnsureCurrentTurn(scheduled, cancellationToken);
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(8));
            answer = await mcp.CallToolAsync(KnownObjectTool, arguments, readDeadline.Token).ConfigureAwait(false);
            EnsureCurrentTurn(scheduled, cancellationToken);
            if (answer.ValueKind != JsonValueKind.Object ||
                !answer.TryGetProperty("npcId", out var actor) || actor.ValueKind != JsonValueKind.Number ||
                !actor.TryGetInt32(out var actualNpc) || actualNpc != npcId ||
                !answer.TryGetProperty("worldId", out var world) || world.ValueKind != JsonValueKind.String ||
                world.GetString() != expectedWorldId)
                throw new AgentTargetChangedException();
            if (!answer.TryGetProperty("objects", out var rows) || rows.ValueKind != JsonValueKind.Array ||
                rows.GetArrayLength() > 64 || System.Text.Encoding.UTF8.GetByteCount(answer.GetRawText()) > 65536)
                throw new InvalidDataException("InvalidKnowledgeResult");
        }
        // A poisoned MCP client must reach #391's reattach boundary. A fresh
        // credential failure stays terminal; neither is ordinary retrieval data.
        catch (McpSessionExpiredException) { throw; }
        catch (AgentTargetChangedException) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is AgentActionValidationException or McpRequestException or
                                      HttpRequestException or InvalidDataException or JsonException or
                                      OperationCanceledException)
        {
            // Never expose exception messages, HTTP bodies, transcripts or tokens.
            EnsureCurrentTurn(scheduled, cancellationToken);
            var code = ex is OperationCanceledException ? "KnowledgeReadTimeout" : "KnowledgeReadUnavailable";
            answer = JsonSerializer.SerializeToElement(new { error = code, npcId, worldId = expectedWorldId });
        }
        EnsureCurrentTurn(scheduled, cancellationToken);
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(physicalState)
            ?? throw new InvalidDataException("InvalidPhysicalState");
        fields["knownObjectQuery"] = answer;
        var enrichedState = JsonSerializer.Serialize(fields);
        var final = await _providers.DecideAsync(trigger, enrichedState,
            requestContext + "\n" + AgentPromptFiles.Read("object-knowledge.md"), playerText, recent,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentTurn(scheduled, cancellationToken);
        if (final.Action?.Tool == KnownObjectTool)
            throw new AgentActionValidationException("KnowledgeQueryLimitReached");
        return final;
    }
}
