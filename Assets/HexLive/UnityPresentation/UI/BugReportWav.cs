using System;
using System.IO;
using System.Text;

namespace HexLive.UnityPresentation.UI
{
    // §114.8: adapted from First Signal main7901ccdf2 ReportWav.
    public static class BugReportWav
    {
        public const int Rate = 16000, MaxSeconds = 90;
        public static byte[] Prepare(byte[] wav)
        {
            if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") throw new ArgumentException("Invalid WAV");
            int channels = 0, rate = 0, offset = 0, length = 0;
            for (int at = 12; at <= wav.Length - 8;)
            {
                int size = BitConverter.ToInt32(wav, at + 4);
                if (size < 0 || size > wav.Length - at - 8) throw new ArgumentException("Invalid WAV chunk");
                string name = Encoding.ASCII.GetString(wav, at, 4);
                if (name == "fmt ")
                {
                    if (size < 16 || BitConverter.ToInt16(wav, at + 8) != 1 ||
                        BitConverter.ToInt16(wav, at + 22) != 16) throw new ArgumentException("Expected PCM16");
                    channels = BitConverter.ToInt16(wav, at + 10); rate = BitConverter.ToInt32(wav, at + 12);
                }
                if (name == "data") { offset = at + 8; length = size; }
                at += 8 + size + (size & 1);
            }
            if (channels < 1 || channels > 8 || rate < 8000 || rate > 192000 || offset == 0 ||
                length % (channels * 2) != 0) throw new ArgumentException("Invalid PCM format");
            int samples = Math.Min(length / 2, checked(rate * channels * MaxSeconds));
            var pcm = new float[samples];
            for (int i = 0; i < samples; i++) pcm[i] = BitConverter.ToInt16(wav, offset + i * 2) / 32768f;
            pcm = TrimSilence(pcm, channels, rate);
            int frames = pcm.Length / channels;
            int count = Math.Min(Rate * MaxSeconds, (int)((long)frames * Rate / rate));
            if (count < Rate / 4) throw new ArgumentException("RecordingTooShort");
            using var stream = new MemoryStream(44 + count * 2);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(Rate); writer.Write(Rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
            for (int i = 0; i < count; i++)
            {
                double at = (double)i * rate / Rate;
                int a = Math.Min(frames - 1, (int)at), b = Math.Min(frames - 1, a + 1);
                double sample = 0;
                for (int c = 0; c < channels; c++)
                    sample += (pcm[a * channels + c] + (pcm[b * channels + c] - pcm[a * channels + c]) * (at - a)) / channels;
                writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, sample)) * 32767));
            }
            return stream.ToArray();
        }
        private static float[] TrimSilence(float[] pcm, int channels, int rate)
        {
            int frame = Math.Max(1, rate / 50), count = pcm.Length / channels / frame;
            if (count < 10) return pcm;
            var rms = new float[count];
            for (int f = 0; f < count; f++)
            {
                double sum = 0; int from = f * frame * channels;
                for (int i = from; i < from + frame * channels; i++) sum += pcm[i] * (double)pcm[i];
                rms[f] = (float)Math.Sqrt(sum / (frame * channels));
            }
            var sorted = (float[])rms.Clone(); Array.Sort(sorted);
            float threshold = Math.Min(.015f, Math.Max(.003f, sorted[count * 15 / 100] * 3));
            int pad = Math.Max(1, (int)Math.Round(.35 * rate / frame));
            var keep = new bool[count]; bool voiced = false;
            for (int f = 0; f < count; f++) if (rms[f] > threshold)
            {
                voiced = true;
                for (int k = Math.Max(0, f - pad); k <= Math.Min(count - 1, f + pad); k++) keep[k] = true;
            }
            if (!voiced) return pcm;
            int kept = 0; foreach (bool value in keep) if (value) kept++;
            if (kept * frame < rate / 4 || kept == count) return pcm;
            var result = new float[kept * frame * channels]; int cursor = 0;
            for (int f = 0; f < count; f++) if (keep[f])
            { Array.Copy(pcm, f * frame * channels, result, cursor, frame * channels); cursor += frame * channels; }
            return result;
        }
    }
}
