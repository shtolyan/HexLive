using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.7/§171.8: everything one process needs to serve voice — the ElevenLabs
/// "custom LLM" endpoint, session tickets, voices, usage — independent of WHO hosts
/// it. The world is reached through <see cref="IWorldGateway"/> chosen per ticket.
/// </summary>
public sealed class VoiceBrainService
{
    public BrainOptions Options { get; }
    public SessionTickets Tickets { get; }
    public ElevenLabsAgents ElevenLabs { get; }
    private readonly OpenAiStream _llm;
    private readonly RelationshipStore _bonds;
    private readonly IUsageLedger? _ledger;
    private readonly UsageTariffs? _tariffs;
    private readonly Func<SessionTicket, IWorldGateway> _worldFor;
    private readonly ILogger _log;
    private readonly byte[] _secret;
    private readonly Dictionary<string, string> _voices;

    public VoiceBrainService(BrainOptions options, Func<SessionTicket, IWorldGateway> worldFor, ILogger log, HttpClient? http = null)
    {
        Options = options;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _llm = new OpenAiStream(http, options.OpenAiKey);
        ElevenLabs = new ElevenLabsAgents(http, options.ElevenLabsKey);
        Tickets = new SessionTickets(options.TicketKey);
        _bonds = new RelationshipStore(Path.Combine(options.StateDirectory, "bonds"));
        _ledger = options.UsageDirectory.Length > 0 ? new JsonlUsageLedger(options.UsageDirectory) : null;
        _tariffs = options.TariffsPath.Length > 0 && File.Exists(options.TariffsPath) ? UsageTariffs.Load(options.TariffsPath) : null;
        _worldFor = worldFor;
        _log = log;
        _secret = Encoding.UTF8.GetBytes(options.BrainSecret);
        _voices = LoadVoices(options.VoicesPath);
    }

    /// <summary>Character voice: an explicit request, else the character's voice bank/name, else Masha.</summary>
    public string VoiceFor(string? requested, string? voiceBank, string? name)
    {
        foreach (var key in new[] { requested, voiceBank, name })
            if (key is { Length: > 0 } k && _voices.TryGetValue(k, out var id)) return id;
        return _voices.GetValueOrDefault("Masha", "");
    }

    /// <summary>The OpenAI-compatible streaming endpoint ElevenLabs calls (base URL …/v1; it appends /chat/completions).</summary>
    public void MapCompletions(IEndpointRouteBuilder app, string path = "/v1/chat/completions") =>
        app.MapPost(path, HandleCompletionAsync);

    private async Task HandleCompletionAsync(HttpContext c)
    {
        var auth = c.Request.Headers.Authorization.ToString();
        var presented = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(auth[7..]) : [];
        if (!CryptographicOperations.FixedTimeEquals(presented, _secret))
        {
            _log.LogWarning("voice brain: request rejected, auth header {Kind}", auth.Length == 0 ? "missing" : auth.Split(' ')[0]);
            c.Response.StatusCode = 401; return;
        }
        using var doc = await JsonDocument.ParseAsync(c.Request.Body, cancellationToken: c.RequestAborted);
        var body = doc.RootElement;
        var ticketText = body.TryGetProperty("elevenlabs_extra_body", out var extra) && extra.TryGetProperty("ticket", out var t) ? t.GetString() : null;
        if (!Tickets.TryVerify(ticketText, out var ticket))
        {
            _log.LogWarning("voice brain: request rejected, ticket {State}", ticketText == null ? "missing" : "invalid");
            c.Response.StatusCode = 403; return;
        }
        BrainTurn.CurrentTicket.Value = ticket;

        c.Response.ContentType = "text/event-stream";
        c.Response.Headers.CacheControl = "no-cache";
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = body.TryGetProperty("model", out var m) ? m.GetString() ?? Options.Model : Options.Model;
        async Task Chunk(object delta, string? finish)
        {
            var json = JsonSerializer.Serialize(new { id, @object = "chat.completion.chunk", created, model,
                choices = new[] { new { index = 0, delta, finish_reason = finish } } });
            await c.Response.WriteAsync("data: " + json + "\n\n", c.RequestAborted);
            await c.Response.Body.FlushAsync(c.RequestAborted);
        }
        await Chunk(new { role = "assistant" }, null);
        var turn = new BrainTurn(Options, _llm, _worldFor(ticket), _bonds, _ledger, _tariffs, _log);
        try { await turn.RunAsync(body, ticket, piece => Chunk(new { content = piece }, null), c.RequestAborted); }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { return; } // barge-in
        catch (Exception ex)
        {
            _log.LogWarning("voice brain: turn failed: {Type} {Message}", ex.GetType().Name,
                ex is HttpRequestException or WorldToolRejectedException ? ex.Message : "");
            await Chunk(new { content = "Погоди… мысль сбилась." }, null);
        }
        await Chunk(new { }, "stop");
        await c.Response.WriteAsync("data: [DONE]\n\n", c.RequestAborted);
    }

    /// <summary>§171.4: bill the finished ElevenLabs conversation (they finalise a few seconds after hang-up).</summary>
    public async Task<(double? Seconds, decimal? ProviderCredits)> RecordSessionEndAsync(string conversationId, SessionTicket ticket, CancellationToken token)
    {
        (double? Seconds, decimal? Cost, string Status) facts = (null, null, "");
        for (var i = 0; i < 6 && facts.Seconds == null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            facts = await ElevenLabs.ConversationAsync(conversationId, token);
        }
        var usage = new UsageEvent
        {
            AtUtc = DateTimeOffset.UtcNow, Kind = UsageKind.Voice, Provider = "ElevenLabs", Model = "agents",
            Status = facts.Seconds == null ? "unknown" : "ok", OwnerUserId = ticket.OwnerUserId, CharacterId = ticket.CharacterId,
            WorldId = ticket.WorldId, NpcId = ticket.NpcId, SessionId = ticket.SessionId,
            AudioSeconds = facts.Seconds, ProviderRequestId = conversationId,
        };
        try { _ledger?.Append(_tariffs?.Price(usage) ?? usage); } catch (Exception ex) { _log.LogWarning("usage ledger: {Type}", ex.GetType().Name); }
        return (facts.Seconds, facts.Cost);
    }

    private static Dictionary<string, string> LoadVoices(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path.Length == 0 || !File.Exists(path)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("characters", out var characters) && characters.ValueKind == JsonValueKind.Object)
            foreach (var c in characters.EnumerateObject())
                if (c.Value.TryGetProperty("voice_id", out var id) && id.GetString() is { Length: > 0 } v) map[c.Name] = v;
        return map;
    }
}
