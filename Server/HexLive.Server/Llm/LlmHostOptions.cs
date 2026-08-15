using System;
using System.Collections.Generic;
using System.Globalization;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Llm
{

/// <summary>
/// Validated, opt-in configuration for the host-side LLM HTTP adapter.
/// Secrets are accepted from the environment only; see <c>CONTRACT.md</c>.
/// </summary>
public sealed class LlmHostOptions
{
    public const string EndpointEnvironmentVariable = "HEXLIVE_LLM_ENDPOINT";
    public const string ApiKeyEnvironmentVariable = "HEXLIVE_LLM_API_KEY";
    public const string ModelEnvironmentVariable = "HEXLIVE_LLM_MODEL";
    public const string NpcsEnvironmentVariable = "HEXLIVE_LLM_NPCS";
    public const string TimeoutEnvironmentVariable = "HEXLIVE_LLM_TIMEOUT_SECONDS";
    public const string BackoffEnvironmentVariable = "HEXLIVE_LLM_BACKOFF_SECONDS";
    public const string MaxQueuedEnvironmentVariable = "HEXLIVE_LLM_MAX_QUEUED_REQUESTS";
    public const string MaxConcurrentEnvironmentVariable = "HEXLIVE_LLM_MAX_CONCURRENT_REQUESTS";

    private const int MaxTimeoutSeconds = 120;
    private const int MaxBackoffSeconds = 300;
    private const int MaxQueuedRequestsLimit = SpecLlmControl.MaxInFlightRequests;
    private const int MaxConcurrentRequestsLimit = SpecLlmControl.MaxInFlightRequests;

    public Uri? Endpoint { get; private set; }
    public string ApiKey { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(12);
    public TimeSpan Backoff { get; private set; } = TimeSpan.FromSeconds(8);
    public int MaxQueuedRequests { get; private set; } = SpecLlmControl.MaxProviderQueuedRequests;
    public int MaxConcurrentRequests { get; private set; } = SpecLlmControl.MaxProviderConcurrentRequests;
    public IReadOnlyList<EntityId> SelectedNpcIds => _selectedNpcIds;

    private readonly List<EntityId> _selectedNpcIds = new();
    private bool _optInSpecified;

    public bool Enabled => Endpoint is not null && _selectedNpcIds.Count > 0;

    /// <summary>
    /// Applies non-empty <c>HEXLIVE_LLM_*</c> values. Only endpoint or NPC
    /// selection opts in, so auxiliary values are ignored unless either source
    /// requests opt-in. Command-line settings are applied afterwards and override
    /// their non-secret environment equivalents.
    /// </summary>
    public void ApplyEnvironment(
        Func<string, string?> readVariable,
        bool commandLineOptInSpecified = false)
    {
        if (readVariable is null) throw new ArgumentNullException(nameof(readVariable));

        ApplyIfPresent(EndpointEnvironmentVariable, SetEndpoint);
        ApplyIfPresent(NpcsEnvironmentVariable, SetSelectedNpcIds);
        if (!_optInSpecified && !commandLineOptInSpecified)
        {
            return;
        }

        ApplyIfPresent(ApiKeyEnvironmentVariable, SetApiKey);
        ApplyIfPresent(ModelEnvironmentVariable, SetModel);
        ApplyIfPresent(TimeoutEnvironmentVariable, SetRequestTimeoutSeconds);
        ApplyIfPresent(BackoffEnvironmentVariable, SetBackoffSeconds);
        ApplyIfPresent(MaxQueuedEnvironmentVariable, SetMaxQueuedRequests);
        ApplyIfPresent(MaxConcurrentEnvironmentVariable, SetMaxConcurrentRequests);

        void ApplyIfPresent(string name, Action<string> apply)
        {
            var value = ReadIfPresent(name);
            if (value is not null)
            {
                apply(value);
            }
        }

        string? ReadIfPresent(string name)
        {
            var value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public void SetEndpoint(string value)
    {
        _optInSpecified = true;
        value = (value ?? string.Empty).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("LLM endpoint must be an absolute http(s) URL.", nameof(value));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "LLM endpoint must not contain user-info credentials or a fragment.", nameof(value));
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            throw new ArgumentException(
                "LLM endpoint must use HTTPS unless it is a loopback address.", nameof(value));
        }

        Endpoint = endpoint;
    }

    public void SetApiKey(string value)
    {
        value ??= string.Empty;
        if (value.Length > 4096 || (!string.IsNullOrEmpty(value) && !IsBearerToken(value)))
        {
            throw new ArgumentException("LLM API key is not a valid bearer token value.", nameof(value));
        }

        ApiKey = value;
    }

    public void SetModel(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length > 200 || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
        {
            throw new ArgumentException("LLM model hint is invalid or too long.", nameof(value));
        }

        Model = value;
    }

    public void SetRequestTimeoutSeconds(string value)
    {
        var seconds = ParseBoundedInt(value, 1, MaxTimeoutSeconds, "LLM timeout");
        RequestTimeout = TimeSpan.FromSeconds(seconds);
    }

    public void SetBackoffSeconds(string value)
    {
        var seconds = ParseBoundedInt(value, 0, MaxBackoffSeconds, "LLM backoff");
        Backoff = TimeSpan.FromSeconds(seconds);
    }

    public void SetMaxConcurrentRequests(string value)
    {
        MaxConcurrentRequests = ParseBoundedInt(
            value, 1, MaxConcurrentRequestsLimit, "LLM concurrency");
    }

    public void SetMaxQueuedRequests(string value)
    {
        MaxQueuedRequests = ParseBoundedInt(
            value, 0, MaxQueuedRequestsLimit, "LLM queue cap");
    }

    public void SetSelectedNpcIds(string csv)
    {
        _optInSpecified = true;
        var parsed = new List<EntityId>();
        var seen = new HashSet<int>();
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = ParseBoundedInt(part.Trim(), 1, int.MaxValue, "LLM NPC id");
            if (!seen.Add(value))
            {
                throw new ArgumentException("LLM NPC ids must be unique.", nameof(csv));
            }

            parsed.Add(new EntityId(value));
        }

        _selectedNpcIds.Clear();
        _selectedNpcIds.AddRange(parsed);
    }

    /// <summary>Rejects partial opt-in instead of silently leaving LLM control off.</summary>
    public void Validate()
    {
        if (!_optInSpecified)
        {
            return;
        }

        if (Endpoint is null)
        {
            throw new InvalidOperationException(
                $"LLM configuration requires {EndpointEnvironmentVariable} or --llm-endpoint.");
        }

        if (_selectedNpcIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"LLM configuration requires {NpcsEnvironmentVariable} or --llm-npcs.");
        }

        if (MaxQueuedRequests + MaxConcurrentRequests <
            SpecLlmControl.MaxInFlightRequests)
        {
            throw new InvalidOperationException(
                "LLM queue and concurrency must cover the simulation in-flight " +
                $"request budget of {SpecLlmControl.MaxInFlightRequests}.");
        }
    }

    private static int ParseBoundedInt(string value, int minimum, int maximum, string label)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"{label} must be an integer from {minimum} through {maximum}.");
        }

        return parsed;
    }

    private static bool IsBearerToken(string value)
    {
        var paddingStarted = false;
        var hasData = false;
        foreach (var character in value)
        {
            if (character == '=')
            {
                if (!hasData)
                {
                    return false;
                }

                paddingStarted = true;
                continue;
            }

            if (paddingStarted ||
                !(character is >= 'a' and <= 'z' ||
                  character is >= 'A' and <= 'Z' ||
                  character is >= '0' and <= '9' ||
                  character is '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }

            hasData = true;
        }

        return hasData;
    }
}

}
