using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using HexLive.VoiceBrain;

// §171.7 voice brain prototype. ElevenLabs Agents hears and speaks; this process thinks.
var options = BrainOptions.FromEnvironment();
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
var app = builder.Build();
var log = app.Logger;
// Prototype diagnostics: which paths the provider actually calls (no bodies, no headers).
app.Use(async (c, next) =>
{
    await next();
    if (c.Request.Path != "/health") log.LogInformation("http {Method} {Path} -> {Status}", c.Request.Method, c.Request.Path, c.Response.StatusCode);
});

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var llm = new OpenAiStream(http, options.OpenAiKey);
var elevenLabs = new ElevenLabsAgents(http, options.ElevenLabsKey);
var tickets = new SessionTickets(options.TicketKey);
var bonds = new RelationshipStore(Path.Combine(options.StateDirectory, "bonds"));
IUsageLedger? ledger = options.UsageDirectory.Length > 0 ? new JsonlUsageLedger(options.UsageDirectory) : null;
UsageTariffs? tariffs = options.TariffsPath.Length > 0 && File.Exists(options.TariffsPath) ? UsageTariffs.Load(options.TariffsPath) : null;
var mcp = new McpClient(new AgentProviderOptions
{
    McpUri = options.McpUri, McpToken = options.McpToken,
    XaiKey = "", ElevenLabsKey = "", XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "",
});
var voices = LoadVoices(options.VoicesPath);
var secret = Encoding.UTF8.GetBytes(options.BrainSecret);

static bool IsLoopback(HttpContext c) => c.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
    && !c.Request.Headers.ContainsKey("CF-Connecting-IP") && !c.Request.Headers.ContainsKey("X-Forwarded-For");

// ---- Browser side (prototype: loopback only; Firebase sign-in replaces this) -------------
app.MapGet("/voice/test", (HttpContext c) => !IsLoopback(c) ? Results.NotFound()
    : Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "voice-test.html"), "text/html; charset=utf-8"));

app.MapPost("/voice/session", async (HttpContext c, SessionRequest request) =>
{
    if (!IsLoopback(c)) return Results.NotFound();
    if (options.AgentId.Length == 0) return Results.Problem("VOICEBRAIN_AGENT_ID is not set");
    var describe = await mcp.CallToolAsync("describe_colonist", new { npcId = request.NpcId }, c.RequestAborted);
    var name = CharacterView.Name(describe);
    var world = await mcp.CallToolAsync("world_status", new { }, c.RequestAborted);
    var worldId = world.TryGetProperty("worldId", out var w) ? w.GetString() ?? "" : "";
    var ticket = tickets.Mint("local-dev", worldId + ":" + request.NpcId, worldId, request.NpcId);
    var token = await elevenLabs.ConversationTokenAsync(options.AgentId, c.RequestAborted);
    var voiceId = request.Voice is { Length: > 0 } v && voices.TryGetValue(v, out var id) ? id
        : voices.TryGetValue(name, out var byName) ? byName : voices.GetValueOrDefault("Masha", "");
    log.LogInformation("session npc={Npc} name={Name} voice={Voice}", request.NpcId, name, voiceId.Length > 0 ? voiceId[..4] + "…" : "default");
    return Results.Json(new { conversationToken = token, ticket, voiceId, name });
});

