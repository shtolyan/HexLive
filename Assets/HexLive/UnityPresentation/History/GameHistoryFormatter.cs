#nullable enable
using System;
using System.Globalization;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.Localization;

namespace HexLive.UnityPresentation.History
{
    public enum GameHistoryTone
    {
        Neutral,
        Good,
        Bad,
        Danger,
        Social,
        Build
    }

    public readonly struct GameHistoryText
    {
        public GameHistoryText(string time, string title, string detail, GameHistoryTone tone)
        {
            Time = time;
            Title = title;
            Detail = detail;
            Tone = tone;
        }

        public string Time { get; }
        public string Title { get; }
        public string Detail { get; }
        public GameHistoryTone Tone { get; }
    }

    public static class GameHistoryFormatter
    {
        public static GameHistoryText Format(GameHistoryRecord record)
        {
            var actor = Npc(record.EntityId);
            var target = FirstNpcIn(record.Message, record.EntityId);
            var title = record.Type switch
            {
                "AidStarted" => F("history.AidStarted", actor, target),
                "AidRequested" => F("history.AidRequested", actor, target),
                "AidWaitTimeout" => F("history.AidWaitTimeout", actor),
                "Aided" => F("history.Aided", actor, TargetFromArrow(record.Message, target)),
                "RelationshipChanged" => FormatRelationship(record, actor),
                "TalkRequested" => F("history.TalkRequested", actor, target),
                "TalkStarted" => F("history.TalkStarted", actor, target),
                "RomanceCompleted" => F("history.RomanceCompleted", actor, target),
                "RomanceForced" => F("history.RomanceForced", actor, target),
                "TalkCompleted" => F("history.TalkCompleted", actor, target),
                "CampsMerged" => F("history.CampsMerged", actor),
                "TalkQuarreled" => F("history.TalkQuarreled", actor, target),
                "TalkWaitTimeout" => F("history.TalkWaitTimeout", actor),
                // §108: сговор против чужака и чем он кончился.
                "GroupHuntPactFormed" => F("history.GroupHuntPactFormed", actor),
                "GroupHuntEngaged" => F("history.GroupHuntEngaged", actor),
                "GroupHuntTargetFled" => F("history.GroupHuntTargetFled", actor),
                "GroupHuntDone" => F("history.GroupHuntDone", actor),
                "GroupHuntFailed" => F("history.GroupHuntFailed", actor),
                "InteractionBlocked" => F("history.InteractionBlocked", actor),
                "InteractionRejected" => F("history.InteractionRejected", actor),
                "FoodShared" => F("history.FoodShared", actor, target),
                "FoodStolen" => Loc.Get("history.FoodStolen"),
                "Grieving" => F("history.Grieving", actor),
                "Mourned" => F("history.Mourned", actor),
                "Looted" => F("history.Looted", actor),
                "StrippedHelpless" => F("history.StrippedHelpless", actor),
                "NpcDied" => F("history.NpcDied", actor),
                "BledOut" => F("history.BledOut", actor),
                "StarvedToDeath" => F("history.StarvedToDeath", actor),
                // §60.7: без сознания в глубокой воде дольше DrownDeathTicks.
                "Drowned" => F("history.Drowned", actor),
                "VitalPartDestroyed" => F("history.VitalPartDestroyed", actor),
                "DogFight" => F("history.DogFight", actor),
                "DogAggro" => Loc.Get("history.DogAggro"),
                "DogKilled" => Loc.Get("history.DogKilled"),
                "DogShot" => F("history.DogShot", actor),
                "HelpCry" => F("history.HelpCry", actor),
                "HelpMoan" => F("history.HelpMoan", actor), // §57.10
                "HelpCryAnswered" => F("history.HelpCryAnswered", actor),
                "HelpCryIgnored" => F("history.HelpCryIgnored", actor),
                "HelpCryAssistStarted" => F("history.HelpCryAssistStarted", actor),
                "HelpCryAssistArrived" => F("history.HelpCryAssistArrived", actor),
                "HelpCryAssistLost" => F("history.HelpCryAssistLost", actor),
                "HelpCryDefended" => F("history.HelpCryDefended", actor),
                "FriendGuard" => F("history.FriendGuard", actor),
                "NightRaid" => Loc.Get("history.NightRaid"),
                // §135: системные события зверя — актёра у них нет.
                "MobTookLimb" => Loc.Get("history.MobTookLimb"),
                "MobAteLimb" => Loc.Get("history.MobAteLimb"),
                "MobLeft" => Loc.Get("history.MobLeft"), // §46 v4: стая ушла
                "Preyed" => F("history.Preyed", actor),
                "PreyFoughtBack" => F("history.PreyFoughtBack", actor),
                "PreyFled" => F("history.PreyFled", actor),
                "PredatorKilled" => Loc.Get("history.PredatorKilled"),
                "Murdered" => Loc.Get("history.Murdered"),
                "SharkBite" => F("history.SharkBite", actor),
                "LimbSevered" => F("history.LimbSevered", actor),
                "Fainted" => F("history.Fainted", actor),
                // The coma path emits these two — the old "Collapsed" title waited
                // for a type nothing has ever sent, so comas were invisible here.
                "FellAsleepExhausted" => F("history.FellAsleepExhausted", actor),
                "FaintedBloodLoss" => F("history.FaintedBloodLoss", actor),
                "WokeUp" => F("history.WokeUp", actor),
                // §105: «Collapsed» снова в деле — теперь его действительно
                // эмитят, и это уже не обморок, а обратный отсчёт.
                "Collapsed" => F("history.Collapsed", actor),
                "Rescued" => F("history.Rescued", actor),
                "WoundInflicted" => F("history.WoundInflicted", actor),
                "GotSick" => F("history.GotSick", actor),
                "ThreatSpotted" => F("history.ThreatSpotted", actor),
                "Bandaged" => F("history.Bandaged", actor),
                "Medicated" => F("history.Medicated", actor),
                "StatusStarving" => F("history.StatusStarving", actor),
                "StatusDehydrated" => F("history.StatusDehydrated", actor),
                "StatusOverheated" => F("history.StatusOverheated", actor),
                "Sunburn" => F("history.Sunburn", actor),
                "RainStarted" => Loc.Get("history.RainStarted"),
                "RainStopped" => Loc.Get("history.RainStopped"),
                "StormSurge" => Loc.Get("history.StormSurge"),
                "TreeChopped" => F("history.TreeChopped", actor),
                "BoulderBroken" => F("history.BoulderBroken", actor),
                "LogSplit" => F("history.LogSplit", actor),
                "CrownChopped" => F("history.CrownChopped", actor),
                "CoconutProcessed" => F("history.CoconutProcessed", actor),
                "CoconutDrank" => F("history.CoconutDrank", actor),
                "CoconutEaten" => F("history.CoconutEaten", actor),
                "BottleFilled" => F("history.BottleFilled", actor),
                "DrankBottle" => F("history.DrankBottle", actor),
                "FireLit" => F("history.FireLit", actor),
                "FireFueled" => F("history.FireFueled", actor),
                "FireOut" => Loc.Get("history.FireOut"),
                // The fire emits MeatRoasted — the old "MeatCooked" title waited
                // for a type nothing has ever sent, so cooking was invisible here.
                "MeatRoasted" => Loc.Get("history.MeatRoasted"),
                "MeatHungOnSpit" => F("history.MeatHungOnSpit", actor),
                "MeatSpoiled" => Loc.Get("history.MeatSpoiled"),
                // §54.17: the finish line of the meat chain.
                "MeatEaten" => F("history.MeatEaten", actor),
                "RaftProgress" => F("history.RaftProgress", actor),
                "RaftLaunched" => Loc.Get("history.RaftLaunched"),
                "BuildProgress" => F("history.BuildProgress", actor),
                "FurnitureBuilt" => F("history.FurnitureBuilt", actor),
                "HutCompleted" => Loc.Get("history.HutCompleted"),
                "BedCrafted" => F("history.BedCrafted", actor),
                "TentCrafted" => F("history.TentCrafted", actor),
                "RackCrafted" => F("history.RackCrafted", actor),
                "Butchered" => F("history.Butchered", actor),
                "EmergencyUnload" => F("history.EmergencyUnload", actor),
                "DireStraits" => Loc.Get("history.DireStraits"),
                _ when record.Type.StartsWith("Crafted", StringComparison.Ordinal) =>
                    F("history.Crafted", actor),
                // Dev-фолбэк для типа без термина: сырой тип события, локали
                // не требует (и не должен маскироваться под перевод).
                _ => $"{actor}: {record.Type}"
            };

            return new GameHistoryText(FormatTime(record.Tick), title,
                Detail(record), Tone(record.Type));
        }

