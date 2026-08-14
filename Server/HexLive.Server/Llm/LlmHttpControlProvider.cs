using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// workers and converts the versioned JSON contract in <c>CONTRACT.md</c> into
/// an immutable decision.
/// </summary>
public sealed class LlmHttpControlProvider : QueuedLlmControlProvider
{
    public const int ContractVersion = 1;
    public const int MaxResponseBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly HttpClient _http;
    private readonly bool _disposeHttpClient;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _backoff;
    private DateTimeOffset _nextAllowedRequestUtc = DateTimeOffset.MinValue;
    private readonly object _backoffGate = new();

    public LlmHttpControlProvider(LlmHostOptions options)
        : this(options, new HttpClient(), disposeHttpClient: true)
    {
    }

    /// <summary>
    /// Uses a caller-owned client. This is primarily for tests and hosts that
    /// deliberately share an <see cref="HttpMessageHandler"/> pool.
    /// </summary>
    public LlmHttpControlProvider(LlmHostOptions options, HttpClient httpClient)
        : this(options, httpClient, disposeHttpClient: false)
    {
    }

    private LlmHttpControlProvider(
        LlmHostOptions options,
        HttpClient httpClient,
        bool disposeHttpClient)
        : base(options?.MaxQueuedRequests ?? SpecLlmControl.MaxProviderQueuedRequests,
            options?.MaxConcurrentRequests ?? SpecLlmControl.MaxProviderConcurrentRequests)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        options.Validate();

        _endpoint = options.Endpoint ?? throw new ArgumentException(
            "LLM endpoint is required when the HTTP provider is enabled.", nameof(options));
        _apiKey = options.ApiKey;
        _model = options.Model;
        _requestTimeout = options.RequestTimeout;
        _backoff = options.Backoff;
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeHttpClient = disposeHttpClient;
    }

    protected override async Task<LlmDecision> DecideAsync(
        LlmDecisionContext context, CancellationToken cancellationToken)
    {
        try
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

            request.Headers.TryAddWithoutValidation(
                "X-HexLive-LLM-Contract-Version",
                ContractVersion.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            if (!string.IsNullOrWhiteSpace(_model))
            {
                request.Headers.TryAddWithoutValidation("X-HexLive-LLM-Model", _model);
            }

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    "LLM provider returned HTTP " +
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            ValidateContentType(response.Content.Headers.ContentType);
            var body = await ReadResponseBodyAsync(response.Content, linked.Token)
                .ConfigureAwait(false);
            var wire = JsonSerializer.Deserialize<LlmDecisionWire>(body, JsonOptions);
            if (wire is null)
            {
                throw new InvalidOperationException("LLM provider returned an empty decision body.");
            }

            return ToDecision(wire);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation belongs to the request/system lifetime, not endpoint
            // health, so it must not throttle the next unrelated request.
            throw;
        }
        catch
        {
            NoteFailure(DateTimeOffset.UtcNow);
            throw;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        if (_disposeHttpClient)
        {
            _http.Dispose();
        }
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

    private LlmRequestWire ToWireRequest(LlmDecisionContext context) => new()
    {
        ContractVersion = ContractVersion,
        Model = _model,
        NpcId = context.NpcId.Value,
        Tick = context.Tick,
        Position = new PositionRequestWire
        {
            X = context.Position.X,
            Y = context.Position.Y,
        },
        StateSummary = context.StateSummary,
        PerceptionSummary = context.PerceptionSummary,
        MemorySummary = context.MemorySummary,
        AllowedCommands = new[]
        {
            "None", "Stop", "MoveTo", "Interact", "AttackNpc", "AttackMob", "SetManualControl",
        },
    };

    private static LlmDecision ToDecision(LlmDecisionWire wire)
    {
        if (wire.ContractVersion != ContractVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported LLM response contractVersion: {wire.ContractVersion}.");
        }

        if (!Enum.TryParse(wire.CommandKind, ignoreCase: false, out LlmCommandKind kind) ||
            !Enum.IsDefined(typeof(LlmCommandKind), kind))
        {
            throw new InvalidOperationException("Unsupported LLM commandKind: " + wire.CommandKind);
        }

        Float2? targetPosition = null;
        if (wire.TargetPosition is not null)
        {
            if (!wire.TargetPosition.X.HasValue || !wire.TargetPosition.Y.HasValue ||
                !float.IsFinite(wire.TargetPosition.X.Value) ||
                !float.IsFinite(wire.TargetPosition.Y.Value))
            {
                throw new InvalidOperationException(
                    "LLM targetPosition requires finite x and y numbers.");
            }

            targetPosition = new Float2(
                wire.TargetPosition.X.Value,
                wire.TargetPosition.Y.Value);
        }

        return new LlmDecision(
            kind,
            targetNpcId: wire.TargetNpcId.HasValue ? new EntityId(wire.TargetNpcId.Value) : null,
            targetObjectId: wire.TargetObjectId.HasValue ? new ObjectId(wire.TargetObjectId.Value) : null,
            targetMobId: wire.TargetMobId,
            targetPosition: targetPosition,
            interaction: ParseInteraction(wire.Interaction),
            manualControlEnabled: wire.ManualControlEnabled,
            reason: wire.Reason ?? string.Empty);
    }

    private static InteractionType? ParseInteraction(string? interaction)
    {
        if (string.IsNullOrWhiteSpace(interaction))
        {
            return null;
        }

        if (!Enum.TryParse(interaction, ignoreCase: false, out InteractionType parsed) ||
            !Enum.IsDefined(typeof(InteractionType), parsed))
        {
            throw new InvalidOperationException("Unsupported LLM interaction: " + interaction);
        }

        return parsed;
    }

    private static void ValidateContentType(MediaTypeHeaderValue? contentType)
    {
        if (!string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LLM provider response Content-Type must be application/json.");
        }

        var charset = contentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset) &&
            !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LLM provider response charset must be UTF-8 when specified.");
        }
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidOperationException(
                $"LLM provider response exceeds {MaxResponseBytes} bytes.");
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: MaxResponseBytes);
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException(
                    $"LLM provider response exceeds {MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private sealed class LlmRequestWire
    {
        public int ContractVersion { get; set; }
        public string Model { get; set; } = string.Empty;
        public int NpcId { get; set; }
        public int Tick { get; set; }
        public PositionRequestWire Position { get; set; } = new();
        public string StateSummary { get; set; } = string.Empty;
        public string PerceptionSummary { get; set; } = string.Empty;
        public string MemorySummary { get; set; } = string.Empty;
        public string[] AllowedCommands { get; set; } = Array.Empty<string>();
    }

    private sealed class PositionRequestWire
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    private sealed class LlmDecisionWire
    {
        [JsonRequired]
        public int ContractVersion { get; set; }

        [JsonRequired]
        public string CommandKind { get; set; } = string.Empty;

        public int? TargetNpcId { get; set; }
        public int? TargetObjectId { get; set; }
        public int? TargetMobId { get; set; }
        public PositionResponseWire? TargetPosition { get; set; }
        public string? Interaction { get; set; }
        public bool? ManualControlEnabled { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class PositionResponseWire
    {
        [JsonRequired]
        public float? X { get; set; }

        [JsonRequired]
        public float? Y { get; set; }
    }
}

}
