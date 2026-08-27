using System;
using System.Collections.Generic;

namespace HexLive.Simulation.Runtime.Journal
{

/// <summary>
/// Spec §136: откуда в событии берётся человек. Хроника не единообразна —
/// у одних событий действующее лицо в <c>EntityId</c>, у других жертва зашита
/// в <c>Message</c> (потому что событие системное), у третьих <c>EntityId</c> —
/// это ПОЛУЧАТЕЛЬ, а не деятель (<c>FoodShared</c> эмитится на того, кого
/// накормили). Роль объявляется в таблице, а не угадывается кодом.
/// </summary>
public enum JournalRole : byte
{
    None = 0,
    /// <summary>Тот, на кого эмитировано событие (<c>EntityId</c>).</summary>
    Emitter = 1,
    /// <summary>Первый <c>NPC&lt;цифры&gt;</c> в сообщении.</summary>
    FirstNpc = 2,
    /// <summary>Цель стрелки <c>-&gt;NPC&lt;цифры&gt;</c>.</summary>
    ArrowTarget = 3,
    /// <summary>Значение именованного токена (<c>Victim=</c>, <c>Mark=</c>, …).</summary>
    Token = 4
}

/// <summary>
/// Spec §136: с какой стороны автор пережил событие. Это НЕ украшение: «я
/// напоила Милу» и «Мила меня напоила» — одно событие и две совершенно разные
/// записи, и вторая — ровно то, ради чего дневник затевался.
/// </summary>
public enum JournalPerspective : byte
{
    Did = 0,
    Received = 1
}

/// <summary>Spec §136: откуда взять уточнение — предмет, конечность, причину.</summary>
public enum JournalExtra : byte
{
    None = 0,
    /// <summary>Первое слово сообщения: у трети событий id стоит именно там.</summary>
    LeadingWord = 1,
    Token = 2
}

/// <summary>Spec §136: правило для одного типа события.</summary>
public readonly struct JournalRule
{
    public JournalRule(
        int weight,
        JournalRole subject = JournalRole.None,
        string subjectToken = null,
        int mirrorWeight = 0,
        JournalPerspective perspective = JournalPerspective.Did,
        JournalExtra extra = JournalExtra.None,
        string extraToken = null)
    {
        Weight = weight;
        Subject = subject;
        SubjectToken = subjectToken;
        MirrorWeight = mirrorWeight;
        Perspective = perspective;
        Extra = extra;
        ExtraToken = extraToken;
    }

    public int Weight { get; }
    public JournalRole Subject { get; }
    public string SubjectToken { get; }

    /// <summary>
    /// Вес ЗЕРКАЛЬНОЙ записи — той, что уходит второй участнице с
    /// перевёрнутой перспективой. 0 — зеркала нет.
    /// </summary>
    public int MirrorWeight { get; }

    public JournalPerspective Perspective { get; }
    public JournalExtra Extra { get; }
    public string ExtraToken { get; }
}

/// <summary>
/// Spec §136: единственная таблица «насколько это важно и о ком это».
///
/// <para>
/// Веса разложены по ярусам <see cref="Spec136"/>: Major — про это пишут даже
/// в занятый час; Minor — если Major не случилось; Chore — в отдельную запись
/// не попадает никогда, только перечислением внутри тихой.
/// </para>
/// <para>
/// ⚠️ Атрибуция здесь выведена из РЕАЛЬНЫХ вызовов <c>Trace.Emit</c>, а не из
/// названий. Три ловушки, на которые она уже наступила бы:
/// <c>FoodShared</c> эмитится на того, кого НАКОРМИЛИ (деятель — в сообщении);
/// у <c>RelationshipChanged</c> первый <c>NPC</c> в сообщении — это САМ автор,
/// собеседница за стрелкой; <c>Preyed</c> пишет <c>Victim=</c> голым числом,
/// без префикса <c>NPC</c>.
/// </para>
/// <para>
/// Полноту стережёт <c>JournalCatalogGate</c>: каждое имя из
/// <see cref="GameEventTypes.ListedTypes"/> обязано быть здесь — с весом или с
/// явным нулём. Без гейта новое событие тихо перестало бы попадать в дневники,
/// и это заметили бы через полгода.
/// </para>
/// </summary>
public static class JournalCatalog
{
    private static readonly Dictionary<string, JournalRule> Rules = Build();

