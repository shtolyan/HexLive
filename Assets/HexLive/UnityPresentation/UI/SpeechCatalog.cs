using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{

// §67.10: the ONE table that turns a speech act into everything the player
// sees and hears. Every utterance in the game — a conversation turn, a
// complaint about an empty belly, a scream at a wolf — is a single id from
// this catalog, and the id alone decides:
//
//   * the voice bank  →  voice_<char>_<id>_<n>.wav (hexkufa, §67.6)
//   * the face        →  the emotion prefix before the first '_' (§67.8)
//   * the bubble      →  the Emoji sprite named in Icon (§28.15E)
//   * arbitration     →  Rank + MinGap (who interrupts whom, how often)
//
// The point of the single table is the invariant the design asks for: SPEECH
// IS NEVER SILENT ART AND NEVER A MUTE BUBBLE. NpcSpeechDirector shows the
// bubble and plays the voice from the same call, so one cannot happen without
// the other, and an id that is missing here still draws (fallback icon) rather
// than speaking with no picture.
//
// The line texts themselves (hexkufa) live in HEXKUFA_LANGUAGE.md — the ids
// here are the same slugs as that catalog's group names.
public static class SpeechCatalog
{
    // Who wins when two things want the mouth at once. Higher interrupts lower;
    // equal rank does not interrupt (first one finishes).
    public enum Rank
    {
        Ambient = 0, // self-talk about her own state — the quietest layer
        Talk = 1,    // a conversation turn (§28.15A) — owns the mouth while chatting
        Action = 2,  // work / success / failure beats tied to a verb
        Alarm = 3    // wound, wolf, help cry, death — always cuts through
    }

    public readonly struct Line
    {
        public readonly string Icon;   // Resources/HexLive/UI/Emoji/<Icon>.png
        public readonly Rank Rank;
        public readonly float MinGap;  // seconds before the SAME line may repeat

        public Line(string icon, Rank rank, float minGap)
        {
            Icon = icon;
            Rank = rank;
            MinGap = minGap;
        }
    }

    public const string FallbackIcon = "SmallTalk";

    // Bubble is held at least this long even when no voice file exists yet, so
    // the visual half of the feature works before a single line is recorded.
    public const float MinBubbleSeconds = 1.7f;
    public const float BubbleTailSeconds = 0.45f;

    private static readonly Dictionary<string, Line> Lines = new()
    {
        // ---- A. her own body (ambient self-talk) -------------------------
        ["sad_hunger"] = new("Hunger", Rank.Ambient, 40f),
        ["sad_thirst"] = new("Thirst", Rank.Ambient, 40f),
        ["sad_cold"] = new("Cold", Rank.Ambient, 35f),
        ["sad_heat"] = new("Heat", Rank.Ambient, 35f),
        ["sleepy_tired"] = new("Tired", Rank.Ambient, 45f),
        ["sleepy_bed"] = new("Bed", Rank.Ambient, 30f),
        ["sleepy_wake"] = new("Tired", Rank.Ambient, 60f),
        ["hurt_sick"] = new("Sick", Rank.Ambient, 50f),
        ["sad_dirty"] = new("Wash", Rank.Ambient, 90f),
        ["sad_lonely"] = new("Lonely", Rank.Ambient, 70f),
        ["happy_relief"] = new("Relief", Rank.Action, 25f),
        ["sad_wet"] = new("Wet", Rank.Ambient, 80f),
        ["sleepy_dream"] = new("Dream", Rank.Ambient, 120f),

        // ---- B. work and its outcomes -----------------------------------
        ["work_chop"] = new("Chop", Rank.Action, 14f),
        ["work_haul"] = new("Haul", Rank.Action, 30f),
        ["work_build"] = new("Build", Rank.Action, 16f),
        ["work_craft"] = new("Craft", Rank.Action, 20f),
        ["happy_done"] = new("Done", Rank.Action, 8f),
        ["happy_fire_lit"] = new("Fire", Rank.Action, 20f),
        ["sad_fire_out"] = new("FireOut", Rank.Action, 60f),
        ["happy_find_food"] = new("Food", Rank.Action, 20f),
        ["happy_water"] = new("Thirst", Rank.Action, 30f),
        ["angry_fail"] = new("Fail", Rank.Action, 35f),
        ["happy_wash"] = new("Wash", Rank.Action, 30f),
        ["happy_eat"] = new("Hunger", Rank.Action, 20f),
        ["happy_drink"] = new("Thirst", Rank.Action, 20f),
        ["happy_bed_done"] = new("Bed", Rank.Action, 10f),
        ["happy_dress"] = new("Dress", Rank.Action, 40f),
        ["happy_gift"] = new("Gift", Rank.Action, 20f),

        // ---- C. danger, combat, death -----------------------------------
        ["fear_wolf"] = new("Dogs", Rank.Alarm, 12f),
        ["call_help"] = new("Help", Rank.Alarm, 8f),
        ["angry_attack"] = new("Attack", Rank.Alarm, 6f),
        ["hurt_bitten"] = new("Blood", Rank.Alarm, 4f),
        ["hurt_wound"] = new("Pain", Rank.Alarm, 5f),
        ["happy_victory"] = new("Victory", Rank.Action, 10f),
        ["fear_flee"] = new("Flee", Rank.Alarm, 12f),
        ["fear_shark"] = new("Sharks", Rank.Alarm, 15f),
        ["hurt_death"] = new("Death", Rank.Alarm, 0f),
        ["hurt_faint"] = new("Faint", Rank.Alarm, 20f),
        ["cry_corpse"] = new("Grief", Rank.Action, 25f),
        ["cry_bury"] = new("Bury", Rank.Action, 20f),
        ["angry_defend"] = new("Attack", Rank.Alarm, 10f),
        ["fear_dark_alone"] = new("Warning", Rank.Ambient, 120f),

        // ---- D. conversation (one per TalkTopic) -------------------------
        ["happy_topic_smalltalk"] = new("SmallTalk", Rank.Talk, 0f),
        ["sad_topic_escape"] = new("Escape", Rank.Talk, 0f),
        ["fear_topic_sharks"] = new("Sharks", Rank.Talk, 0f),
        ["angry_topic_dogs"] = new("Dogs", Rank.Talk, 0f),
        ["sad_topic_weather"] = new("Weather", Rank.Talk, 0f),
        ["happy_topic_food"] = new("Food", Rank.Talk, 0f),
        ["happy_topic_fire"] = new("Fire", Rank.Talk, 0f),
        ["cry_topic_home"] = new("Home", Rank.Talk, 0f),
        ["happy_topic_gossip"] = new("Gossip", Rank.Talk, 0f),
        ["happy_topic_flirt"] = new("Flirt", Rank.Talk, 0f),
        ["happy_topic_joke"] = new("Joke", Rank.Talk, 0f),
        ["angry_topic_grumble"] = new("Grumble", Rank.Talk, 0f),
        ["happy_laugh"] = new("Joke", Rank.Talk, 6f),
        ["happy_agree"] = new("Agree", Rank.Talk, 6f),
        ["happy_bond_plus"] = new("Flirt", Rank.Action, 6f),
        ["angry_bond_minus"] = new("Grumble", Rank.Action, 6f),
        ["call_name"] = new("Call", Rank.Action, 20f),

        // ---- E. mutual aid (§53) ----------------------------------------
        ["happy_aid_give"] = new("Aid", Rank.Action, 12f),
        ["happy_aid_thanks"] = new("Thanks", Rank.Action, 12f),
        ["sad_aid_ask"] = new("Help", Rank.Action, 20f)
    };

    private static readonly HashSet<string> WarnedMissing = new();

    // Never fails — an unknown id still gets a bubble (fallback icon) so a new
    // hook can never produce a voice with no picture. Warns once in the editor
    // so the gap gets filled in the table rather than silently living on.
    public static Line Get(string id)
    {
        if (!string.IsNullOrEmpty(id) && Lines.TryGetValue(id, out var line))
        {
            return line;
        }

        if (!string.IsNullOrEmpty(id) && WarnedMissing.Add(id))
        {
            Debug.LogWarning($"[SpeechCatalog] no entry for speech id '{id}' — " +
                             "add it to SpeechCatalog.Lines (see HEXKUFA_LANGUAGE.md §7).");
        }

        return new Line(FallbackIcon, Rank.Talk, 6f);
    }

    public static bool Has(string id) => !string.IsNullOrEmpty(id) && Lines.ContainsKey(id);

    // §67.8: the face emotion is the id's prefix — "sad_thirst" → "sad".
    public static string EmotionOf(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "happy";
        }

        var cut = id.IndexOf('_');
        return cut > 0 ? id[..cut] : id;
    }

    // ---- sim signal → speech id -----------------------------------------

    // A sim TalkTopic name (snapshot ships the enum's ToString()). The five
    // personal-complaint topics (§67.10) reuse the self-talk lines: the same
    // hexkufa words work whether she mutters them or says them to a housemate.
    public static string ForTopic(string topicName)
    {
        return topicName switch
        {
            "SmallTalk" => "happy_topic_smalltalk",
            "Escape" => "sad_topic_escape",
            "Sharks" => "fear_topic_sharks",
            "Dogs" => "angry_topic_dogs",
            "Weather" => "sad_topic_weather",
            "Food" => "happy_topic_food",
            "Fire" => "happy_topic_fire",
            "Home" => "cry_topic_home",
            "Gossip" => "happy_topic_gossip",
            "Flirt" => "happy_topic_flirt",
            "Joke" => "happy_topic_joke",
            "Grumble" => "angry_topic_grumble",
            "Hunger" => "sad_hunger",
            "Thirst" => "sad_thirst",
            "Pain" => "hurt_wound",
            "Tired" => "sleepy_tired",
            "Cold" => "sad_cold",
            _ => null
        };
    }

    // One-shot social cue (WorldSnapshot.SocialCueKind). Null = this cue has no
    // utterance of its own (the "+/-" pop already speaks for it).
    public static string ForCue(string cueKind)
    {
        return cueKind switch
        {
            "HelpCry" => "call_help",
            "HelpCryAssistStarted" or "HelpCryAssistArrived" or "HelpCryDefended" => "angry_defend",
            "DangerSpotted" => "fear_wolf",
            // §80: чужак-человек. Своей группы реплик пока нет — ставим
            // «страх зверя»: молчать в момент, когда над головой всплыло его
            // лицо, было бы хуже, чем сказать не совсем то. Заведём
            // fear_stranger — заменится одной строкой.
            "DangerStranger" => "fear_wolf",
            "AidRequest" => "sad_aid_ask",
            "AidIncoming" or "AidStarted" => "happy_aid_give",
            "AidCompleted" => "happy_aid_thanks",
            "WitnessedMurder" => "cry_corpse",
            "TalkRejected" or "TalkRefused" or "TalkQuarrel" or "Resentment" => null,
            _ => null
        };
    }

    // The verb she is performing right now (WorldSnapshot.CurrentInteraction) →
    // a work utterance, fired on the rising edge only. Null = silent verb.
    public static string ForInteraction(string interaction)
    {
        return interaction switch
        {
            "Harvest" or "Process" => "work_chop",
            "Build" or "BuildRaft" => "work_build",
            "Craft" => "work_craft",
            "Butcher" => "work_craft",
            "Eat" => "happy_eat",
            "Drink" => "happy_drink",
            "FillBottle" => "happy_water",
            "Sleep" => "sleepy_bed",
            "WashClothes" => "happy_wash",
            "Dress" => "happy_dress",
            "Bury" => "cry_bury",
            "FeedOther" or "TreatOther" or "MedicateOther" or "ConsoleOther" or "HydrateOther"
                => "happy_aid_give",
            // §68: winding a dressing round her own wound — it hurts going on.
            "TreatSelf" => "hurt_wound",
            _ => null
        };
    }

    // ---- her own state → an ambient complaint ---------------------------

    // Everything the ambient layer is allowed to look at, pushed once per
    // snapshot by HexWorldRenderer (presentation-only; the sim never sees it).
    public struct BodyState
    {
        public float Hunger;
        public float Thirst;
        public float Energy;
        public float ThermalComfort; // -1 freezing .. +1 boiling
        public float Hygiene;        // 1 clean .. 0 filthy
        public float Social;
        public float Wetness;
        public bool Wounded;
        public bool Sick;
        public bool Asleep;
        public bool Fainted;
    }

    // The single worst thing about her right now, or null when she has nothing
    // to complain about. Thresholds sit well past "mildly bothered" so the
    // colony does not become a market — the point is a rare, readable beat.
    public static string ForState(in BodyState s)
    {
        var worst = (string)null;
        var severity = 0f;

        void Consider(string id, float value, float threshold)
        {
            if (value <= threshold)
            {
                return;
            }

            var k = (value - threshold) / Mathf.Max(0.001f, 1f - threshold);
            if (k > severity)
            {
                severity = k;
                worst = id;
            }
        }

        Consider("sad_hunger", s.Hunger, 0.60f);
        Consider("sad_thirst", s.Thirst, 0.60f);
        Consider("sleepy_tired", 1f - s.Energy, 0.75f);
        Consider("sad_cold", -s.ThermalComfort, 0.40f);
        Consider("sad_heat", s.ThermalComfort, 0.45f);
        Consider("hurt_sick", s.Sick ? 1f : 0f, 0.5f);
        Consider("sad_dirty", 1f - s.Hygiene, 0.80f);
        Consider("sad_lonely", 1f - s.Social, 0.85f);
        Consider("sad_wet", s.Wetness, 0.85f);

        return worst;
    }
}

}
