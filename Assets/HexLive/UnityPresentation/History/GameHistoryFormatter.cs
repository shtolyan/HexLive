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
                "AidStarted" => T($"{actor} went to help {target}.", $"{actor} пошла помогать {target}."),
                "AidRequested" => T($"{actor} noticed {target} needs help.",
                    $"{actor} заметила, что {target} нужна помощь."),
                "AidWaitTimeout" => T($"{actor} stopped waiting for help.",
                    $"{actor} перестала ждать помощи."),
                "Aided" => T($"{actor} helped {TargetFromArrow(record.Message, target)}.",
                    $"{actor} помогла {TargetFromArrow(record.Message, target)}."),
                "RelationshipChanged" => FormatRelationship(record, actor),
                "TalkRequested" => T($"{actor} invited {target} to talk.",
                    $"{actor} позвала {target} поговорить."),
                "TalkStarted" => T($"{actor} started a conversation with {target}.",
                    $"{actor} начала разговор с {target}."),
                "TalkCompleted" => T($"{actor} had a good talk with {target}.",
                    $"{actor} хорошо поговорила с {target}."),
                "TalkQuarreled" => T($"{actor} quarreled with {target}.",
                    $"{actor} поссорилась с {target}."),
                "TalkWaitTimeout" => T($"{actor} stopped waiting for the conversation.",
                    $"{actor} перестала ждать разговора."),
                "InteractionBlocked" => T($"{actor} could not use a busy object.",
                    $"{actor} не смогла воспользоваться занятым объектом."),
                "InteractionRejected" => T($"{actor}'s talk was refused.",
                    $"{actor} отказали в разговоре."),
                "FoodShared" => T($"{actor} received food from {target}.",
                    $"{actor} получила еду от {target}."),
                "FoodStolen" => T("Food was stolen in the camp.", "В лагере украли еду."),
                "Grieving" => T($"{actor} is grieving.", $"{actor} скорбит."),
                "Mourned" => T($"{actor} paid respects to the dead.", $"{actor} почтила память погибшей."),
                "VisitedGrave" => T($"{actor} visited a grave.", $"{actor} посетила могилу."),
                "NpcDied" => T($"{actor} died.", $"{actor} погибла."),
                "BledOut" => T($"{actor} bled out.", $"{actor} истекла кровью."),
                "StarvedToDeath" => T($"{actor} died from hunger or thirst.",
                    $"{actor} умерла от голода или жажды."),
                "VitalPartDestroyed" => T($"{actor} suffered a fatal wound.",
                    $"{actor} получила смертельную рану."),
                "DogFight" => T($"{actor} was attacked by a dog.", $"{actor} атакована собакой."),
                "DogAggro" => T("A wild dog noticed the camp.", "Дикая собака заметила лагерь."),
                "DogKilled" => T("A dog was killed.", "Собаку убили."),
                "DogShot" => T($"{actor} fired at a dog.", $"{actor} выстрелила в собаку."),
                "HelpCry" => T($"{actor} called for help.", $"{actor} позвала на помощь."),
                "HelpCryAnswered" => T($"{actor} answered a call for help.",
                    $"{actor} откликнулась на клич о помощи."),
                "HelpCryIgnored" => T($"{actor} did not answer the call for help.",
                    $"{actor} не откликнулась на клич о помощи."),
                "HelpCryAssistStarted" => T($"{actor} ran to defend a housemate.",
                    $"{actor} побежала защищать соседку."),
                "HelpCryAssistArrived" => T($"{actor} joined the fight.",
                    $"{actor} вступила в бой."),
                "HelpCryAssistLost" => T($"{actor} lost the attacker.",
                    $"{actor} потеряла нападающего."),
                "HelpCryDefended" => T($"{actor} struck the attacker.",
                    $"{actor} ударила нападающего."),
                "FriendGuard" => T($"{actor} rushed to defend a friend.",
                    $"{actor} бросилась защищать подругу."),
                "NightRaid" => T("Dogs raided the camp at dusk.", "На закате на лагерь напали собаки."),
                "Preyed" => T($"{actor} attacked a housemate.", $"{actor} напала на соседку."),
                "PreyFoughtBack" => T($"{actor} fought back.", $"{actor} дала отпор."),
                "PreyFled" => T($"{actor} fled from an attacker.", $"{actor} сбежала от нападавшей."),
                "PredatorKilled" => T("An attacker was killed in self-defence.",
                    "Нападавшую убили при самообороне."),
                "Murdered" => T("A housemate was killed for meat.", "Соседку убили ради мяса."),
                "SharkBite" => T($"{actor} was bitten by a shark.", $"{actor} укусила акула."),
                "LimbSevered" => T($"{actor} lost a limb.", $"{actor} потеряла конечность."),
                "Fainted" => T($"{actor} fainted.", $"{actor} потеряла сознание."),
                "Collapsed" => T($"{actor} collapsed into a coma.", $"{actor} впала в кому."),
                "WokeUp" => T($"{actor} came to.", $"{actor} пришла в себя."),
                "Bandaged" => T($"{actor} dressed the wounds.", $"{actor} перевязала раны."),
                "Medicated" => T($"{actor} used medicine.", $"{actor} приняла лекарство."),
                "StatusStarving" => T($"{actor} is starving.", $"{actor} голодает."),
                "StatusDehydrated" => T($"{actor} is dehydrated.", $"{actor} обезвожена."),
                "StatusOverheated" => T($"{actor} is overheating.", $"{actor} перегревается."),
                "Sunburn" => T($"{actor} was burned by the sun.", $"{actor} обгорела на солнце."),
                "RainStarted" => T("Rain started.", "Начался дождь."),
                "RainStopped" => T("Rain stopped.", "Дождь закончился."),
                "StormSurge" => T("A storm surge hit the island.", "Штормовой нагон ударил по острову."),
                "TreeChopped" => T($"{actor} felled a tree.", $"{actor} срубила дерево."),
                "BoulderBroken" => T($"{actor} broke a boulder.", $"{actor} разбила валун."),
                "LogSplit" => T($"{actor} split a log into sticks.", $"{actor} расколола бревно на палки."),
                "CrownChopped" => T($"{actor} cut palm leaves.", $"{actor} нарубила пальмовых листьев."),
                "CoconutProcessed" => T($"{actor} opened a coconut.", $"{actor} вскрыла кокос."),
                "CoconutDrank" => T($"{actor} drank coconut water.", $"{actor} выпила кокосовую воду."),
                "CoconutEaten" => T($"{actor} ate coconut.", $"{actor} съела кокос."),
                "BottleFilled" => T($"{actor} filled a bottle.", $"{actor} наполнила бутылку."),
                "DrankBottle" => T($"{actor} drank from a bottle.", $"{actor} попила из бутылки."),
                "FireLit" => T($"{actor} lit the fire.", $"{actor} разожгла костер."),
                "FireFueled" => T($"{actor} fed the fire.", $"{actor} подбросила топлива в костер."),
                "FireOut" => T("The fire went out.", "Костер погас."),
                "RaftProgress" => T($"{actor} worked on the raft.", $"{actor} строит плот."),
                "RaftLaunched" => T("The raft is finished.", "Плот готов."),
                "BuildProgress" => T($"{actor} built part of the shelter.", $"{actor} построила часть укрытия."),
                "FurnitureBuilt" => T($"{actor} finished furniture.", $"{actor} закончила мебель."),
                "HutCompleted" => T("The hut is complete.", "Хижина достроена."),
                "BedCrafted" => T($"{actor} made a bed.", $"{actor} сделала постель."),
                "TentCrafted" => T($"{actor} made a sun shelter.", $"{actor} сделала навес от солнца."),
                "RackCrafted" => T($"{actor} made a drying rack.", $"{actor} сделала сушилку."),
                "Buried" => T($"{actor} buried the dead.", $"{actor} похоронила погибшую."),
                "Butchered" => T($"{actor} butchered a body.", $"{actor} разделала тушу."),
                "EmergencyUnload" => T($"{actor} dropped supplies to make room for food.",
                    $"{actor} сбросила припасы, чтобы освободить место для еды."),
                "DireStraits" => T("The colony is in crisis.", "Колония в критическом состоянии."),
                _ when record.Type.StartsWith("Crafted", StringComparison.Ordinal) =>
                    T($"{actor} crafted something useful.", $"{actor} что-то смастерила."),
                _ => T($"{actor}: {record.Type}", $"{actor}: {record.Type}")
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
                return T($"{actor} warmed toward {target}.", $"{actor} стала теплее к {target}.");
            }

            if (delta.StartsWith("-", StringComparison.Ordinal))
            {
                return T($"{actor} grew colder toward {target}.", $"{actor} стала хуже относиться к {target}.");
            }

            return T($"{actor}'s relationship with {target} changed.",
                $"Отношение {actor} к {target} изменилось.");
        }

        private static string Detail(GameHistoryRecord record)
        {
            return record.Type switch
            {
                "AidStarted" => T($"Help: {AidKind(Token(record.Message, "Kind="))}",
                    $"Помощь: {AidKind(Token(record.Message, "Kind="))}"),
                "AidRequested" => T($"Help: {AidKind(Token(record.Message, "Kind="))}",
                    $"Помощь: {AidKind(Token(record.Message, "Kind="))}"),
                "AidWaitTimeout" => T("The promised helper did not arrive in time.",
                    "Помощница не успела прийти вовремя."),
                "Aided" => T($"Help: {AidKind(Token(record.Message, "Kind="))}. Relationship improved.",
                    $"Помощь: {AidKind(Token(record.Message, "Kind="))}. Отношения улучшились."),
                "TalkRequested" => T($"Affinity before invite: {Token(record.Message, "Affinity=")}",
                    $"Отношение перед приглашением: {Token(record.Message, "Affinity=")}"),
                "TalkStarted" => T($"Topic: {TalkTopic(Token(record.Message, "Topic="))}",
                    $"Тема: {TalkTopic(Token(record.Message, "Topic="))}"),
                "TalkCompleted" => T("Social need rose. Relationship improved.",
                    "Общение стало лучше. Отношения улучшились."),
                "TalkQuarreled" => T("Both gained some social contact, but affinity fell.",
                    "Общение случилось, но отношения ухудшились."),
                "TalkWaitTimeout" => T("The invited NPC did not arrive in time.",
                    "Собеседница не успела подойти вовремя."),
                "RelationshipChanged" => RelationshipDetail(record.Message),
                "InteractionBlocked" => BusyObjectDetail(record.Message),
                "InteractionRejected" => T("A personal refusal can hurt affinity.",
                    "Личный отказ может ухудшить отношения."),
                "NpcDied" => DeathDetail(record.Message),
                "FoodStolen" => NpcArrowDetail(record.Message),
                "Murdered" => NpcArrowDetail(record.Message),
                "HelpCry" => HelpCryDetail(record.Message),
                "HelpCryAnswered" => HelpCryDecisionDetail(record.Message),
                "HelpCryIgnored" => HelpCryDecisionDetail(record.Message),
                "HelpCryDefended" => CleanDetail(record.Message),
                "RaftProgress" => record.Message,
                "DireStraits" => T("Several survivors need urgent attention.",
                    "Нескольким выжившим срочно нужна помощь."),
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
                ? T($"Affinity now {affinity}.", $"Отношение теперь {affinity}.")
                : T($"Affinity now {affinity} ({delta}).", $"Отношение теперь {affinity} ({delta}).");
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
                "BledOut" => T("blood loss", "кровопотеря"),
                "DogFight" => T("dog attack", "нападение собаки"),
                "Heatstroke" => T("heatstroke", "перегрев"),
                "Hypothermia" => T("hypothermia", "переохлаждение"),
                "LimbSevered" => T("lost limb", "потеря конечности"),
                "Preyed" => T("housemate attack", "нападение соседки"),
                "PreyFoughtBack" => T("self-defence fight", "бой при самообороне"),
                "SharkBite" => T("shark bite", "укус акулы"),
                "StarvedToDeath" => T("hunger or thirst", "голод или жажда"),
                "Sunburn" => T("sun exposure", "солнце"),
                "VitalPartDestroyed" => T("fatal wound", "смертельная рана"),
                _ => type
            };
            return T($"Cause: {localized}.", $"Причина: {localized}.");
        }

        private static GameHistoryTone Tone(string type)
        {
            if (type is "Aided" or "AidRequested" or "AidStarted" or "TalkCompleted" or "TalkRequested" or
                "TalkStarted" or "FoodShared" or "RelationshipChanged" or "Mourned" or "VisitedGrave" or
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

            if (type is "NpcDied" or "BledOut" or "StarvedToDeath" or "VitalPartDestroyed" or
                "DogFight" or "NightRaid" or "Murdered" or "Preyed" or "SharkBite" or "LimbSevered" or
                "HelpCry" or "HelpCryAssistStarted" or "HelpCryAssistArrived" or "HelpCryDefended")
            {
                return GameHistoryTone.Danger;
            }

            if (type is "AidWaitTimeout" or "TalkQuarreled" or "TalkWaitTimeout" or "InteractionBlocked" or
                "InteractionRejected" or "FoodStolen" or "Grieving" or "StatusStarving" or "StatusDehydrated" or
                "StatusOverheated" or "Sunburn" or "Fainted" or "Collapsed" or
                "HelpCryIgnored" or "HelpCryAssistLost")
            {
                return GameHistoryTone.Bad;
            }

            return GameHistoryTone.Neutral;
        }

        private static string FormatTime(int tick)
        {
            var day = EnvironmentSystem.CalendarDay(tick);
            var progress = (tick % EnvironmentSystem.DayLengthTicks) /
                           (float)EnvironmentSystem.DayLengthTicks;
            var clock = EnvironmentSystem.FormatClock(progress);
            return T($"Day {day}, {clock}", $"День {day}, {clock}");
        }

        private static string Npc(int? id) =>
            id.HasValue ? $"NPC #{id.Value.ToString(CultureInfo.InvariantCulture)}" : T("Colony", "Колония");

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
            "Feed" => T("food", "еда"),
            "Hydrate" => T("water", "вода"),
            "Treat" => T("treatment", "лечение ран"),
            "Medicate" => T("medicine", "лекарство"),
            "Console" => T("comfort", "утешение"),
            _ => kind
        };

        private static string TalkTopic(string topic) => topic switch
        {
            "SmallTalk" => T("small talk", "разговор ни о чем"),
            "Escape" => T("escape", "побег с острова"),
            "Sharks" => T("sharks", "акулы"),
            "Dogs" => T("dogs", "собаки"),
            "Weather" => T("weather", "погода"),
            "Food" => T("food", "еда"),
            "Fire" => T("fire", "костер"),
            "Home" => T("home", "дом"),
            "Gossip" => T("gossip", "слухи"),
            "Flirt" => T("flirting", "флирт"),
            "Joke" => T("jokes", "шутки"),
            "Grumble" => T("complaints", "ворчание"),
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

        private static string HelpCryDetail(string message)
        {
            var attacker = Token(message, "Dog=");
            if (!string.IsNullOrEmpty(attacker))
            {
                return T($"Attacker: dog #{attacker}.", $"Нападающий: собака #{attacker}.");
            }

            attacker = Token(message, "Attacker=NPC");
            return string.IsNullOrEmpty(attacker)
                ? CleanDetail(message)
                : T($"Attacker: NPC #{attacker}.", $"Нападающая: NPC #{attacker}.");
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

            return T($"For NPC #{victim}: score {score}, roll {roll}.",
                $"За NPC #{victim}: шанс {score}, бросок {roll}.");
        }

        private static string BusyObjectDetail(string message)
        {
            var occupiedBy = Token(message, "occupied by ");
            if (string.IsNullOrEmpty(occupiedBy))
            {
                return CleanDetail(message);
            }

            return T($"Occupied by {occupiedBy}.", $"Занято: {occupiedBy}.");
        }

        private static string CleanDetail(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return message.Length <= 160 ? message : message.Substring(0, 157) + "...";
        }

        private static string T(string en, string ru) =>
            Loc.Current == Language.Russian ? ru : en;
    }
}