        private static string FormatRelationship(GameHistoryRecord record, string actor)
        {
            var target = TargetFromArrow(record.Message, FirstNpcIn(record.Message, record.EntityId));
            var delta = Token(record.Message, "(", ")");
            if (delta.StartsWith("+", StringComparison.Ordinal))
            {
                return F("history.rel.warmed", actor, target);
            }

            if (delta.StartsWith("-", StringComparison.Ordinal))
            {
                return F("history.rel.colder", actor, target);
            }

            return F("history.rel.changed", actor, target);
        }

        private static string Detail(GameHistoryRecord record)
        {
            return record.Type switch
            {
                "AidStarted" => F("history.detail.aid", AidKind(Token(record.Message, "Kind="))),
                "AidRequested" => F("history.detail.aid", AidKind(Token(record.Message, "Kind="))),
                "AidWaitTimeout" => Loc.Get("history.detail.AidWaitTimeout"),
                "Aided" => F("history.detail.Aided", AidKind(Token(record.Message, "Kind="))),
                "TalkRequested" => F("history.detail.TalkRequested", Token(record.Message, "Affinity=")),
                "TalkStarted" => F("history.detail.TalkStarted", TalkTopic(Token(record.Message, "Topic="))),
                "TalkCompleted" => Loc.Get("history.detail.TalkCompleted"),
                "CampsMerged" => Loc.Get("history.detail.CampsMerged"),
                "TalkQuarreled" => Loc.Get("history.detail.TalkQuarreled"),
                "TalkWaitTimeout" => Loc.Get("history.detail.TalkWaitTimeout"),
                "RelationshipChanged" => RelationshipDetail(record.Message),
                "InteractionBlocked" => BusyObjectDetail(record.Message),
                "InteractionRejected" => Loc.Get("history.detail.InteractionRejected"),
                "NpcDied" => DeathDetail(record.Message),
                "FoodStolen" => NpcArrowDetail(record.Message),
                "Murdered" => NpcArrowDetail(record.Message),
                "StrippedHelpless" => F("history.detail.StrippedHelpless",
                    Token(record.Message, "Count=")),
                "HelpCry" => HelpCryDetail(record.Message),
                "HelpCryAnswered" => HelpCryDecisionDetail(record.Message),
                "HelpCryIgnored" => HelpCryDecisionDetail(record.Message),
                "HelpCryDefended" => CleanDetail(record.Message),
                "GroupHuntPactFormed" => Loc.Get("history.detail.GroupHuntPactFormed"),
                "GroupHuntDone" => Loc.Get("history.detail.GroupHuntDone"),
                "GroupHuntFailed" => CleanDetail(record.Message),
                "RaftProgress" => record.Message,
                "DireStraits" => Loc.Get("history.detail.DireStraits"),
                _ => CleanDetail(record.Message)
            };
        }

