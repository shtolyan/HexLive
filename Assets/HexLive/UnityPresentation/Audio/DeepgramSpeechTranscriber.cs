#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>§160 replaceable player-speech boundary. Audio goes straight to STT.</summary>
    public interface IPlayerSpeechTranscriber : IDisposable
    {
        Task<string> TranscribeAsync(byte[] wav, string temporaryToken,
            CancellationToken cancellationToken);
    }

    public sealed class DeepgramSpeechTranscriber : IPlayerSpeechTranscriber
    {
        private readonly int _maxCharacters;
        private readonly bool _rejectOverflow;
        public DeepgramSpeechTranscriber(int maxCharacters = 240, bool rejectOverflow = false)
        { _maxCharacters = Math.Max(1, maxCharacters); _rejectOverflow = rejectOverflow; }
        private static readonly Uri Endpoint = new(
            "https://api.deepgram.com/v1/listen?model=nova-3&language=ru&smart_format=true&punctuate=true");
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };

        public async Task<string> TranscribeAsync(byte[] wav, string temporaryToken,
            CancellationToken cancellationToken)
        {
            if (wav == null || wav.Length < 44 || string.IsNullOrWhiteSpace(temporaryToken))
                return string.Empty;
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", temporaryToken);
            request.Content = new ByteArrayContent(wav);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var response = await _http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var transcript = (string?)JObject.Parse(json)["results"]?["channels"]?[0]?
                ["alternatives"]?[0]?["transcript"];
            transcript = transcript?.Trim() ?? string.Empty;
            if (_rejectOverflow && transcript.Length > _maxCharacters)
                throw new InvalidOperationException("TranscriptTooLong");
            return transcript.Length <= _maxCharacters ? transcript : transcript.Substring(0, _maxCharacters);
        }

        public void Dispose() => _http.Dispose();
    }
}
