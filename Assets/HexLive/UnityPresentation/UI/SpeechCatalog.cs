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
        // §81.13: проигравший сцену абьюза бежит домой в слезах.
        // §110.8: обещанного здесь «фолбэка на банк cry» не существовало —
        // вторая ступень PlayVoiceLine ищет voice_<char>_cry, а таких файлов
        // нет ни одного, и пузырь был немым. У группы теперь своя секция в
        // HEXKUFA_LANGUAGE.md §7 (C15) и свои файлы.
        ["cry_beaten"] = new("Grief", Rank.Action, 30f),
        // §110: лежит и рыдает после стресс-краха. Ambient — потому что это
        // фон состояния, а не событие; пауза between всхлипами = AmbientGap.
        // §110: всхлип — не бормотание себе под нос, а сама сцена, поэтому
        // ранг Action: слой Ambient молчит в компании (а подруга как раз
        // подошла утешать) и держит пол в 25 с — на 60-секундный плач это два
        // звука за всю истерику. Action оставляет только MinGap: хнычет каждые
        // ~12 с, и рядом стоящая её слышит.
        ["cry_breakdown"] = new("Grief", Rank.Action, 12f),
        ["angry_defend"] = new("Attack", Rank.Alarm, 10f),
        ["fear_dark_alone"] = new("Warning", Rank.Ambient, 120f),
        ["fear_stranger"] = new("Warning", Rank.Alarm, 15f),

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
        // §110: «ну не плачь» — своя реплика утешения вместо общей на все виды
        // помощи: над рыдающей помощница именно ГОВОРИТ, это вся её работа.
        ["happy_console"] = new("Console", Rank.Action, 12f),
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
            // §108: разговор ПРО НЕГО. Значок — злая гримаса ворчания: в самом
            // пузыре его всё равно заслоняет портрет (лицо старше значка), а
            // значок остаётся запасным на тот единственный игровой час, пока
            // снимок ещё не сделан. Своей группы реплик на хекскуфе пока нет —
            // берётся ворчание, заменится одной строкой.
            "Stranger" => "angry_topic_grumble",
            "Hunger" => "sad_hunger",
            "Thirst" => "sad_thirst",
            "Pain" => "hurt_wound",
            "Tired" => "sleepy_tired",
            "Cold" => "sad_cold",
            _ => null
        };
    }

    // ---- one-shot social cue (WorldSnapshot.SocialCueKind) ---------------


    public readonly struct CueVisual
    {
        public readonly string PopIcon;   // Resources/HexLive/UI/Emoji/<PopIcon>.png
        public readonly string SpeechId;  // null = this cue has no utterance of its own

        // §107.5: пузырь ОДИН, и место в нём разыгрывается по рангу — «хочу
        // пить» никогда не перебьёт «рядом чужак». У кьюшки со своей репликой
        // ранг берётся из неё; у молчаливой (TalkRequest, AbuseHurt) он живёт
        // здесь, иначе такой кьюшке нечем было бы соревноваться за пузырь.
        public readonly Rank Rank;

        public CueVisual(string popIcon, string speechId, Rank rank = Rank.Action)
        {
            PopIcon = popIcon;
            SpeechId = speechId;
            Rank = rank;
        }
    }

    // Кьюшка рисуется ДВАЖДЫ — картинкой над головой и репликой в бабле, — и
    // раньше это были два рукописных switch'а в разных файлах. Они разошлись:
    // на любой крик о помощи всплывала СОБАКА, хотя резать могла и рука
    // человека. Одна таблица — расходиться больше негде.
    //
    // Ключ — вид кьюшки из симуляции, при необходимости с суффиксом «кто»:
    // "HelpCry:dog" / "HelpCry:npc" / "DangerSpotted:<mobId>". Голый вид без
    // суффикса остаётся рабочим ключом (старые снапшоты, реплеи).
    private static readonly Dictionary<string, CueVisual> Cues = new()
    {
        // ---- крик о помощи: картинка зависит от того, КТО напал ----------
        ["HelpCry:dog"] = new("Dogs", "call_help"),
        ["HelpCry:npc"] = new("Attack", "call_help"),
        ["HelpCry"] = new("Dogs", "call_help"),

        ["HelpCryAssistStarted:dog"] = new("Dogs", "angry_defend"),
        ["HelpCryAssistArrived:dog"] = new("Dogs", "angry_defend"),
        ["HelpCryDefended:dog"] = new("Dogs", "angry_defend"),
        ["HelpCryAssistStarted:npc"] = new("Attack", "angry_defend"),
        ["HelpCryAssistArrived:npc"] = new("Attack", "angry_defend"),
        ["HelpCryDefended:npc"] = new("Attack", "angry_defend"),
        ["HelpCryAssistStarted"] = new("Dogs", "angry_defend"),
        ["HelpCryAssistArrived"] = new("Dogs", "angry_defend"),
        ["HelpCryDefended"] = new("Dogs", "angry_defend"),

        ["HelpCryIgnored"] = new("Grumble", null, Rank.Talk),
        ["HelpCryAnswer"] = new("Home", null, Rank.Action),
        ["HelpCryAnswered"] = new("Home", null, Rank.Action),

        // ---- угроза замечена издалека (§62/§72) --------------------------
        // Над головой — жёлтый треугольник: это ещё не бой, это «вижу».
        // Реплика уже про конкретного: зверь, акула или человек.
        ["DangerSpotted:dog"] = new("Warning", "fear_wolf"),
        ["DangerSpotted:shark"] = new("Warning", "fear_shark"),
        ["DangerSpotted:*"] = new("Warning", "fear_flee"),
        ["DangerSpotted"] = new("Warning", "fear_wolf"),
        // §80: чужак-человек. Над ним всплывает ЛИЦО (портрет перекрывает
        // иконку), а кричит она про чужака, а не про зверюгу.
        ["DangerStranger"] = new("Warning", "fear_stranger"),

        // ---- взаимопомощь (§53) ------------------------------------------
        ["AidRequest"] = new("Food", "sad_aid_ask"),
        ["AidIncoming"] = new("Food", "happy_aid_give"),
        ["AidStarted"] = new("Food", "happy_aid_give"),
        ["AidCompleted"] = new("Food", "happy_aid_thanks"),

        // ---- разговор ------------------------------------------------------
        ["TalkRequest"] = new("SmallTalk", null, Rank.Talk),
        ["TalkIncoming"] = new("SmallTalk", null, Rank.Talk),
        ["TalkSuccess"] = new("Joke", null, Rank.Talk),
        ["TalkRejected"] = new("Grumble", null, Rank.Talk),
        ["TalkRefused"] = new("Grumble", null, Rank.Talk),
        ["TalkQuarrel"] = new("Grumble", null, Rank.Talk),
        ["Resentment"] = new("Grumble", null, Rank.Talk),

        // Увидела убийство. Раньше здесь всплывала АКУЛА 🦈 — та же болезнь, что
        // собака на человека: картинка из соседней строки таблицы.
        ["WitnessedMurder"] = new("Death", "cry_corpse"),

        // ---- §81: сцена абьюза. Своих групп на хекскуфе пока нет — берём
        // ближайшие существующие, чтобы сцена не шла в полной тишине;
        // заменится на angry_extort_* / cry_extort_* одной строкой.
        ["AbuseDemand"] = new("Attack", "angry_attack"),
        ["AbuseStruck"] = new("Attack", "angry_attack"),
        ["AbuseThreatened"] = new("Warning", null, Rank.Alarm),
        ["AbuseCowed"] = new("Warning", null, Rank.Alarm),
        ["AbuseCry"] = new("Grief", "cry_corpse"),
        ["AbuseGaveUp"] = new("Gift", "cry_corpse"),
        ["AbuseHurt"] = new("Blood", null, Rank.Alarm),
        ["AbuseSubmit"] = new("Gift", null, Rank.Alarm),
        ["AbuseTook"] = new("Gift", null, Rank.Alarm),
        ["AbuseDefied"] = new("Grumble", "angry_defend"),
        ["AbuseRefused"] = new("Grumble", null, Rank.Alarm),
        ["AbuseFled"] = new("Flee", null, Rank.Alarm),
        // §81.13: проигравший сцену убегает домой с плачем.
        ["AbuseFledHome"] = new("Flee", "cry_beaten"),

        // ---- §108: сговор и увиденная сцена. Обе несут ЕГО id, так что над
        // головой всплывает его лицо, а иконка — запасная на тот час, пока
        // снимок ещё не сделан.
        ["GroupHuntPact"] = new("Attack", "angry_defend"),
        ["AbuseWitnessed"] = new("Warning", "angry_attack")
    };

    // Never fails: an unknown cue still draws (fallback icon, no utterance).
    // A suffixed kind falls back to "<base>:*" and then to the bare base, so a
    // new mob id shows a sane bubble the day it is added to the sim.
    public static CueVisual ForCue(string cueKind)
    {
        if (string.IsNullOrEmpty(cueKind))
        {
            return new CueVisual(FallbackIcon, null);
        }

        if (Cues.TryGetValue(cueKind, out var visual))
        {
            return visual;
        }

        var cut = cueKind.IndexOf(':');
        if (cut > 0)
        {
            var baseKind = cueKind[..cut];
            if (Cues.TryGetValue(baseKind + ":*", out visual) ||
                Cues.TryGetValue(baseKind, out visual))
            {
                return visual;
            }
        }

        return new CueVisual(FallbackIcon, null);
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
            // §110: утешение отделилось от остальной помощи — у него свои
            // слова («ну не плачь») и свой значок.
            "ConsoleOther" => "happy_console",
            "FeedOther" or "TreatOther" or "MedicateOther" or "HydrateOther"
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
        public bool Crying; // §110: лежит и рыдает
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

        // §110: пока она рыдает, жаловаться ей больше не на что — всхлип
        // перебивает и голод, и жажду (порог 0 даёт k = 1, максимум шкалы).
        Consider("cry_breakdown", s.Crying ? 1f : 0f, 0f);
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