        private static string RelationshipDetail(string message)
        {
            var affinity = Token(message, "Aff=");
            var delta = Token(message, "(", ")");
            if (string.IsNullOrEmpty(affinity))
            {
                return CleanDetail(message);
            }

            return string.IsNullOrEmpty(delta)
                ? F("history.detail.affinity", affinity)
                : F("history.detail.affinity.delta", affinity, delta);
        }

        private static string DeathDetail(string message)
        {
            var cause = Token(message, "Cause=[", "]");
            if (string.IsNullOrEmpty(cause))
            {
                return CleanDetail(message);
            }

            var typeEnd = cause.IndexOf(':');
            var type = typeEnd > 0 ? cause.Substring(0, typeEnd) : cause;
            // Неизвестная причина рендерится сырым типом — как missing key в Loc.
            var key = "history.cause." + type;
            var localized = Loc.Has(key) ? Loc.Get(key) : type;
            return F("history.detail.cause", localized);
        }

        /// <summary>§136: дневник красит чернила тем же тоном — одно событие не
        /// может быть тревожным в ленте и будничным на странице.</summary>
        internal static GameHistoryTone ToneOf(string type) => Tone(type);

        private static GameHistoryTone Tone(string type)
        {
            if (type is "Aided" or "AidRequested" or "AidStarted" or "RomanceCompleted" or "TalkCompleted" or "CampsMerged" or "TalkRequested" or
                "TalkStarted" or "FoodShared" or "RelationshipChanged" or "Mourned" or
                "Rescued" or // §105: её вытащили — это про людей, а не про урон
                "HelpCryAnswered")
            {
                return GameHistoryTone.Social;
            }

            if (type.StartsWith("Crafted", StringComparison.Ordinal) ||
                type is "TreeChopped" or "BoulderBroken" or "LogSplit" or "CrownChopped" or
                    "BuildProgress" or "FurnitureBuilt" or "HutCompleted" or "RaftProgress" or "RaftLaunched")
            {
                return GameHistoryTone.Build;
            }

            if (type is "NpcDied" or "BledOut" or "StarvedToDeath" or "VitalPartDestroyed" or "Drowned" or
                "DogFight" or "NightRaid" or "Murdered" or "Preyed" or "RomanceForced" or "SharkBite" or "LimbSevered" or
                "MobTookLimb" or // §135: добычу унесли в зубах — это красная строка
                "Collapsed" or // §105: она при смерти — тревожнее этого в колонии ничего нет
                "HelpCry" or "HelpMoan" or // §57.10: слабый зов умирающей — красная строка
                "HelpCryAssistStarted" or "HelpCryAssistArrived" or "HelpCryDefended" or
                // §108: расправа — это драка, и в ленте она должна быть красной.
                "GroupHuntPactFormed" or "GroupHuntEngaged" or "GroupHuntStruck" or
                "GroupHuntTargetFled" or "GroupHuntDone" or "GroupHuntFailed" or
                // §111: обыск лежащего — не хозяйственная работа, а сцена
                // насилия без сопротивления. В ленте она красная.
                "StrippedHelpless")
            {
                return GameHistoryTone.Danger;
            }

            if (type is "AidWaitTimeout" or "TalkQuarreled" or "TalkWaitTimeout" or "InteractionBlocked" or
                "InteractionRejected" or "FoodStolen" or "Grieving" or "StatusStarving" or "StatusDehydrated" or
                "StatusOverheated" or "Sunburn" or "Fainted" or
                "FellAsleepExhausted" or "FaintedBloodLoss" or
                "WoundInflicted" or "GotSick" or "MeatSpoiled" or "ThreatSpotted" or
                "HelpCryIgnored" or "HelpCryAssistLost")
            {
                return GameHistoryTone.Bad;
            }

            return GameHistoryTone.Neutral;
        }

