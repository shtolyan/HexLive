using System.Text;
using System.Text.RegularExpressions;

namespace HexLive.AgentHost;

/// <summary>Russian Snowball regions and suffix steps; https://snowballstem.org/algorithms/russian/stemmer.html.</summary>
public static class MemoryWords
{
    private const string Vowels = "аеиоуыэюя";
    private static readonly HashSet<string> Stop = new("и в на о об а но мы ты я он она что как это тот тогда помнишь помню вспомни наш наша наши мне тебе с со по ли the a an of to and remember our".Split(' '));
    public static string Normalize(string text) => text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё', 'е');
    public static string[] Tokens(string text) => Regex.Matches(Normalize(text), @"[\p{L}\p{Nd}]+")
        .Select(m => m.Value).Where(s => !Stop.Contains(s)).Select(Stem).ToArray();
    public static string Stem(string word)
    {
        if (!word.Any(c => Vowels.Contains(c))) return word;
        int Region(int from)
        {
            for (var i = Math.Max(1, from + 1); i < word.Length; i++)
                if (Vowels.Contains(word[i - 1]) && !Vowels.Contains(word[i])) return i + 1;
            return word.Length;
        }
        var rv = word.IndexOfAny(Vowels.ToCharArray()) + 1;
        if (rv == 0) return word;
        var r2 = Region(Region(0));
        var value = word;
        bool Cut(string suffixes, int region, bool afterAya = false)
        {
            foreach (var suffix in suffixes.Split(' ').OrderByDescending(s => s.Length))
            {
                var at = value.Length - suffix.Length;
                if (at < region || !value.EndsWith(suffix, StringComparison.Ordinal) ||
                    afterAya && (at <= rv || value[at - 1] is not ('а' or 'я'))) continue;
                value = value[..at]; return true;
            }
            return false;
        }
        if (!Cut("ившись ывшись ивши ывши ив ыв", rv) && !Cut("вшись вши в", rv, true))
        {
            Cut("ся сь", rv);
            if (Cut("ее ие ые ое ими ыми ей ий ый ой ем им ым ом его ого ему ому их ых ую юю ая яя ою ею", rv))
            { if (!Cut("ивш ывш ующ", rv)) Cut("ем нн вш ющ щ", rv, true); }
            else if (!Cut("ила ыла ена ейте уйте ите или ыли ей уй ил ыл им ым ен ило ыло ено ят ует уют ит ыт ены ить ыть ишь ую ю", rv)
                && !Cut("ла на ете йте ли й л ем н ло но ет ны ть ешь нно ют", rv, true))
                Cut("а ев ов ие ье е иями ями ами еи ии и ией ей ой ий й иям ям ием ем ам ом о у ах иях ях ы ь ию ью ю ия ья я", rv);
        }
        Cut("и", rv);
        Cut("ость ост", r2);
        if (value.EndsWith("нн", StringComparison.Ordinal)) value = value[..^1];
        else if (Cut("ейше ейш", rv)) { if (value.EndsWith("нн", StringComparison.Ordinal)) value = value[..^1]; }
        else Cut("ь", rv);
        return value;
    }
}
