using System;
using System.Collections.Generic;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Which trace events are things that HAPPENED IN THE WORLD, as opposed to the
/// simulation thinking out loud.
/// <para>
/// The distinction is not cosmetic. The simulation emits ~244 event types and
/// roughly 174 events a tick, but the overwhelming majority are AI introspection —
/// <c>GoalScored</c> (about 55 per colonist per decision), <c>PerceivedObject</c>,
/// <c>PlanCandidate</c>, <c>DecisionInput</c>. Nothing consumes them: the game
/// reads only the list below, for the colony history, the sound layer and the
/// spoken lines. In a normal tick, <b>zero to three</b> events on this list fire.
/// </para>
/// <para>
/// Locally that is merely wasted work. On a server it is bandwidth: unfiltered,
/// the event stream costs ~66 KB/s per viewer — a third of everything sent.
/// Filtering to this list takes it to roughly 2 KB/s.
/// </para>
/// <para>
/// ⚠️ <b>Filter by TYPE only — never touch <c>Message</c>.</b> It is parsed, not
/// just displayed: <c>SoundManager.TryMobPosFromMessage</c> digs the wolf id out
/// of <c>"Dog={id} …"</c>, which is the ONLY source of a 3D position for
/// <c>DogAggro</c> and <c>DogKilled</c> (both are system events with no
/// <c>EntityId</c>), and <c>GameHistoryFormatter</c> parses <c>Kind=</c>,
/// <c>Topic=</c>, <c>Cause=[…]</c>, <c>Aff=</c> and <c>-&gt;NPC</c> out of a dozen more.
/// </para>
/// <para>
/// This list lives in the SIMULATION assembly, not in the Unity layer, for the
/// same reason <see cref="SimulationSystemRegistry"/> does: the game, the server
/// and headless probes must not be able to disagree about it. A server filtering
/// by a stale copy would silently drop events the client needs.
/// </para>
/// </summary>
public static class GameEventTypes
{
    /// <summary>
    /// Everything a player could plausibly see, hear or read about.
    /// <para>
    /// ⚠️ <b>Every name here must be a type the simulation actually emits.</b> A
    /// name nobody emits is dead weight that looks like coverage — and this list
    /// had two of them for a long time: it waited for <c>Collapsed</c> while the
    /// coma path emitted <c>FellAsleepExhausted</c>/<c>FaintedBloodLoss</c>, and
    /// for <c>MeatCooked</c> while the fire emitted <c>MeatRoasted</c>. Comas and
    /// cooking therefore never reached the colony history or the sound layer at
    /// all. The probe's whitelist gate scans the simulation sources for
    /// <c>Trace.Emit</c> literals and fails on any name here that is not among
    /// them — run it after touching this list.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> PlayerVisible = new(StringComparer.Ordinal)
    {
        // Aid and medicine
        "Aided",
        "AidStarted",
        "AidRequested",
        "AidWaitTimeout",
        "Bandaged",
        "BandageCrafted",
        "Medicated",
        "GotSick",

        // Gathering and crafting
        "BedCrafted",
        "BottleFilled",
        "BoulderBroken",
        "BuildProgress",
        "CoconutDrank",
        "CoconutEaten",
        "CoconutProcessed",
        "CrownChopped",
        "DrankBottle",
        "FurnitureBuilt",
        "HutCompleted",
        "LogSplit",
        "RackCrafted",
        "TentCrafted",
        "TreeChopped",

        // Fire and food
        "FireFueled",
        "FireLit",
        "FireOut",
        // Was "MeatCooked", which nothing has ever emitted — the fire emits this.
        "MeatRoasted",
        "MeatHungOnSpit",
        "MeatSpoiled",
        // §54.17: the meat chain's finish line — someone actually ate a roast.
        "MeatEaten",
        "FoodShared",
        "FoodStolen",

        // Weather and environment
        "RainStarted",
        "RainStopped",
        "StormSurge",
        "Sunburn",
        // NOT Heatstroke/Hypothermia: those fire on EVERY slow tick while thermal
        // damage is being taken — an episode would write a line every 4 seconds.
        // The edge-triggered "StatusOverheated" above already reports the moment
        // it begins, which is the part a player wants to know.

        // Wildlife and violence
        "DogAggro",
        "DogGaveUp",
        "DogFight",
        "DogKilled",
        "DogShot",
        "SharkBite",
        "PredatorKilled",
        "PreyFled",
        "PreyFoughtBack",
        "Preyed",
        "Murdered",
        "ThreatSpotted",
        "StandoffReleased",
        "NightRaid",
        "FriendGuard",
        // §135: зверь унёс оторванную конечность и доел её. Обе строки нужны
        // игроку по одной причине: они объясняют, почему бой оборвался на
        // середине и почему следующие полдня никто не нападает.
        "MobTookLimb",
        "MobAteLimb",
        // §46 v4: парная строка к NightRaid — стая пришла, стая ушла. Игрок
        // иначе не узнаёт, почему давление спало. НЕ путать с "MobLeaving":
        // то запись планировщика («уйдёт на тике N»), она осталась отладкой.
        "MobLeft",

        // Injury and death
        // NOT "Bleeding": it fires on every slow tick while any bad wound is
        // open — a five-minute untreated bleed would be ~75 identical history
        // lines. "WoundInflicted" reports the moment of injury, "BledOut" the
        // outcome; the ongoing state is visible on the health bar.
        "BledOut",
        // §60.7: без сознания в глубокой воде дольше DrownDeathTicks — утонула.
        "Drowned",
        "WoundInflicted",
        "LimbSevered",
        "VitalPartDestroyed",
        "StarvedToDeath",
        "NpcDied",
        // §28.15C v3: "Buried"/"VisitedGrave" сняты вместе с механикой похорон —
        // их больше никто не эмитит, а имя в этом списке ждало бы события,
        // которого не бывает (ровно то, что ловит EventWhitelistGate).
        "Butchered",
        "Grieving",
        "Mourned",
        "Looted",
        // §111: обыскал лежащего врага. ОДНО событие на сцену, а не на вещь:
        // по событию на нож один обыск занимал бы всю ленту.
        "StrippedHelpless",
        // Was "Collapsed", which nothing has ever emitted — the coma path emits these two.
        "FellAsleepExhausted",
        "FaintedBloodLoss",
        "Fainted",
        // §110: стресс-ветка того же краха — легла и рыдает (в сознании).
        "CryingBreakdown",
        "WokeUp",
        // §105: «Collapsed» вернулось в список — но теперь его ДЕЙСТВИТЕЛЬНО
        // эмитят (MortalityHelpers.EnterDying), и это самое громкое событие,
        // какое бывает: кто-то упал и умирает. «Rescued» — счастливый конец
        // той же сцены. Промежуточный "Dying" сюда НЕ входит намеренно: он
        // тикает каждый медленный тик и залил бы историю.
        "Collapsed",
        "Rescued",
        "ShipwreckSurvivorAppeared",
        "ShipwreckSurvivorJoined",

        // Distress
        "DireStraits",
        "EmergencyUnload",
        "HelpCry",
        // §57.10: слабый зов лежащей умирающей — раз в кулдаун крика, историю
        // не заливает (в отличие от потикового "Dying" выше).
        "HelpMoan",
        "HelpCryAnswered",
        "HelpCryAssistArrived",
        "HelpCryAssistExpired",
        "HelpCryAssistHolding",
        "HelpCryAssistLost",
        "HelpCryAssistStarted",
        "HelpCryDefended",
        "HelpCryIgnored",
        "StatusDehydrated",
        "StatusOverheated",
        "StatusStarving",

        // Social
        "RelationshipChanged",
        // §153: игрок подарил вещь — и получательница на неё ответила.
        "GiftGiven",
        "RomanceCompleted",
        "RomanceForced",
        "TalkCompleted",
        // §146.12: два самостоятельных лагеря договорились жить вместе.
        "CampsMerged",
        "TalkQuarreled",
        "TalkRequested",
        "TalkStarted",
        "TalkWaitTimeout",
        "InteractionBlocked",
        "InteractionRejected",

        // §133: одежда персонализирована, и чужую просят каждый раз заново.
        // Оба ответа игроку видны: это сюжет колонии, а не отладка.
        "WearPermissionGranted",
        "WearPermissionRefused",
        "ClothesStowed",

        // §108: групповая охота на чужака. Вся дуга видима игроку — сговор,
        // первое столкновение, его бегство и исход; «кто держит строй» и
        // «почему сговор не сложился» остаются отладочными.
        "GroupHuntPactFormed",
        "GroupHuntEngaged",
        "GroupHuntStruck",
        "GroupHuntTargetFled",
        "GroupHuntDone",
        "GroupHuntFailed",

        // Escape
        "RaftLaunched",
        "RaftProgress",
    };

