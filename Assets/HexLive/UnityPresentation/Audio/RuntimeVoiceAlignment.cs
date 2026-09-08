#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace HexLive.UnityPresentation.Audio
{
/// <summary>§67.7 DSP and duration-constrained Viterbi port of bake_lipsync.py.
/// FIR addition, nearest-floor resampling and post-window normalization match
/// the calibrated offline pipeline, including its intentional quirks.</summary>
internal static class RuntimeVoiceAlignment
{
    private const int Visemes = 14;
    private const double Epsilon = 1.1920929e-7;
    private sealed class State
    {
        public int Vis;
        public int Min;
        public int Max;
        public bool Optional => Vis < 0;
    }

    public static byte[][] Bake(short[] pcm, string text, CancellationToken cancel = default)
    {
        var dsp = new Dsp();
        var count = (int)Math.Ceiling(pcm.Length / 44100d * 60) + 1;
        var distances = new double[count][];
        var volumes = new double[count];
        for (var f = 0; f < count; f++)
        {
            cancel.ThrowIfCancellationRequested();
            distances[f] = new double[Visemes];
            Array.Fill(distances[f], double.NaN);
            if (f + 1 < count)
                dsp.Analyze(pcm, (int)Math.Round(f / 60d * 44100), distances[f], out volumes[f]);
        }
        var states = G2P(text);
        int[]? path = null;
        if (states.Count > 1)
        {
            double sum = 0, square = 0;
            var finite = 0;
            foreach (var row in distances)
                foreach (var d in row)
                    if (!double.IsNaN(d)) { sum += d; square += d * d; finite++; }
            if (finite > 0)
            {
                var mean = sum / finite;
                var std = Math.Sqrt(Math.Max(0, square / finite - mean * mean));
                if (std <= 1e-9) std = 1;
                var normalized = new double[count][];
                for (var f = 0; f < count; f++)
                {
                    normalized[f] = new double[Visemes];
                    for (var v = 0; v < Visemes; v++) normalized[f][v] = (distances[f][v] - mean) / std;
                }
                path = Align(normalized, volumes, states, cancel);
            }
        }
        var ratios = new double[count, Visemes];
        if (path != null)
        {
            for (var f = 0; f < count - 1; f++)
                if (states[path[f]].Vis >= 0) ratios[f, states[path[f]].Vis] = 0.8;
            var raw = (double[,])ratios.Clone();
            for (var f = 1; f < count - 1; f++)
                for (var v = 0; v < Visemes; v++)
                    ratios[f, v] = .25 * raw[f - 1, v] + .5 * raw[f, v] + .25 * raw[f + 1, v];
        }
        else
        {
            for (var f = 0; f < count; f++)
            {
                double total = 0;
                for (var v = 0; v < Visemes; v++)
                {
                    var score = Math.Pow(10, -distances[f][v]);
                    ratios[f, v] = double.IsNaN(score) || double.IsInfinity(score) ? 0 : score;
                    total += ratios[f, v];
                }
                if (total > 0)
                    for (var v = 0; v < Visemes; v++) ratios[f, v] /= total;
            }
        }
        var result = new byte[count][];
        for (var f = 0; f < count; f++)
        {
            result[f] = new byte[Visemes + 1];
            if (f == 0 || f == count - 1) continue;
            for (var v = 0; v < Visemes; v++) result[f][v] = Byte(ratios[f, v]);
            result[f][Visemes] = Byte(volumes[f]);
        }
        return result;
    }

    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static readonly Dictionary<char, int> Russian = new()
    {
        ['а']=0, ['я']=0, ['и']=1, ['ы']=1, ['й']=1, ['у']=2, ['ю']=2,
        ['э']=3, ['е']=3, ['ё']=4, ['о']=4, ['б']=5, ['м']=5, ['п']=5,
        ['в']=6, ['ф']=6, ['з']=7, ['с']=7, ['ц']=7, ['ж']=8, ['ш']=8,
        ['щ']=8, ['ч']=8, ['д']=9, ['н']=9, ['т']=9, ['р']=10, ['л']=11,
        ['г']=12, ['к']=12, ['х']=12, ['ъ']=-2, ['ь']=-2,
    };

    private static List<State> G2P(string text)
    {
        var states = new List<State> { new() { Vis = -1, Min = 1 } };
        text = (text ?? string.Empty).ToLowerInvariant();
        var annotation = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[') { annotation = true; continue; }
            if (c == ']') { annotation = false; continue; }
            if (annotation) continue;
            if (!char.IsLetter(c))
            {
                if (states[states.Count - 1].Vis >= 0) states.Add(new State { Vis = -1, Min = 1 });
                continue;
            }
            int vis;
            if (i + 1 < text.Length && RuntimeVoiceCalibration.Digraphs.TryGetValue(text.Substring(i, 2), out vis)) i++;
            else if (!RuntimeVoiceCalibration.Letters.TryGetValue(c, out vis) && !Russian.TryGetValue(c, out vis))
                return new List<State>(); // unsupported language: acoustic fallback for the whole utterance
            if (vis < 0) continue;
            if (states[states.Count - 1].Vis == vis) { states[states.Count - 1].Max = 0; continue; }
            states.Add(new State { Vis = vis, Min = vis <= 4 ? 4 : 2,
                Max = vis <= 4 ? 0 : vis == 5 || vis == 9 || vis == 12 ? 8 : vis == 10 || vis == 11 ? 10 : 12 });
        }
        if (states[states.Count - 1].Vis >= 0) states.Add(new State { Vis = -1, Min = 1 });
        return states;
    }

    private static int[]? Align(double[][] distances, double[] volumes, List<State> states, CancellationToken cancel)
    {
        var entry = new int[states.Count];
        var subState = new List<int>();
        var subPosition = new List<int>();
        for (var s = 0; s < states.Count; s++)
        {
            entry[s] = subState.Count;
            for (var p = 0; p < (states[s].Max == 0 ? states[s].Min : states[s].Max); p++)
            { subState.Add(s); subPosition.Add(p); }
        }
        var size = subState.Count;
        var exits = new List<int>[size];
        var step = new int[size];
        var stay = new bool[size];
        for (var j = 0; j < size; j++)
        {
            var s = subState[j];
            var st = states[s];
            var last = subPosition[j] == (st.Max == 0 ? st.Min : st.Max) - 1;
            stay[j] = last && st.Max == 0;
            step[j] = last ? -1 : j + 1;
            exits[j] = new List<int>();
            if (subPosition[j] < st.Min - 1) continue;
            for (var t = s + 1; t < states.Count; t++)
            {
                exits[j].Add(entry[t]);
                if (!states[t].Optional) break;
            }
        }
        const double inf = 1e18;
        var cost = new double[size];
        Array.Fill(cost, inf);
        cost[entry[0]] = Emission(0, states[0].Vis, distances, volumes);
        for (var s = 1; s < states.Count && states[s - 1].Optional; s++)
            cost[entry[s]] = Emission(0, states[s].Vis, distances, volumes);
        var back = new int[volumes.Length][];
        for (var f = 1; f < volumes.Length; f++)
        {
            cancel.ThrowIfCancellationRequested();
            var next = new double[size];
            Array.Fill(next, inf);
            var arg = back[f] = new int[size];
            Array.Fill(arg, -1);
            for (var j = 0; j < size; j++)
            {
                if (cost[j] >= inf) continue;
                if (stay[j] && cost[j] < next[j]) { next[j] = cost[j]; arg[j] = j; }
                var k = step[j];
                if (k >= 0 && cost[j] < next[k]) { next[k] = cost[j]; arg[k] = j; }
                foreach (var target in exits[j])
                    if (cost[j] < next[target]) { next[target] = cost[j]; arg[target] = j; }
            }
            for (var j = 0; j < size; j++) next[j] += Emission(f, states[subState[j]].Vis, distances, volumes);
            cost = next;
        }
        var best = -1;
        var bestCost = inf;
        for (var j = 0; j < size; j++)
        {
            var s = subState[j];
            var remaining = states[s].Vis >= 0 && subPosition[j] < states[s].Min - 1 ? 1 : 0;
            for (var t = s + 1; t < states.Count; t++) if (!states[t].Optional) remaining++;
            var total = cost[j] + 4 * remaining;
            if (total < bestCost) { best = j; bestCost = total; }
        }
        if (best < 0) return null;
        var path = new int[volumes.Length];
        for (var f = path.Length - 1; f >= 0; f--)
        {
            if (best < 0) return null;
            path[f] = subState[best];
            if (f > 0) best = back[f][best];
        }
        return path;
    }

    private static double Emission(int frame, int vis, double[][] distances, double[] volumes)
    {
        var volume = volumes[frame];
        if (vis < 0) return 3 * volume;
        var basis = double.IsNaN(distances[frame][vis]) ? 2 : distances[frame][vis];
        return basis + 2.5 * Math.Clamp((.1 - volume) / .1, 0, 1) - (vis <= 4 ? .6 * volume : 0);
    }

    private sealed class Dsp
    {
        private const int N = RuntimeVoiceCalibration.SampleCount;
        private const int Window = 2823; // ceil(1024 * 44100 / 16000)
        private readonly double[] _fir;
        private readonly double[,] _mel = new double[26, N / 2 + 1];
        private readonly double[,] _dct = new double[12, 26];
        private readonly double[] _hamming = new double[N];
        public Dsp()
        {
            var range = (float)(500d / 44100);
            var taps = (int)Math.Round(3.1f / range);
            if ((taps + 1) % 2 == 0) taps++;
            _fir = new double[taps];
            var cutoff = (float)(7500d / 44100);
            for (var i = 0; i < taps; i++)
            {
                var x = (float)(i - (taps - 1) / 2d);
                var angle = (float)(2 * Math.PI) * cutoff * x;
                _fir[i] = (float)(2 * cutoff * (float)Math.Sin(angle) / angle);
            }
            for (var i = 0; i < N; i++) _hamming[i] = .54 - .46 * Math.Cos(2 * Math.PI * i / (N - 1));
            var melStep = 1127 * Math.Log(1 + 8000d / 700) / 27;
            const double frequencyStep = 8000d / (N / 2);
            for (var ch = 0; ch < 26; ch++)
            {
                var start = Hz(melStep * ch);
                var center = Hz(melStep * (ch + 1));
                var end = Hz(melStep * (ch + 2));
                var mid = (int)Math.Round(center / frequencyStep);
                for (var k = (int)Math.Ceiling(start / frequencyStep) + 1; k <= (int)Math.Floor(end / frequencyStep) && k <= N / 2; k++)
                {
                    var f = frequencyStep * k;
                    _mel[ch, k] = (k < mid ? (f - start) / (center - start) : (end - f) / (end - center)) / ((end - start) * .5);
                }
                for (var row = 0; row < 12; row++) _dct[row, ch] = Math.Cos((row + 1) * (ch + .5) * Math.PI / 26);
            }
        }
        private static double Hz(double mel) => 700 * (Math.Exp(mel / 1127) - 1);

        public void Analyze(short[] pcm, int position, double[] distances, out double volume)
        {
            var window = new double[Window];
            double square = 0, peak = 0;
            for (var i = 0; i < Window; i++)
            {
                var source = position - Window + i;
                var sample = source < 0 || source >= pcm.Length ? 0 : pcm[source] / 32768d;
                window[i] = sample;
                square += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            var rms = Math.Sqrt(square / Window);
            volume = rms <= 0 ? 0 : Math.Clamp((Math.Log10(rms) - RuntimeVoiceCalibration.MinVolume) /
                (RuntimeVoiceCalibration.MaxVolume - RuntimeVoiceCalibration.MinVolume), 0, 1);
            if (peak < Epsilon) { volume = 0; return; }
            var x = new double[N];
            for (var i = 0; i < N; i++)
            {
                var index = (int)Math.Floor(i * (44100d / 16000));
                double filtered = window[index];
                for (var j = 0; j < _fir.Length && j <= index; j++) filtered += _fir[j] * window[index - j];
                x[i] = filtered;
            }
            var real = new double[N];
            var imag = new double[N];
            peak = 0;
            for (var i = 0; i < N; i++)
            {
                real[i] = (i == 0 ? x[0] : x[i] - .97 * x[i - 1]) * _hamming[i];
                peak = Math.Max(peak, Math.Abs(real[i]));
            }
            if (peak >= Epsilon) for (var i = 0; i < N; i++) real[i] /= peak;
            Fft(real, imag);
            var spectrum = new double[N / 2 + 1];
            for (var i = 0; i <= N / 2; i++) spectrum[i] = Math.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
            var mel = new double[26];
            for (var ch = 0; ch < 26; ch++)
            {
                double value = 0;
                for (var k = 0; k <= N / 2; k++) value += _mel[ch, k] * spectrum[k];
                if (value <= 0) { volume = 0; return; }
                mel[ch] = 10 * Math.Log10(value);
            }
            var mfcc = new double[12];
            for (var r = 0; r < 12; r++) for (var ch = 0; ch < 26; ch++) mfcc[r] += _dct[r, ch] * mel[ch];
            for (var v = 0; v < Visemes; v++)
            {
                double sum = 0;
                for (var r = 0; r < 12; r++) { var d = mfcc[r] - RuntimeVoiceCalibration.Templates[v][r]; sum += d * d; }
                distances[v] = Math.Sqrt(sum / 12);
            }
        }
        private static void Fft(double[] real, double[] imag)
        {
            for (int i = 1, j = 0; i < N; i++)
            {
                var bit = N >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (real[i], real[j]) = (real[j], real[i]); }
            }
            for (var length = 2; length <= N; length <<= 1)
            {
                var angle = -2 * Math.PI / length;
                var wr = Math.Cos(angle);
                var wi = Math.Sin(angle);
                for (var start = 0; start < N; start += length)
                {
                    double ur = 1, ui = 0;
                    for (var j = 0; j < length / 2; j++)
                    {
                        var a = start + j;
                        var b = a + length / 2;
                        var vr = real[b] * ur - imag[b] * ui;
                        var vi = real[b] * ui + imag[b] * ur;
                        real[b] = real[a] - vr; imag[b] = imag[a] - vi;
                        real[a] += vr; imag[a] += vi;
                        (ur, ui) = (ur * wr - ui * wi, ur * wi + ui * wr);
                    }
                }
            }
        }
    }
}
}
