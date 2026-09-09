using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.AgentHost;

public sealed partial class AgentHostRuntime
{
    private string SpeakerKey(string senderId) => SpeakerKeyFor(_options.McpUri, senderId);

    public static string SpeakerKeyFor(Uri endpoint, string senderId)
    {
        // Different deployments can share a host but use different MCP paths.
        var server = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server))).ToLowerInvariant() +
            ":" + (Guid.TryParseExact(senderId, "N", out var id) ? id.ToString("N") : "unidentified");
    }

    public static string SenderOf(JsonElement inbox) => inbox.TryGetProperty("messages", out var rows) &&
        rows.GetArrayLength() > 0 && rows[0].TryGetProperty("senderId", out var sender) ? sender.GetString() ?? "" : "";

    public static string[] MessageIdsOf(JsonElement inbox) => inbox.GetProperty("messages").EnumerateArray()
        .Select(m => m.GetProperty("messageId").GetString() ?? "").ToArray();

    // Take a contiguous group only. Advancing its watermark cannot consume another person's message.
    public static JsonElement FirstSpeakerInbox(JsonElement inbox)
    {
        var sender = SenderOf(inbox);
        var rows = inbox.GetProperty("messages").EnumerateArray().TakeWhile(m =>
            (m.TryGetProperty("senderId", out var s) ? s.GetString() ?? "" : "") == sender).ToArray();
        return JsonSerializer.SerializeToElement(new {
            messages = rows,
            watermark = rows.Length > 0 ? rows[^1].GetProperty("seq").GetInt64() :
                inbox.TryGetProperty("watermark", out var w) ? w.GetInt64() : 0
        });
    }

    private async Task<JsonElement> RemoveProcessedMessagesAsync(JsonElement inbox, CancellationToken token)
    {
        var archive = await _memory.SnapshotAsync(token);
        var rows = inbox.GetProperty("messages").EnumerateArray().Where(m => {
            var sender = m.TryGetProperty("senderId", out var s) ? s.GetString() ?? "" : "";
            return !archive.Speakers.TryGetValue(SpeakerKey(sender), out var saved) ||
                !saved.AppliedMessageIds.Contains(m.GetProperty("messageId").GetString() ?? "");
        }).ToArray();
        return JsonSerializer.SerializeToElement(new { messages = rows,
            watermark = inbox.TryGetProperty("watermark", out var w) ? w.GetInt64() : 0 });
    }

    private static async Task<bool> AcknowledgeInboxSafelyAsync(McpClient mcp, string attachmentId,
        long throughSeq, CancellationToken token)
    {
        try
        {
            await mcp.CallToolAsync("ack_agent_inbox", new { attachmentId, throughSeq }, token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            // Keep the attachment/inbox: the durable message IDs suppress a repeated model turn.
            return false;
        }
    }

    private async Task ObserveSpeakersAsync(JsonElement attachment, JsonElement clock, int npcId, CancellationToken token)
    {
        if (!attachment.TryGetProperty("presentSpeakerIds", out var present)) return;
        var world = await _memory.BindHexLiveWorldAsync(clock, npcId, _options.WorldId, token);
        var archive = await _memory.SnapshotAsync(token);
        var serverPrefix = SpeakerKey("").Split(':')[0] + ":";
        if (_activeSpeakerKey.Length == 0)
            _activeSpeakerKey = archive.PrimarySpeakerKey?.StartsWith(serverPrefix, StringComparison.Ordinal) == true
                ? archive.PrimarySpeakerKey : SpeakerKey("");
        var online = present.EnumerateArray().Select(p => SpeakerKey(p.GetString() ?? "")).ToHashSet(StringComparer.Ordinal);
        foreach (var key in archive.Speakers.Keys.Where(k => k.StartsWith(serverPrefix, StringComparison.Ordinal)).Union(online))
            await _memory.ObserveSpeakerPresenceAsync(key, online.Contains(key), world, token);
    }
}
