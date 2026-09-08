using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

/// <summary>Provider-independent voice seam around the proven PCM/FMOD conversion path.</summary>
public sealed class ElevenLabsVoiceAdapter(string integrationId,
    Func<CancellationToken, Task<string>> getKey) : IVoiceAdapter
{
    public async Task<byte[]> SynthesizeWavAsync(VoiceSelection selection, string text, CancellationToken token)
    {
        if (selection.IntegrationId != integrationId || string.IsNullOrWhiteSpace(selection.VoiceId) ||
            string.IsNullOrWhiteSpace(selection.ModelId) || string.IsNullOrWhiteSpace(text) || text.Length > 600)
            throw new InvalidDataException("InvalidVoiceSelection");
        var key = await getKey(token);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("MissingVoiceCredential");
        using var provider = new AgentProviders(new AgentProviderOptions
        {
            McpUri = new Uri("http://localhost/mcp"), McpToken = "", XaiKey = "", XaiModel = "",
            ElevenLabsKey = key, ElevenLabsVoiceId = selection.VoiceId, ElevenLabsModel = selection.ModelId,
        });
        // No MCP or model call is made by this adapter.
        var wav = (await provider.SynthesizeAsync(text, token)).Wav;
        if (wav.Length > 6 * 1024 * 1024) throw new InvalidDataException("VoiceTooLarge");
        return wav;
    }
}

public sealed class NoVoiceAdapter : IVoiceAdapter
{
    public Task<byte[]> SynthesizeWavAsync(VoiceSelection selection, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Existing speech delivery treats absent WAV as a text-only utterance.
        return Task.FromResult(Array.Empty<byte>());
    }
}
