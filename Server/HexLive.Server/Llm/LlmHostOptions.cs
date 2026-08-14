using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Llm
{

public sealed class LlmHostOptions
{
    public Uri? Endpoint { get; private set; }
    public string ApiKey { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(12);
    public TimeSpan Backoff { get; private set; } = TimeSpan.FromSeconds(8);
    public int MaxQueuedRequests { get; private set; } = SpecLlmControl.MaxProviderQueuedRequests;
    public int MaxConcurrentRequests { get; private set; } = SpecLlmControl.MaxProviderConcurrentRequests;
    public IReadOnlyList<EntityId> SelectedNpcIds => _selectedNpcIds;

    private readonly List<EntityId> _selectedNpcIds = new();

    public bool Enabled => Endpoint is not null && _selectedNpcIds.Count > 0;

    public void SetEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("LLM endpoint must be an absolute http(s) URL.", nameof(value));
        }

        Endpoint = endpoint;
    }

    public void SetApiKey(string value) => ApiKey = value ?? string.Empty;

    public void SetModel(string value) => Model = value ?? string.Empty;

    public void SetRequestTimeoutSeconds(string value)
    {
        var seconds = int.Parse(value);
        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "LLM timeout must be positive.");
        }

        RequestTimeout = TimeSpan.FromSeconds(seconds);
    }

    public void SetBackoffSeconds(string value)
    {
        var seconds = int.Parse(value);
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "LLM backoff cannot be negative.");
        }

        Backoff = TimeSpan.FromSeconds(seconds);
    }

    public void SetMaxConcurrentRequests(string value)
    {
        MaxConcurrentRequests = int.Parse(value);
        if (MaxConcurrentRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "LLM concurrency must be positive.");
        }
    }

    public void SetMaxQueuedRequests(string value)
    {
        MaxQueuedRequests = int.Parse(value);
        if (MaxQueuedRequests < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "LLM queue cap cannot be negative.");
        }
    }

    public void SetSelectedNpcIds(string csv)
    {
        _selectedNpcIds.Clear();
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            _selectedNpcIds.Add(new EntityId(int.Parse(part.Trim())));
        }
    }
}

}
