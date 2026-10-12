using System.Net;
using HexLive.AgentHost;
using HexLive.VoiceBrain;

// §171.7 standalone prototype host: the brain reaches a game server over MCP (admin token)
// and hands out sessions to the local test page only. HexLive.Server hosts the same
// service in-process for real players (§171.8).
var options = BrainOptions.FromEnvironment();
var mcpUri = new Uri(Environment.GetEnvironmentVariable("HEXLIVE_MCP_URI") ?? throw new InvalidOperationException("Missing HEXLIVE_MCP_URI"));
var mcpToken = Environment.GetEnvironmentVariable("HEXLIVE_MCP_TOKEN") ?? throw new InvalidOperationException("Missing HEXLIVE_MCP_TOKEN");
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
var app = builder.Build();
var log = app.Logger;
app.Use(async (c, next) =>
{
    await next();
    if (c.Request.Path != "/health") log.LogInformation("http {Method} {Path} -> {Status}", c.Request.Method, c.Request.Path, c.Response.StatusCode);
});

var mcp = new McpClient(new AgentProviderOptions
{
    McpUri = mcpUri, McpToken = mcpToken,
    XaiKey = "", ElevenLabsKey = "", XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "",
});
var world = new McpWorldGateway(mcp);
var voice = new VoiceBrainService(options, _ => world, log);
voice.MapCompletions(app);
VoiceBrainService.MapClientScript(app, "/api/voice/v1/client.js");

static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
    && !c.Request.Headers.ContainsKey("CF-Connecting-IP") && !c.Request.Headers.ContainsKey("X-Forwarded-For");

app.MapGet("/voice/test", (HttpContext c) => !IsLoopback(c) ? Results.NotFound()
    : Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "voice-test.html"), "text/html; charset=utf-8"));

app.MapPost("/voice/session", async (HttpContext c, SessionRequest request) =>
{
    if (!IsLoopback(c)) return Results.NotFound();
    if (options.AgentId.Length == 0) return Results.Problem("VOICEBRAIN_AGENT_ID is not set");
    var describe = await world.CallAsync("describe_colonist", new { npcId = request.NpcId }, c.RequestAborted);
    var name = CharacterView.Name(describe);
    var status = await world.CallAsync("world_status", new { }, c.RequestAborted);
    var worldId = status.TryGetProperty("worldId", out var w) ? w.GetString() ?? "" : "";
    var ticket = voice.Tickets.Mint("local-dev", worldId + ":" + request.NpcId, worldId, request.NpcId);
    var token = await voice.ElevenLabs.ConversationTokenAsync(options.AgentId, c.RequestAborted);
    var voiceId = voice.VoiceFor(request.Voice, null, name);
    return Results.Json(new { conversationToken = token, ticket, voiceId, name });
});

app.MapPost("/voice/session/end", async (HttpContext c, SessionEnd request) =>
{
    if (!IsLoopback(c) || !voice.Tickets.TryVerify(request.Ticket, out var ticket)) return Results.NotFound();
    var (seconds, credits) = await voice.RecordSessionEndAsync(request.ConversationId, ticket, c.RequestAborted);
    return Results.Json(new { seconds, providerCredits = credits });
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.Run();

public sealed record SessionRequest(int NpcId, string? Voice);
public sealed record SessionEnd(string ConversationId, string Ticket);