    /// <summary>Правило типа. Незнакомый тип — вес 0, то есть «в дневник не идёт».</summary>
    public static JournalRule For(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return default;
        }

        if (Rules.TryGetValue(type, out var rule))
        {
            return rule;
        }

        // Семейство Crafted* — по префиксу, как и в GameEventTypes: рецепты это
        // данные, и новый не должен требовать правки этой таблицы.
        return type.StartsWith("Crafted", StringComparison.Ordinal)
            ? new JournalRule(24, extra: JournalExtra.LeadingWord)
            : default;
    }

    public static bool Knows(string type) =>
        !string.IsNullOrEmpty(type) &&
        (Rules.ContainsKey(type) || type.StartsWith("Crafted", StringComparison.Ordinal));

    private static Dictionary<string, JournalRule> Build()
    {
        var r = new Dictionary<string, JournalRule>(StringComparer.Ordinal);

        // ── смерть и край (самое громкое, что бывает) ────────────────────────
        r["NpcDied"] = new JournalRule(100, extra: JournalExtra.Token, extraToken: "Cause=[");
        r["Collapsed"] = new JournalRule(98, perspective: JournalPerspective.Received,
            extra: JournalExtra.Token, extraToken: "Cause=");
        r["BledOut"] = new JournalRule(97, perspective: JournalPerspective.Received);
        r["StarvedToDeath"] = new JournalRule(97, perspective: JournalPerspective.Received);
        r["Drowned"] = new JournalRule(97, perspective: JournalPerspective.Received);
        r["VitalPartDestroyed"] = new JournalRule(96, perspective: JournalPerspective.Received,
            extra: JournalExtra.LeadingWord);
        r["LimbSevered"] = new JournalRule(95, perspective: JournalPerspective.Received,
            extra: JournalExtra.LeadingWord);
        r["Rescued"] = new JournalRule(92, perspective: JournalPerspective.Received,
            extra: JournalExtra.Token, extraToken: "Cause=");
        r["ShipwreckSurvivorAppeared"] = new JournalRule(
            92, perspective: JournalPerspective.Received);
        r["ShipwreckSurvivorJoined"] = new JournalRule(
            88, perspective: JournalPerspective.Received);
        r["FaintedBloodLoss"] = new JournalRule(88, perspective: JournalPerspective.Received);
        r["FellAsleepExhausted"] = new JournalRule(80, perspective: JournalPerspective.Received);
        r["Fainted"] = new JournalRule(80, perspective: JournalPerspective.Received);
        r["CryingBreakdown"] = new JournalRule(86, perspective: JournalPerspective.Received);
        r["WokeUp"] = new JournalRule(64, perspective: JournalPerspective.Received);

        // ── насилие ─────────────────────────────────────────────────────────
        // Хищница помнит, что сделала; жертва — что с ней сделали. Обе записи
        // нужны, и это ровно тот случай, ради которого есть зеркало.
        r["Preyed"] = new JournalRule(94, JournalRole.Token, "Victim=", mirrorWeight: 99);
        r["Murdered"] = new JournalRule(99, JournalRole.FirstNpc, mirrorWeight: 99);
        r["PreyFoughtBack"] = new JournalRule(90, JournalRole.Token, "Attacker=",
            perspective: JournalPerspective.Received);
        r["PreyFled"] = new JournalRule(88, JournalRole.FirstNpc,
            perspective: JournalPerspective.Received);
        r["DogFight"] = new JournalRule(78, extra: JournalExtra.Token, extraToken: "Dog=");
        r["DogShot"] = new JournalRule(66, extra: JournalExtra.Token, extraToken: "Dog=");
        r["DogKilled"] = new JournalRule(70);
        r["PredatorKilled"] = new JournalRule(72);
        r["DogAggro"] = new JournalRule(58);
        r["DogGaveUp"] = new JournalRule(30);
        r["StandoffReleased"] = new JournalRule(26);
        r["MobTookLimb"] = new JournalRule(95);
        r["MobAteLimb"] = new JournalRule(40);
        r["MobLeft"] = new JournalRule(34);
        r["NightRaid"] = new JournalRule(74);
        r["ThreatSpotted"] = new JournalRule(46, extra: JournalExtra.Token, extraToken: "Mob=");
        r["WoundInflicted"] = new JournalRule(72, perspective: JournalPerspective.Received,
            extra: JournalExtra.LeadingWord);

        // §108: расправа над чужаком. Загонщица и загнанная пишут разное.
        r["GroupHuntPactFormed"] = new JournalRule(76, JournalRole.Token, "Target=", mirrorWeight: 0);
        r["GroupHuntEngaged"] = new JournalRule(80, JournalRole.Token, "Target=", mirrorWeight: 88);
        r["GroupHuntStruck"] = new JournalRule(62, JournalRole.Token, "Target=");
        r["GroupHuntTargetFled"] = new JournalRule(84, perspective: JournalPerspective.Received);
        r["GroupHuntDone"] = new JournalRule(82);
        r["GroupHuntFailed"] = new JournalRule(58);

        // §111: обыск лежащего. У обысканной это не «пропажа ножа», а сцена.
        r["StrippedHelpless"] = new JournalRule(78, JournalRole.Token, "Mark=", mirrorWeight: 92);
        r["Looted"] = new JournalRule(38, JournalRole.FirstNpc);
        r["FoodStolen"] = new JournalRule(70, JournalRole.FirstNpc, mirrorWeight: 74);

        // ── помощь и забота: сердце фичи ────────────────────────────────────
        // «Мила меня напоила, когда мне было совсем худо» — это ЗЕРКАЛО Aided,
        // и весит оно больше оригинала: помнят добро к себе.
        r["Aided"] = new JournalRule(70, JournalRole.ArrowTarget, mirrorWeight: 90,
            extra: JournalExtra.Token, extraToken: "Kind=");
        r["AidStarted"] = new JournalRule(44, JournalRole.FirstNpc,
            extra: JournalExtra.Token, extraToken: "Kind=");
        r["AidRequested"] = new JournalRule(36, JournalRole.FirstNpc,
            extra: JournalExtra.Token, extraToken: "Kind=");
        r["AidWaitTimeout"] = new JournalRule(56, JournalRole.FirstNpc,
            perspective: JournalPerspective.Received);
        r["FoodShared"] = new JournalRule(86, JournalRole.FirstNpc, mirrorWeight: 66,
            perspective: JournalPerspective.Received, extra: JournalExtra.Token, extraToken: "Given ");
        r["FriendGuard"] = new JournalRule(80, JournalRole.Token, "Victim=", mirrorWeight: 94);
        r["HelpCryAnswered"] = new JournalRule(74, JournalRole.Token, "Victim=", mirrorWeight: 90);
        r["HelpCryIgnored"] = new JournalRule(50, JournalRole.Token, "Victim=", mirrorWeight: 84);
        r["HelpCryDefended"] = new JournalRule(84, JournalRole.Token, "Victim=", mirrorWeight: 94);
        r["HelpCry"] = new JournalRule(82, perspective: JournalPerspective.Received);
        r["HelpMoan"] = new JournalRule(80, perspective: JournalPerspective.Received);
        r["HelpCryAssistStarted"] = new JournalRule(40);
        r["HelpCryAssistArrived"] = new JournalRule(46);
        r["HelpCryAssistHolding"] = new JournalRule(22);
        r["HelpCryAssistExpired"] = new JournalRule(32);
        r["HelpCryAssistLost"] = new JournalRule(34);
        r["Bandaged"] = new JournalRule(48);
        r["Medicated"] = new JournalRule(50);
        r["BandageCrafted"] = new JournalRule(18);
        r["GotSick"] = new JournalRule(62, perspective: JournalPerspective.Received);

        // ── горе ────────────────────────────────────────────────────────────
        r["Grieving"] = new JournalRule(88, JournalRole.FirstNpc,
            perspective: JournalPerspective.Received);
        r["Mourned"] = new JournalRule(76, JournalRole.FirstNpc);
        r["Butchered"] = new JournalRule(56, extra: JournalExtra.LeadingWord);

        // ── социальное ──────────────────────────────────────────────────────
        r["RomanceCompleted"] = new JournalRule(
            88, JournalRole.FirstNpc, mirrorWeight: 88);
        // Дневниковая запись принадлежит жертве: вес инициатора равен нулю,
        // зеркало получает максимальный приоритет и называет виновника.
        r["RomanceForced"] = new JournalRule(
            0, JournalRole.FirstNpc, mirrorWeight: 100);
        r["TalkQuarreled"] = new JournalRule(68, JournalRole.FirstNpc, mirrorWeight: 68);
        r["TalkCompleted"] = new JournalRule(34, JournalRole.FirstNpc, mirrorWeight: 30);
        // §146.12: договор о едином доме — крупная запись обеих переговорщиц.
        r["CampsMerged"] = new JournalRule(
            90, JournalRole.Token, "Target=", mirrorWeight: 90);
        r["TalkStarted"] = new JournalRule(22, JournalRole.FirstNpc,
            extra: JournalExtra.Token, extraToken: "Topic=");
        r["TalkRequested"] = new JournalRule(16, JournalRole.FirstNpc);
        r["TalkWaitTimeout"] = new JournalRule(30, JournalRole.FirstNpc,
            perspective: JournalPerspective.Received);
        r["InteractionRejected"] = new JournalRule(52, JournalRole.FirstNpc,
            perspective: JournalPerspective.Received);
        r["InteractionBlocked"] = new JournalRule(12, JournalRole.FirstNpc);
        r["RelationshipChanged"] = new JournalRule(28, JournalRole.ArrowTarget);

        // §133: чужая одежда. Отказ помнят дольше, чем согласие.
        r["WearPermissionGranted"] = new JournalRule(44, JournalRole.FirstNpc, mirrorWeight: 32,
            perspective: JournalPerspective.Received);
        r["WearPermissionRefused"] = new JournalRule(58, JournalRole.FirstNpc, mirrorWeight: 26,
            perspective: JournalPerspective.Received);
        r["ClothesStowed"] = new JournalRule(10, extra: JournalExtra.LeadingWord);

        // ── тело и нужда ────────────────────────────────────────────────────
        r["StatusStarving"] = new JournalRule(64, perspective: JournalPerspective.Received);
        r["StatusDehydrated"] = new JournalRule(64, perspective: JournalPerspective.Received);
        r["StatusOverheated"] = new JournalRule(54, perspective: JournalPerspective.Received);
        r["Sunburn"] = new JournalRule(42, perspective: JournalPerspective.Received,
            extra: JournalExtra.LeadingWord);
        r["DireStraits"] = new JournalRule(72);
        r["EmergencyUnload"] = new JournalRule(36, extra: JournalExtra.LeadingWord);

        // ── погода: не про неё, но день окрашивает ──────────────────────────
        r["StormSurge"] = new JournalRule(50);
        r["RainStarted"] = new JournalRule(18);
        r["RainStopped"] = new JournalRule(8);

        // ── быт: в отдельную запись не идёт никогда, только в перечень ──────
        r["TreeChopped"] = new JournalRule(14, extra: JournalExtra.LeadingWord);
        r["CrownChopped"] = new JournalRule(12);
        r["LogSplit"] = new JournalRule(10);
        r["BoulderBroken"] = new JournalRule(12, extra: JournalExtra.LeadingWord);
        r["CoconutProcessed"] = new JournalRule(8, extra: JournalExtra.LeadingWord);
        r["CoconutDrank"] = new JournalRule(8);
        r["CoconutEaten"] = new JournalRule(8);
        r["MeatEaten"] = new JournalRule(16, extra: JournalExtra.LeadingWord);
        r["MeatRoasted"] = new JournalRule(14);
        r["MeatHungOnSpit"] = new JournalRule(10);
        r["MeatSpoiled"] = new JournalRule(18);
        r["BottleFilled"] = new JournalRule(6);
        r["DrankBottle"] = new JournalRule(6);
        r["FireLit"] = new JournalRule(19);
        r["FireFueled"] = new JournalRule(7);
        r["FireOut"] = new JournalRule(19);

        // ── стройка: гордость, но будничная ─────────────────────────────────
        r["BuildProgress"] = new JournalRule(12);
        r["FurnitureBuilt"] = new JournalRule(38);
        r["BedCrafted"] = new JournalRule(46);
        r["TentCrafted"] = new JournalRule(42);
        r["RackCrafted"] = new JournalRule(38);
        r["HutCompleted"] = new JournalRule(56);
        r["RaftProgress"] = new JournalRule(20);
        r["RaftLaunched"] = new JournalRule(90);

        return r;
    }
}

}
