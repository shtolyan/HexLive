using System.Text.RegularExpressions;

namespace HexLive.VoiceBrain;

/// <summary>§171.7: an explicit self-introduction, never an inferred account identity.</summary>
public static class VoiceIntroduction
{
    private const string Name = @"([\p{L}][\p{L}\p{M}'’\-]{0,47})";
    private static readonly Regex Explicit = new(
        @"^\s*(?:(?:привет|здравствуй|кстати|да|а|hello|hi)\s*[,!—]?\s+)*(?:меня\s+зовут|мо[её]\s+имя|называй(?:те)?\s+меня|зови\s+меня|my\s+name\s+is|call\s+me)\s*[:—,\-]?\s*[""«]?" + Name,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Short = new(
        @"^\s*(?:(?i:привет|здравствуй)[,!]?\s*)?(?i:я|это|i['’]m|i am)\s+(\p{Lu}[\p{L}\p{M}'’\-]{0,47})(?=$|[,.!?]|\s+(?i:и|из|рад|твой|from)\b)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Reverse = new(@"^\s*" + Name + @"\s+(?i:меня\s+зовут)\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool TryRead(string text, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 2000) text = text[..2000];
        var match = Explicit.Match(text);
        if (!match.Success) match = Short.Match(text);
        if (!match.Success) match = Reverse.Match(text);
        if (!match.Success) return false;
        name = match.Groups[1].Value;
        if (name.ToLowerInvariant() is "не" or "никто" or "никогда" or "секрет" or "not" or "nobody" or "unknown")
        { name = ""; return false; }
        if (name.All(c => !char.IsLetter(c) || char.IsUpper(c))) name = name.ToLowerInvariant();
        name = char.ToUpperInvariant(name[0]) + name[1..];
        return true;
    }
}
