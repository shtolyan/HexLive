using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Llm
{

/// <summary>
/// Host-side HTTP provider for §32.15. It inherits the non-blocking pump from
/// the simulation assembly; this class only performs network I/O on provider
/// workers and converts a compact JSON response into an immutable decision.
/// </summary>
public sealed class LlmHttpControlProvider : QueuedLlmControlProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _backoff;
    private DateTimeOffset _nextAllowedRequestUtc = DateTimeOffset.MinValue;
    private readonly object _backoffGate = new();

    public LlmHttpControlProvider(LlmHostOptions options)
        : this(options, new HttpClient())
    {
    }

    public LlmHttpControlProvider(LlmHostOptions options, HttpClient httpClient)
        : base(options?.MaxQueuedRequests ?? SpecLlmControl.MaxProviderQueuedRequests,
            options?.MaxConcurrentRequests ?? SpecLlmControl.MaxProviderConcurrentRequests)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _endpoint = options.Endpoint ?? throw new ArgumentException(
            "LLM endpoint is required when the HTTP provider is enabled.", nameof(options));
        _model = options.Model;
        _requestTimeout = options.RequestTimeout;
        _backoff = options.Backoff;
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    protected override async Task<LlmDecision> DecideAsync(
        LlmDecisionContext context, CancellationToken cancellationToken)
    {
        var backoffDelay = BackoffDelayUtc(DateTimeOffset.UtcNow);
        if (backoffDelay > TimeSpan.Zero)
        {
            await Task.Delay(backoffDelay, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = new CancellationTokenSource(_requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(ToWireRequest(context), JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_model))
        {
            request.Headers.TryAddWithoutValidation("X-HexLive-LLM-Model", _model);
        }

        using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            NoteFailure(DateTimeOffset.UtcNow);
            throw new HttpRequestException(
                "LLM provider returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }

        var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        var wire = JsonSerializer.Deserialize<LlmDecisionWire>(body, JsonOptions);
        if (wire is null)
        {
            NoteFailure(DateTimeOffset.UtcNow);
            throw new InvalidOperationException("LLM provider returned an empty decision body.");
        }

        return ToDecision(wire);
    }

    public override void Dispose()
    {
        base.Dispose();
        _http.Dispose();
    }

    private TimeSpan BackoffDelayUtc(DateTimeOffset now)
    {
        lock (_backoffGate)
        {
            return _nextAllowedRequestUtc > now ? _nextAllowedRequestUtc - now : TimeSpan.Zero;
        }
    }

    private void NoteFailure(DateTimeOffset now)
    {
        if (_backoff <= TimeSpan.Zero)
        {
            return;
        }

        lock (_backoffGate)
        {
            var next = now + _backoff;
            if (next > _nextAllowedRequestUtc)
            {
                _nextAllowedRequestUtc = next;
            }
        }
    }

    private object ToWireRequest(LlmDecisionContext context) => new
    {
        model = _model,
        npcId = context.NpcId.Value,
        tick = context.Tick,
        position = new { x = context.Position.X, y = context.Position.Y },
        stateSummary = context.StateSummary,
        perceptionSummary = context.PerceptionSummary,
        memorySummary = context.MemorySummary,
        allowedCommands = new[]
        {
            "None", "Stop", "MoveTo", "Interact", "AttackNpc", "AttackMob", "SetManualControl",
        },
    };

    private static LlmDecision ToDecision(LlmDecisionWire wire)
    {
        if (!Enum.TryParse(wire.CommandKind ?? "None", ignoreCase: true, out LlmCommandKind kind))
        {
            throw new InvalidOperationException("Unsupported LLM commandKind: " + wire.CommandKind);
        }

        return new LlmDecision(
            kind,
            targetNpcId: wire.TargetNpcId.HasValue ? new EntityId(wire.TargetNpcId.Value) : null,
            targetObjectId: wire.TargetObjectId.HasValue ? new ObjectId(wire.TargetObjectId.Value) : null,
            targetMobId: wire.TargetMobId,
            targetPosition: wire.TargetPosition is null
                ? null
                : new Float2(wire.TargetPosition.X, wire.TargetPosition.Y),
            interaction: ParseInteraction(wire.Interaction),
            manualControlEnabled: wire.ManualControlEnabled,
            reason: wire.Reason ?? string.Empty);
    }

    private static InteractionType? ParseInteraction(string? interaction)
    {
        return string.IsNullOrWhiteSpace(interaction)
            ? null
            : Enum.Parse<InteractionType>(interaction, ignoreCase: true);
    }

    private sealed class LlmDecisionWire
    {
        public string? CommandKind { get; set; }
        public int? TargetNpcId { get; set; }
        public int? TargetObjectId { get; set; }
        public int? TargetMobId { get; set; }
        public PositionWire? TargetPosition { get; set; }
        public string? Interaction { get; set; }
        public bool? ManualControlEnabled { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class PositionWire
    {
        public float X { get; set; }
        public float Y { get; set; }
    }
}

}
