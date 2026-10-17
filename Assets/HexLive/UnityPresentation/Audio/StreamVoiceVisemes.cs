#nullable enable
using System;
using System.Collections.Generic;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>§171.18: provider character timings, existing calibrated G2P map.</summary>
    public sealed class StreamVoiceVisemes
    {
        private readonly List<(float start, float end, int vis)> _segments = new();
        private float _tail;
        public void Clear() { _segments.Clear(); _tail = 0; }

        public void Append(string[] chars, float[] starts, float[] durations)
        {
            if (chars == null || starts == null || durations == null || chars.Length == 0 ||
                chars.Length != starts.Length || chars.Length != durations.Length) return;
            var text = string.Concat(chars).ToLowerInvariant();
            // SDK timing chunks are relative to their own audio; retain their order.
            var offset = _tail;
            int letter = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                if (!float.IsFinite(starts[i]) || !float.IsFinite(durations[i]) || starts[i] < 0 || durations[i] < 0)
                { Clear(); return; }
                var start = offset + starts[i] / 1000f;
                var end = start + durations[i] / 1000f;
                if (letter < text.Length && RuntimeVoiceAlignment.TryTextViseme(text, letter, out var vis, out var count) && vis >= 0)
                {
                    if (count == 2 && i + 1 < chars.Length)
                    {
                        end = offset + (starts[i + 1] + durations[i + 1]) / 1000f;
                        letter += (chars[i]?.Length ?? 0) + (chars[i + 1]?.Length ?? 0);
                        i++;
                    }
                    else letter += chars[i]?.Length ?? 0;
                    _segments.Add((start, end, vis));
                }
                else letter += chars[i]?.Length ?? 0;
                _tail = Math.Max(_tail, end);
            }
        }

        public int Sample(float seconds)
        {
            // Old chunks can be removed without changing the absolute speech clock.
            while (_segments.Count > 0 && _segments[0].end <= seconds) _segments.RemoveAt(0);
            return _segments.Count > 0 && seconds >= _segments[0].start ? _segments[0].vis : -1;
        }
    }
}
