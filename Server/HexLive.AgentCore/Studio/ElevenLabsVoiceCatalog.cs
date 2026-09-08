using System.Text.Json;

namespace HexLive.AgentCore.Studio;

public sealed record VoiceDescriptor(string Id, string Name)
{
    public override string ToString() => $"{Name} · {Id}";
}
public sealed class ElevenLabsVoiceCatalog(Func<CancellationToken, Task<string>> key, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = handler == null ? new() : new(handler, false);
    public async Task<IReadOnlyList<VoiceDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var credential = await key(token);
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("MissingVoiceCredential");
        var voices = new List<VoiceDescriptor>(); var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var url = "https://api.elevenlabs.io/v2/voices?page_size=100&include_total_count=false" +
                (cursor == null ? "" : "&next_page_token=" + Uri.EscapeDataString(cursor));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("xi-api-key", credential);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("VoiceCatalogRejected", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("VoiceCatalogPageTooLarge");
                bytes.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            foreach (var voice in root.GetProperty("voices").EnumerateArray())
            {
                var id = voice.GetProperty("voice_id").GetString(); var name = voice.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || name?.Length > 256) throw new InvalidDataException("InvalidVoiceCatalogEntry");
                voices.Add(new(id, name ?? id));
            }
            if (voices.Count > 2048) throw new InvalidDataException("VoiceCatalogTooLarge");
            cursor = root.GetProperty("has_more").GetBoolean() ? root.GetProperty("next_page_token").GetString() : null;
            if (root.GetProperty("has_more").GetBoolean() && (cursor == null || !cursors.Add(cursor)))
                throw new InvalidDataException("VoiceCatalogCursorInvalid");
        } while (cursor != null);
        return voices.DistinctBy(v => v.Id).OrderBy(v => v.Name, StringComparer.Ordinal).ToArray();
    }
    public void Dispose() => _http.Dispose();
}