app.MapPost("/voice/session/end", async (HttpContext c, SessionEnd request) =>
{
    if (!IsLoopback(c) || !tickets.TryVerify(request.Ticket, out var ticket)) return Results.NotFound();
    // ElevenLabs finalises a conversation a few seconds after hang-up.
    (double? Seconds, decimal? Cost, string Status) facts = (null, null, "");
    for (var i = 0; i < 6 && facts.Seconds == null; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), c.RequestAborted);
        facts = await elevenLabs.ConversationAsync(request.ConversationId, c.RequestAborted);
    }
    var usage = new UsageEvent
    {
        AtUtc = DateTimeOffset.UtcNow, Kind = UsageKind.Voice, Provider = "ElevenLabs", Model = "agents",
        Status = facts.Seconds == null ? "unknown" : "ok", OwnerUserId = ticket.OwnerUserId, CharacterId = ticket.CharacterId,
        WorldId = ticket.WorldId, NpcId = ticket.NpcId, SessionId = ticket.SessionId,
        AudioSeconds = facts.Seconds, ProviderRequestId = request.ConversationId,
    };
    try { ledger?.Append(tariffs?.Price(usage) ?? usage); } catch (Exception ex) { log.LogWarning("usage ledger: {Type}", ex.GetType().Name); }
    log.LogInformation("session end {Conv} seconds={Seconds} providerCredits={Cost}", request.ConversationId, facts.Seconds, facts.Cost);
    return Results.Json(new { seconds = facts.Seconds, providerCredits = facts.Cost });
});

// ---- ElevenLabs side: OpenAI-compatible streaming endpoint -----------------------------
app.MapPost("/v1/chat/completions", async (HttpContext c) =>
{
    var auth = c.Request.Headers.Authorization.ToString();
    var presented = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(auth[7..]) : [];
    if (!CryptographicOperations.FixedTimeEquals(presented, secret))
    {
        log.LogWarning("llm request rejected: auth header {Kind}", auth.Length == 0 ? "missing" : auth.Split(' ')[0]);
        c.Response.StatusCode = 401; return;
    }
    using var doc = await JsonDocument.ParseAsync(c.Request.Body, cancellationToken: c.RequestAborted);
    var body = doc.RootElement;
    var ticketText = body.TryGetProperty("elevenlabs_extra_body", out var extra) && extra.TryGetProperty("ticket", out var t) ? t.GetString() : null;
    if (!tickets.TryVerify(ticketText, out var ticket))
    {
        log.LogWarning("llm request rejected: ticket {State}", ticketText == null ? "missing" : "invalid");
        c.Response.StatusCode = 403; return;
    }
    BrainTurn.CurrentTicket.Value = ticket;

    c.Response.ContentType = "text/event-stream";
    c.Response.Headers.CacheControl = "no-cache";
    var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var model = body.TryGetProperty("model", out var m) ? m.GetString() ?? options.Model : options.Model;
    async Task Chunk(object delta, string? finish)
    {
        var json = JsonSerializer.Serialize(new { id, @object = "chat.completion.chunk", created, model,
            choices = new[] { new { index = 0, delta, finish_reason = finish } } });
        await c.Response.WriteAsync("data: " + json + "\n\n", c.RequestAborted);
        await c.Response.Body.FlushAsync(c.RequestAborted);
    }
    await Chunk(new { role = "assistant" }, null);
    var turn = new BrainTurn(options, llm, mcp, bonds, ledger, tariffs, log);
    try { await turn.RunAsync(body, ticket, piece => Chunk(new { content = piece }, null), c.RequestAborted); }
    catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { return; } // barge-in
    catch (Exception ex)
    {
        log.LogWarning("turn failed: {Type} {Message}", ex.GetType().Name, ex is HttpRequestException ? ex.Message : "");
        await Chunk(new { content = "Погоди… мысль сбилась." }, null);
    }
    await Chunk(new { }, "stop");
    await c.Response.WriteAsync("data: [DONE]\n\n", c.RequestAborted);
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.Run();

static Dictionary<string, string> LoadVoices(string path)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (path.Length == 0 || !File.Exists(path)) return map;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    void Walk(JsonElement e, string? key)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name is "voice_id" or "voiceId" && p.Value.ValueKind == JsonValueKind.String && key != null) map[key] = p.Value.GetString()!;
                else Walk(p.Value, p.Value.ValueKind == JsonValueKind.Object ? p.Name : key);
            }
        else if (e.ValueKind == JsonValueKind.String && key != null && e.GetString() is { Length: 20 } s && s.All(char.IsLetterOrDigit)) map[key] = s;
    }
    Walk(doc.RootElement, null);
    return map;
}

public sealed record SessionRequest(int NpcId, string? Voice);
public sealed record SessionEnd(string ConversationId, string Ticket);