        /// <summary>§136: дневник показывает то же «День N, ЧЧ:ММ», и делать
        /// это вторым способом значило бы завести второй календарь.</summary>
        internal static string FormatTime(int tick)
        {
            var day = EnvironmentSystem.CalendarDay(tick);
            var progress = (tick % EnvironmentSystem.DayLengthTicks) /
                           (float)EnvironmentSystem.DayLengthTicks;
            var clock = EnvironmentSystem.FormatClock(progress);
            return F("history.time", day, clock);
        }

        private static string Npc(int? id) =>
            id.HasValue
                ? $"NPC #{id.Value.ToString(CultureInfo.InvariantCulture)}"
                : Loc.Get("history.colony");

        private static string FirstNpcIn(string message, int? fallback)
        {
            var idx = message.IndexOf("NPC", StringComparison.Ordinal);
            if (idx < 0)
            {
                return Npc(fallback);
            }

            idx += 3;
            var start = idx;
            while (idx < message.Length && char.IsDigit(message[idx]))
            {
                idx++;
            }

            return idx > start ? $"NPC #{message.Substring(start, idx - start)}" : Npc(fallback);
        }

        private static string TargetFromArrow(string message, string fallback)
        {
            var arrow = message.IndexOf("->NPC", StringComparison.Ordinal);
            if (arrow < 0)
            {
                return fallback;
            }

            var start = arrow + 5;
            var end = start;
            while (end < message.Length && char.IsDigit(message[end]))
            {
                end++;
            }

            return end > start ? $"NPC #{message.Substring(start, end - start)}" : fallback;
        }

