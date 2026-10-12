using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.VoiceBrain;

/// <summary>The world refused a tool call; the reason goes back to the model, never a fake success.</summary>
public sealed class WorldToolRejectedException(string reason) : Exception(reason);

/// <summary>
/// §171.7/§171.8: how the brain reads and commands the world. Inside HexLive.Server
/// it calls the MCP tool table in-process as the player; the prototype host goes over
/// MCP. Either way every action takes the ordinary command path and its checks.
/// </summary>
public interface IWorldGateway
{
    Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token);
    Task RecordConversationAsync(int npcId, string turnId, CancellationToken token) => Task.CompletedTask;
}

/// <summary>Prototype host: the game server's /mcp endpoint.</summary>
public sealed class McpWorldGateway(McpClient mcp) : IWorldGateway
{
    public async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token)
    {
        try { return await mcp.CallToolAsync(tool, arguments, token); }
        catch (McpToolRejectedException ex) { throw new WorldToolRejectedException(ex.Message); }
    }
}
