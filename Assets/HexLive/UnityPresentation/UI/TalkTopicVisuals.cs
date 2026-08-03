using UnityEngine;

namespace HexLive.UnityPresentation.UI
{

// Spec 28.15E: presentation-side lookup that turns a sim TalkTopic name (the
// snapshot ships the enum's ToString()) into the emoji glyph shown in the
// overhead speech bubble, plus an accent colour. The sim never knows about
// glyphs — this is the only place the mapping lives, so retheming (or swapping
// glyphs for sprite icons later) is a one-file change.
public static class TalkTopicVisuals
{
    public readonly struct Topic
    {
        public readonly string Emoji;
        public readonly Color Accent;

        public Topic(string emoji, Color accent)
        {
            Emoji = emoji;
            Accent = accent;
        }
    }

    private static readonly Color Warm = new(0.98f, 0.78f, 0.30f);
    private static readonly Color Cool = new(0.55f, 0.78f, 0.98f);
    private static readonly Color Danger = new(0.95f, 0.45f, 0.40f);
    private static readonly Color Love = new(0.98f, 0.45f, 0.62f);
    private static readonly Color Neutral = new(0.85f, 0.85f, 0.88f);

    // Emoji chosen to avoid VS16 variation selectors where possible (some
    // Unity font stacks drop them). One glyph per subject.
    //
    // §67.10: LEGACY reference table — nothing reads For() any more. The bubble
    // picture now comes from SpeechCatalog (id → PNG in Resources/.../Emoji),
    // which is why the five personal-complaint topics are deliberately absent
    // here. Only IsKnown() is still live.
    public static Topic For(string topicName)
    {
        switch (topicName)
        {
            case "Escape":  return new Topic("⛵", Cool);      // ⛵ sailboat
            case "Sharks":  return new Topic("\U0001F988", Danger);// 🦈
            case "Dogs":    return new Topic("\U0001F415", Danger);// 🐕
            case "Weather": return new Topic("\U0001F327", Cool);  // 🌧 rain cloud
            case "Food":    return new Topic("\U0001F965", Warm);  // 🥥 coconut
            case "Fire":    return new Topic("\U0001F525", Warm);  // 🔥
            case "Home":    return new Topic("\U0001F3E0", Warm);  // 🏠
            case "Gossip":  return new Topic("\U0001F440", Neutral);// 👀
            case "Flirt":   return new Topic("\U0001F497", Love);  // 💗
            case "Joke":    return new Topic("\U0001F602", Warm);  // 😂
            case "Grumble": return new Topic("\U0001F620", Danger);// 😠
            case "Stranger": return new Topic("\U0001F620", Danger);// 😠 §107: в пузыре его лицо
            case "SmallTalk":
            default:        return new Topic("\U0001F4AC", Neutral);// 💬
        }
    }

    public static bool IsKnown(string topicName)
        => !string.IsNullOrEmpty(topicName) && topicName != "-";

    // Sims-style relationship pop for a resolved talk. Success (+affinity) is a
    // green plus, a quarrel (-affinity) a red minus; a bigger swing doubles the
    // glyph ("сильно/несильно"). Returns false when the delta is negligible.
    public static readonly Color PosColor = new(0.36f, 0.86f, 0.42f);
    public static readonly Color NegColor = new(0.95f, 0.38f, 0.34f);

    public static bool RelationshipPop(float delta, out string glyph, out Color color)
    {
        var mag = Mathf.Abs(delta);
        if (mag < 0.001f)
        {
            glyph = string.Empty;
            color = PosColor;
            return false;
        }

        var doubled = mag >= 0.10f;
        if (delta > 0f)
        {
            glyph = doubled ? "++" : "+";
            color = PosColor;
        }
        else
        {
            // Use a proper minus sign (U+2212) — reads cleaner than hyphen.
            glyph = doubled ? "−−" : "−";
            color = NegColor;
        }

        return true;
    }
}

}
