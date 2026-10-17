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
public sealed partial class VoiceBrainService
{
    public BrainOptions Options { get; }
    public SessionTickets Tickets { get; }
    public ElevenLabsAgents ElevenLabs { get; }
    private readonly OpenAiStream _llm;
    private readonly RelationshipStore _bonds;
    private readonly VoiceConversationMemory _memory;
    private readonly ContactClock _contacts;
    private readonly IUsageLedger? _ledger;
    private readonly UsageTariffs? _tariffs;
    private readonly Func<SessionTicket, IWorldGateway> _worldFor;
    private readonly ILogger _log;
    private readonly byte[] _secret;
    private readonly Dictionary<string, string> _voices;
    private readonly Func<string, bool>? _canSpend;
    private readonly Func<SessionTicket, CancellationToken?>? _callLifetime;

    public VoiceBrainService(BrainOptions options, Func<SessionTicket, IWorldGateway> worldFor, ILogger log, HttpClient? http = null,
        Func<string, bool>? canSpend = null, Func<SessionTicket, CancellationToken?>? callLifetime = null)
    {
        Options = options;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _llm = new OpenAiStream(http, options.OpenAiKey);
        ElevenLabs = new ElevenLabsAgents(http, options.ElevenLabsKey);
        Tickets = new SessionTickets(options.TicketKey);
        _bonds = new RelationshipStore(Path.Combine(options.StateDirectory, "bonds"));
        _memory = new VoiceConversationMemory(Path.Combine(options.StateDirectory, "conversations"));
        _contacts = new ContactClock(Path.Combine(options.StateDirectory, "contacts"));
        _ledger = options.UsageDirectory.Length > 0 ? new JsonlUsageLedger(options.UsageDirectory) : null;
        if (options.TariffsPath.Length > 0) _tariffs = UsageTariffs.Load(options.TariffsPath); // explicit bad config fails closed
        else
        {
            using var tariffs = typeof(VoiceBrainService).Assembly.GetManifestResourceStream("HexLive.VoiceBrain.usage-tariffs.json")!;
            _tariffs = JsonSerializer.Deserialize<UsageTariffs>(tariffs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        _worldFor = worldFor;
        _log = log;
        _secret = Encoding.UTF8.GetBytes(options.BrainSecret);
        _voices = LoadVoices(options.VoicesPath);
        _canSpend = canSpend;
        _callLifetime = callLifetime;
    }

    public UsageBalance Balance(string owner, decimal openingUsd) => UsageBalance.Calculate(openingUsd, owner,
        Options.UsageDirectory.Length == 0 ? [] : JsonlUsageLedger.ReadAll(Options.UsageDirectory));

    private static string VoiceEventId(SessionTicket ticket) => "voice-" + ticket.SessionId;

    public void RecordSessionStarted(SessionTicket ticket)
    {
        // A lost browser cannot make an issued session disappear as "free".
        _ledger?.Append(new UsageEvent { EventId = VoiceEventId(ticket), AtUtc = DateTimeOffset.UtcNow,
            Kind = UsageKind.Voice, Provider = "ElevenLabs", Model = "agents", Status = "connecting",
            OwnerUserId = ticket.OwnerUserId, CharacterId = ticket.CharacterId, WorldId = ticket.WorldId,
            NpcId = ticket.NpcId, SessionId = ticket.SessionId });
    }

    /// <summary>Character voice: an explicit request, else the character's voice bank/name, else Masha.</summary>
    public string VoiceFor(string? requested, string? voiceBank, string? name)
    {
        foreach (var key in new[] { requested, voiceBank, name })
            if (key is { Length: > 0 } k && _voices.TryGetValue(k, out var id)) return id;
        return _voices.GetValueOrDefault("Masha", "");
    }

    public object RelationshipView(string characterId, string ownerUserId)
    {
        var value = _bonds.Load(characterId, ownerUserId).Snapshot;
        return new { familiarity = value.Familiarity, trust = value.Trust, affinity = value.Sympathy,
            voiceName = string.IsNullOrWhiteSpace(value.VoiceName) ? "Голос" : value.VoiceName };
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
        var lifetime = _callLifetime?.Invoke(ticket);
        if (_callLifetime != null && lifetime == null) { c.Response.StatusCode = 403; return; }
        using var linked = lifetime.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, lifetime.Value) : null;
        var callToken = linked?.Token ?? c.RequestAborted;
        if (_canSpend != null && !_canSpend(ticket.OwnerUserId)) { c.Response.StatusCode = 402; return; }

        c.Response.ContentType = "text/event-stream";
        c.Response.Headers.CacheControl = "no-cache";
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = body.TryGetProperty("model", out var m) ? m.GetString() ?? Options.Model : Options.Model;
        async Task Chunk(object delta, string? finish)
        {
            var json = JsonSerializer.Serialize(new { id, @object = "chat.completion.chunk", created, model,
                choices = new[] { new { index = 0, delta, finish_reason = finish } } });
            await c.Response.WriteAsync("data: " + json + "\n\n", callToken);
            await c.Response.Body.FlushAsync(callToken);
        }
        var turn = new BrainTurn(Options, _llm, _worldFor(ticket), _bonds, _ledger, _tariffs, _log, _canSpend, _memory, _contacts);
        try
        {
            await Chunk(new { role = "assistant" }, null);
            await turn.RunAsync(body, ticket, piece => Chunk(new { content = piece }, null), callToken);
        }
        catch (OperationCanceledException) when (callToken.IsCancellationRequested) { return; }
        catch (WorldToolRejectedException) when (_callLifetime != null && _callLifetime(ticket) == null) { return; }
        catch (Exception ex)
        {
            _log.LogWarning("voice brain: turn failed: {Type} {Message}", ex.GetType().Name,
                ex is HttpRequestException or WorldToolRejectedException ? ex.Message : "");
            await Chunk(new { content = "Погоди… мысль сбилась." }, null);
        }
        await Chunk(new { }, "stop");
        await c.Response.WriteAsync("data: [DONE]\n\n", callToken);
    }

    /// <summary>
    /// §171.26: her first line for a call that is being set up, or null — off,
    /// timed out, refused or empty. Never throws: the call then opens with the
    /// agent's default line.
    /// </summary>
    public async Task<string?> GreetingAsync(SessionTicket ticket, CancellationToken token)
    {
        if (!Options.Greeting) return null;
        try
        {
            var turn = new BrainTurn(Options, _llm, _worldFor(ticket), _bonds, _ledger, _tariffs, _log, _canSpend, _memory, _contacts);
            return await turn.GreetAsync(ticket, token);
        }
        catch (Exception ex)
        {
            _log.LogWarning("voice greeting skipped: {Type}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>§171.11: the browser module (ES module) — static code, no secrets, same origin as the game page.</summary>
    public static void MapClientScript(IEndpointRouteBuilder app, string path)
    {
        string? script = null;
        app.MapGet(path, () =>
        {
            script ??= ReadClientScript();
            return Results.Text(script, "text/javascript; charset=utf-8");
        });
    }

    public static string ReadClientScript()
    {
        using var stream = typeof(VoiceBrainService).Assembly.GetManifestResourceStream("HexLive.VoiceBrain.hexlive-voice.js")
            ?? throw new InvalidOperationException("hexlive-voice.js is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>§171.4: bill the finished ElevenLabs conversation (they finalise a few seconds after hang-up).</summary>
    public async Task<(double? Seconds, decimal? ProviderCredits)> RecordSessionEndAsync(string conversationId, SessionTicket ticket, CancellationToken token)
    {
        var eventId = VoiceEventId(ticket);
        var previous = Options.UsageDirectory.Length == 0 ? null : JsonlUsageLedger.ReadAll(Options.UsageDirectory)
            .FirstOrDefault(e => e.EventId == eventId);
        if (previous != null && (previous.OwnerUserId != ticket.OwnerUserId || previous.SessionId != ticket.SessionId ||
            previous.ProviderRequestId.Length > 0 && previous.ProviderRequestId != conversationId))
            throw new InvalidDataException("ConversationOwnerMismatch");
        if (previous?.CostSource == UsageCostSource.Reconciled && _memory.For(ticket).HasCompletedImport(ticket.SessionId))
            return (previous.AudioSeconds, previous.ProviderCredits);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        ConversationFacts facts = new(null, null, null, "", "");
        for (var i = 0; i < 6; i++)
        {
            facts = await ElevenLabs.ConversationAsync(conversationId, timeout.Token);
            if (facts.Status is "done" or "failed") break;
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
        // SDK userId is the opaque signed session id, not an account id.
        if (facts.UserId != ticket.SessionId) throw new InvalidDataException("ConversationOwnerMismatch");
        var complete = facts.Status is "done" or "failed";
        try { _memory.For(ticket).Import(ticket.SessionId, facts.Transcript, !complete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _log.LogError("voice conversation archive failed: {Type}", ex.GetType().Name); }
        if (previous?.CostSource == UsageCostSource.Reconciled) return (previous.AudioSeconds, previous.ProviderCredits);
        var usage = new UsageEvent
        {
            EventId = eventId, Revision = complete && facts.CostUsd != null ? 2 : 1,
            AtUtc = DateTimeOffset.UtcNow, Kind = UsageKind.Voice, Provider = "ElevenLabs", Model = "agents",
            Status = complete ? "ok" : "unknown", OwnerUserId = ticket.OwnerUserId, CharacterId = ticket.CharacterId,
            WorldId = ticket.WorldId, NpcId = ticket.NpcId, SessionId = ticket.SessionId,
            AudioSeconds = facts.Seconds, ProviderRequestId = conversationId, ProviderCredits = facts.ProviderCredits,
            Cost = complete ? facts.CostUsd : null, Currency = complete && facts.CostUsd != null ? "USD" : "",
            CostSource = complete && facts.CostUsd != null ? UsageCostSource.Reconciled : UsageCostSource.Unknown,
        };
        _ledger?.Append(usage); // billing acknowledgement requires durable persistence
        return (facts.Seconds, facts.ProviderCredits);
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