    /// <summary>
    /// The <c>Crafted*</c> family is matched by prefix rather than listed: the
    /// recipes are data, and a new one should show up in the history without a
    /// code change here.
    /// </summary>
    private const string CraftedPrefix = "Crafted";

    public static bool IsPlayerVisible(string type) =>
        !string.IsNullOrEmpty(type) &&
        (PlayerVisible.Contains(type) || type.StartsWith(CraftedPrefix, StringComparison.Ordinal));

    public static bool IsPlayerVisible(SimulationEvent simulationEvent) =>
        simulationEvent != null && IsPlayerVisible(simulationEvent.Type);

    /// <summary>
    /// §121.5/§121.7: ответы на приказы игрока, которые обязаны ДОЕХАТЬ до
    /// зрителя по проводу (тосты сноса приказа и возврата под ИИ), но НЕ
    /// являются летописью: в историю колонии и звук они не попадают, поэтому
    /// список ОТДЕЛЬНЫЙ от <see cref="PlayerVisible"/>. Серверный фильтр
    /// событий пропускает объединение двух списков.
    /// <para>
    /// <c>ManualOrderRejected</c> здесь НЕТ намеренно: отказ едет синхронным
    /// кадром CommandResult своему автору — в потоке событий у него не было бы
    /// адресата, и отказ одного игрока видели бы все зрители.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ManualOrderReplies = new(StringComparer.Ordinal)
    {
        "ManualOrderInterrupted",
        "ManualControlExpired",
        "GroupOrderResult",
    };

    public static bool IsManualOrderReply(string type) =>
        !string.IsNullOrEmpty(type) && ManualOrderReplies.Contains(type);

    /// <summary>§83: что серверу класть в канал событий для зрителей —
    /// летопись плюс ответы на приказы.</summary>
    public static bool IsWireRelevant(SimulationEvent simulationEvent) =>
        simulationEvent != null &&
        (IsPlayerVisible(simulationEvent.Type) || IsManualOrderReply(simulationEvent.Type));

    /// <summary>The listed names, for the drift gate. Does not include the prefix rule.</summary>
    public static IReadOnlyCollection<string> ListedTypes => PlayerVisible;
}

}
