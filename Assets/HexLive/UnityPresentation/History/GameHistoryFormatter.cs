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
                "AidStarted" => L("history.event.AidStarted", actor, target),
                "AidRequested" => L("history.event.AidRequested", actor, target),
                "AidWaitTimeout" => L("history.event.AidWaitTimeout", actor),
                "Aided" => L("history.event.Aided", actor, TargetFromArrow(record.Message, target)),
                "RelationshipChanged" => FormatRelationship(record, actor),
                "TalkRequested" => L("history.event.TalkRequested", actor, target),
                "TalkStarted" => L("history.event.TalkStarted", actor, target),
                "TalkCompleted" => L("history.event.TalkCompleted", actor, target),
                "TalkQuarreled" => L("history.event.TalkQuarreled", actor, target),
                "TalkWaitTimeout" => L("history.event.TalkWaitTimeout", actor),
                "InteractionBlocked" => L("history.event.InteractionBlocked", actor),
                "InteractionRejected" => L("history.event.InteractionRejected", actor),
                "FoodShared" => L("history.event.FoodShared", actor, target),
                "FoodStolen" => L("history.event.FoodStolen"),
                "Grieving" => L("history.event.Grieving", actor),
                "Mourned" => L("history.event.Mourned", actor),
                "VisitedGrave" => L("history.event.VisitedGrave", actor),
                "NpcDied" => L("history.event.NpcDied", actor),
                "BledOut" => L("history.event.BledOut", actor),
                "StarvedToDeath" => L("history.event.StarvedToDeath", actor),
                "VitalPartDestroyed" => L("history.event.VitalPartDestroyed", actor),
                "DogFight" => L("history.event.DogFight", actor),
                "DogAggro" => L("history.event.DogAggro"),
                "DogKilled" => L("history.event.DogKilled"),
                "DogShot" => L("history.event.DogShot", actor),
                "NightRaid" => L("history.event.NightRaid"),
                "Preyed" => L("history.event.Preyed", actor),
                "PreyFoughtBack" => L("history.event.PreyFoughtBack", actor),
                "PreyFled" => L("history.event.PreyFled", actor),
                "PredatorKilled" => L("history.event.PredatorKilled"),
                "Murdered" => L("history.event.Murdered"),
                "SharkBite" => L("history.event.SharkBite", actor),
                "LimbSevered" => L("history.event.LimbSevered", actor),
                "Fainted" => L("history.event.Fainted", actor),
                "Bandaged" => L("history.event.Bandaged", actor),
                "BandageCrafted" => L("history.event.BandageCrafted", actor),
                "Medicated" => L("history.event.Medicated", actor),
                "StatusStarving" => L("history.event.StatusStarving", actor),
                "StatusDehydrated" => L("history.event.StatusDehydrated", actor),
                "StatusOverheated" => L("history.event.StatusOverheated", actor),
                "Sunburn" => L("history.event.Sunburn", actor),
                "RainStarted" => L("history.event.RainStarted"),
                "RainStopped" => L("history.event.RainStopped"),
                "StormSurge" => L("history.event.StormSurge"),
                "TreeChopped" => L("history.event.TreeChopped", actor),
                "BoulderBroken" => L("history.event.BoulderBroken", actor),
                "LogSplit" => L("history.event.LogSplit", actor),
                "MeatCooked" => L("history.event.MeatCooked", actor),
                "CrownChopped" => L("history.event.CrownChopped", actor),
                "CoconutProcessed" => L("history.event.CoconutProcessed", actor),
                "CoconutDrank" => L("history.event.CoconutDrank", actor),
                "CoconutEaten" => L("history.event.CoconutEaten", actor),
                "BottleFilled" => L("history.event.BottleFilled", actor),
                "DrankBottle" => L("history.event.DrankBottle", actor),
                "FireLit" => L("history.event.FireLit", actor),
                "FireFueled" => L("history.event.FireFueled", actor),
                "FireOut" => L("history.event.FireOut"),
                "RaftProgress" => L("history.event.RaftProgress", actor),
                "RaftLaunched" => L("history.event.RaftLaunched"),
                "BuildProgress" => L("history.event.BuildProgress", actor),
                "FurnitureBuilt" => L("history.event.FurnitureBuilt", actor),
                "HutCompleted" => L("history.event.HutCompleted"),
                "BedCrafted" => L("history.event.BedCrafted", actor),
                "TentCrafted" => L("history.event.TentCrafted", actor),
                "RackCrafted" => L("history.event.RackCrafted", actor),
                "Buried" => L("history.event.Buried", actor),
                "Butchered" => L("history.event.Butchered", actor),
                "EmergencyUnload" => L("history.event.EmergencyUnload", actor),
                "DireStraits" => L("history.event.DireStraits"),
                _ when record.Type.StartsWith("Crafted", StringComparison.Ordinal) =>
                    L("history.event.CraftedAny", actor),
                _ => L("history.event.Unknown", actor, record.Type)
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
                return L("history.event.RelationshipChangedPositive", actor, target);
            }

            if (delta.StartsWith("-", StringComparison.Ordinal))
            {
                return L("history.event.RelationshipChangedNegative", actor, target);
            }

            return L("history.event.RelationshipChanged", actor, target);
        }

        private static string Detail(GameHistoryRecord record)
        {
            return record.Type switch
            {
                "AidStarted" => L("history.detail.help", AidKind(Token(record.Message, "Kind="))),
                "AidRequested" => L("history.detail.help", AidKind(Token(record.Message, "Kind="))),
                "AidWaitTimeout" => L("history.detail.AidWaitTimeout"),
                "Aided" => L("history.detail.Aided", AidKind(Token(record.Message, "Kind="))),
                "TalkRequested" => L("history.detail.TalkRequested", Token(record.Message, "Affinity=")),
                "TalkStarted" => L("history.detail.TalkStarted", TalkTopic(Token(record.Message, "Topic="))),
                "TalkCompleted" => L("history.detail.TalkCompleted"),
                "TalkQuarreled" => L("history.detail.TalkQuarreled"),
                "TalkWaitTimeout" => L("history.detail.TalkWaitTimeout"),
                "RelationshipChanged" => RelationshipDetail(record.Message),
                "InteractionBlocked" => BusyObjectDetail(record.Message),
                "InteractionRejected" => L("history.detail.InteractionRejected"),
                "NpcDied" => DeathDetail(record.Message),
                "FoodStolen" => NpcArrowDetail(record.Message),
                "Murdered" => NpcArrowDetail(record.Message),
                "RaftProgress" => record.Message,
                "DireStraits" => L("history.detail.DireStraits"),
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
                ? L("history.detail.RelationshipAffinity", affinity)
                : L("history.detail.RelationshipAffinityDelta", affinity, delta);
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
            var localized = type switch
            {
                "BledOut" => L("history.cause.BledOut"),
                "DogFight" => L("history.cause.DogFight"),
                "Heatstroke" => L("history.cause.Heatstroke"),
                "Hypothermia" => L("history.cause.Hypothermia"),
                "LimbSevered" => L("history.cause.LimbSevered"),
                "Preyed" => L("history.cause.Preyed"),
                "PreyFoughtBack" => L("history.cause.PreyFoughtBack"),
                "SharkBite" => L("history.cause.SharkBite"),
                "StarvedToDeath" => L("history.cause.StarvedToDeath"),
                "Sunburn" => L("history.cause.Sunburn"),
                "VitalPartDestroyed" => L("history.cause.VitalPartDestroyed"),
                _ => type
            };
            return L("history.detail.Cause", localized);
        }

        private static GameHistoryTone Tone(string type)
        {
            if (type is "Aided" or "AidRequested" or "AidStarted" or "TalkCompleted" or "TalkRequested" or
                "TalkStarted" or "FoodShared" or "RelationshipChanged" or "Mourned" or "VisitedGrave")
            {
                return GameHistoryTone.Social;
            }

            if (type.StartsWith("Crafted", StringComparison.Ordinal) ||
                type is "TreeChopped" or "BoulderBroken" or "LogSplit" or "CrownChopped" or
                    "BuildProgress" or "FurnitureBuilt" or "HutCompleted" or "RaftProgress" or "RaftLaunched")
            {
                return GameHistoryTone.Build;
            }

            if (type is "NpcDied" or "BledOut" or "StarvedToDeath" or "VitalPartDestroyed" or
                "DogFight" or "NightRaid" or "Murdered" or "Preyed" or "SharkBite" or "LimbSevered")
            {
                return GameHistoryTone.Danger;
            }

            if (type is "AidWaitTimeout" or "TalkQuarreled" or "TalkWaitTimeout" or "InteractionBlocked" or
                "InteractionRejected" or "FoodStolen" or "Grieving" or "StatusStarving" or "StatusDehydrated" or
                "StatusOverheated" or "Sunburn" or "Fainted")
            {
                return GameHistoryTone.Bad;
            }

            return GameHistoryTone.Neutral;
        }

        private static string FormatTime(int tick)
        {
            var day = tick / EnvironmentSystem.DayLengthTicks + 1;
            var progress = (tick % EnvironmentSystem.DayLengthTicks) /
                           (float)EnvironmentSystem.DayLengthTicks;
            var clock = EnvironmentSystem.FormatClock(progress);
            return L("history.time.day", day, clock);
        }

        private static string Npc(int? id) =>
            id.HasValue ? $"NPC #{id.Value.ToString(CultureInfo.InvariantCulture)}" : L("history.npc.colony");

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

        private static string AidKind(string kind) => kind switch
        {
            "Feed" => L("history.aid.Feed"),
            "Treat" => L("history.aid.Treat"),
            "Medicate" => L("history.aid.Medicate"),
            "Console" => L("history.aid.Console"),
            _ => kind
        };

        private static string TalkTopic(string topic) => topic switch
        {
            "SmallTalk" => L("history.topic.SmallTalk"),
            "Escape" => L("history.topic.Escape"),
            "Sharks" => L("history.topic.Sharks"),
            "Dogs" => L("history.topic.Dogs"),
            "Weather" => L("history.topic.Weather"),
            "Food" => L("history.topic.Food"),
            "Fire" => L("history.topic.Fire"),
            "Home" => L("history.topic.Home"),
            "Gossip" => L("history.topic.Gossip"),
            "Flirt" => L("history.topic.Flirt"),
            "Joke" => L("history.topic.Joke"),
            "Grumble" => L("history.topic.Grumble"),
            _ => topic
        };

        private static string NpcArrowDetail(string message)
        {
            var first = FirstNpcIn(message, null);
            var second = TargetFromArrow(message, string.Empty);
            return string.IsNullOrEmpty(second)
                ? CleanDetail(message)
                : T($"{first} -> {second}", $"{first} -> {second}");
        }

        private static string BusyObjectDetail(string message)
        {
            var occupiedBy = Token(message, "occupied by ");
            if (string.IsNullOrEmpty(occupiedBy))
            {
                return CleanDetail(message);
            }

            return L("history.detail.OccupiedBy", occupiedBy);
        }

        private static string CleanDetail(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return message.Length <= 160 ? message : message.Substring(0, 157) + "...";
        }

        private static string L(string key, params object[] args)
        {
            var template = Loc.Get(key);
            return args.Length == 0
                ? template
                : string.Format(CultureInfo.InvariantCulture, template, args);
        }
    }
}