        private static string Token(string message, string prefix, string suffix = " ")
        {
            var start = message.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += prefix.Length;
            var end = suffix == " "
                ? message.IndexOf(' ', start)
                : message.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                end = message.Length;
            }

            return message.Substring(start, Math.Max(0, end - start)).Trim();
        }

        private static string AidKind(string kind)
        {
            // Неизвестный вид помощи остаётся сырым токеном (виден как баг).
            var key = "history.aid." + kind;
            return Loc.Has(key) ? Loc.Get(key) : kind;
        }

        private static string TalkTopic(string topic)
        {
            var key = "history.topic." + topic;
            return Loc.Has(key) ? Loc.Get(key) : topic;
        }

        private static string NpcArrowDetail(string message)
        {
            var first = FirstNpcIn(message, null);
            var second = TargetFromArrow(message, string.Empty);
            return string.IsNullOrEmpty(second)
                ? CleanDetail(message)
                : $"{first} -> {second}";
        }

        private static string HelpCryDetail(string message)
        {
            var attacker = Token(message, "Dog=");
            if (!string.IsNullOrEmpty(attacker))
            {
                return F("history.detail.attacker.dog", attacker);
            }

            attacker = Token(message, "Attacker=NPC");
            return string.IsNullOrEmpty(attacker)
                ? CleanDetail(message)
                : F("history.detail.attacker.npc", attacker);
        }

        private static string HelpCryDecisionDetail(string message)
        {
            var victim = Token(message, "Victim=NPC");
            var score = Token(message, "Score=");
            var roll = Token(message, "Roll=");
            if (string.IsNullOrEmpty(victim))
            {
                return CleanDetail(message);
            }

            return F("history.detail.helpcry.decision", victim, score, roll);
        }

        private static string BusyObjectDetail(string message)
        {
            var occupiedBy = Token(message, "occupied by ");
            if (string.IsNullOrEmpty(occupiedBy))
            {
                return CleanDetail(message);
            }

            return F("history.detail.occupied", occupiedBy);
        }

        private static string CleanDetail(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return message.Length <= 160 ? message : message.Substring(0, 157) + "...";
        }

        /// <summary>Термин §58 с подстановками ({0}, {1}, …) — как end.subtitle.</summary>
        private static string F(string key, params object[] args) =>
            string.Format(Loc.Get(key), args);
    }
}
