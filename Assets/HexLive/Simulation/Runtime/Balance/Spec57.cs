using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Combat help cry: a badly hurt NPC who starts fleeing can call nearby
// housemates. Responders decide from compassion + relationship, then run at the
// attacker instead of treating this as ordinary non-combat Aid.
public static class Spec57
{
    public static bool HelpCryEnabled = true;
    public static int HelpCryRadiusTiles = 6;
    public static int HelpCryCooldownTicks = 240;
    public static int MaxHelpCryResponders = 2;
    public static float HelpCryDecisionThreshold = 0.45f;
    public static float HelpCryHealthGate = 0.65f;

    // §57.9: крик — ОТ БЕДЫ, а не от бегства. Замер (5 сидов × 16000 тиков):
    // 10 смертей, 7 криков, 0 отвеченных — потому что крик жил в хвосте
    // успешного TryStartFlee, и самые обречённые (загнанная, калека без
    // достижимого refuge, умирающая после боя) молчали. Теперь крик издаёт
    // сам укус/удар, когда драка уже плохая: здоровье ниже HurtHealth, худшая
    // часть ниже HurtPart или противников двое+. Кулдаун прежний, так что
    // загнанная кричит каждые ~240 тиков, пока её грызут, — и это правильно.
    public static bool HelpCryOnHitEnabled = true;
    public static float HelpCryHurtHealth = 0.85f;
    public static float HelpCryHurtPart = 0.7f;

    // §57.9: «СВОИХ В БЕДЕ НЕ БРОСАЮТ». Член дружбы в счёте отклика получает
    // пол: вражда перестаёт быть причиной не спасать от волка (прецедент —
    // §72, где против чужака гейт дружбы снят целиком). Близость по-прежнему
    // добавляет сверху, а личность (CompassionTrait) остаётся главным рычагом.
    // Порог решения опущен 0.56 → 0.45 той же правкой: со старым порогом
    // средняя девушка (черта 0.5) детерминированно игнорировала нейтральную
    // знакомую (score 0.50) — «лояльнее» не могло наступить ни при какой дружбе.
    public static float HelpCryAffinityFloor = 0.5f;

    // §57.9: смертельный крик ГРОМЧЕ. Жертва при смерти / лежащая / с
    // здоровьем или частью ниже MortalPlight слышна дальше, собирает больше
    // рук и получает надбавку к счёту каждой слышащей.
    public static float HelpCryMortalPlight = 0.35f;
    public static int HelpCryMortalRadiusTiles = 10;
    public static int MaxMortalCryResponders = 4;
    public static float HelpCryMortalBonus = 0.25f;

    // §57.9: спасение — событие для ОБЕИХ. Отбитая от волка/налётчика помнит,
    // КТО пришёл на крик, а пришедшая — за кого дралась: взаимный подъём
    // отношений при снятой угрозе (зверь мёртв или враг отступил при живой
    // подмоге). Калибр: разговор даёт 0.02, ссора отнимает 0.18 — спасённая
    // жизнь весит больше ссоры, «она за меня дралась» не смывается перепалкой.
    public static float RescueGratitudeAffinity = 0.2f;

    // §57.10: стон умирающей ВНЕ боя. Волки ушли, она истекает — раньше
    // молчала навсегда (крик жил только в боевых ветках), и помощь §53
    // находила её лишь глазами. Теперь лежащая в тяжести и В СОЗНАНИИ раз в
    // HelpCryCooldownTicks стонет: союзницы в смертельном радиусе обновляют
    // память о ней (как будто видели только что), и §53.8-помощь по памяти
    // без дисконта ведёт их сама. Без сознания стона нет — как и кью (§60).
    public static bool DyingMoanEnabled = true;

    // 29C.4B friend-guard: no cry needed — a friend who is close enough to
    // see the fight drops everything and goes for the aggressor. Friendship
    // is the trigger (affinity gate), not a compassion roll.
    public static bool FriendGuardEnabled = true;
    public static int FriendGuardRadiusTiles = 6;
    public static float FriendGuardAffinity = 0.25f;

    // §109: вписаться — РЕШЕНИЕ, а не рефлекс. Дружба сама по себе больше не
    // тащит в драку: свидетельница взвешивает, насколько дорога та, кого бьют
    // (симпатия), каковы её шансы против ЭТОГО противника (размен Force) и в
    // каком она сама состоянии (здоровье/худшая часть). Сумма весов против
    // порога + детерминированный бросок на ОКНО боя, не на тик — иначе
    // многократный переброс превращал бы любой порог в «рано или поздно да».
    public static float FriendGuardAffinityWeight = 0.45f;
    public static float FriendGuardEdgeWeight = 0.35f;
    public static float FriendGuardConditionWeight = 0.20f;

    // Злость на самого обидчика добавляет храбрости поверх расчёта — та же
    // идея, что в AbuseMath.AnswersBack.
    public static float FriendGuardHatredBonus = 0.15f;

    public static float FriendGuardDecisionFloor = 0.50f;

    // Ниже — сама еле живая: умирающие и разбитые не вписываются никогда.
    public static float FriendGuardHealthGate = 0.50f;

    // «Сила» зверя в единицах AbuseMath.Force — против собаки шансы меряются
    // об эту константу (у зверей нет оружия и брони, Force для них не считается).
    public static float FriendGuardDogForce = 1.0f;

    // §109: ЕСЛИ ТЕБЯ БЬЮТ — БЕЙ В ОТВЕТ. Сцепленный с тобой противник
    // (CombatOpponentNpcId на тебя) — значит бросай дела, доставай лучшее
    // оружие и отвечай; погоня (Defend/GroupHunt с тобой как целью) ближе
    // этого радиуса — встань в стойку заранее, не подставляя спину сборщика.
    public static bool AnswerBlowsEnabled = true;
    public static int AnswerReadyRadiusTiles = 3;

    // §109.13: мёртвая зона доворота к подходящему обидчику, градусы. Без неё
    // жертва подруливала за бегущим на КАЖДОМ среднем тике — «крутится
    // туда-сюда». Смотрит в его сторону, а не ведёт его прицелом.
    public static float BraceFaceDeadzoneDegrees = 35f;

    // §71: SPRINT TO THE RESCUE. While the goal is Defend — a help cry, the
    // friend guard, or the §62 first strike — she moves at this multiple of
    // her normal pace, so "run at the attacker" finally means running. It does
    // NOT compound with the adrenaline sprint: MovementSystem takes the larger
    // of the two, or a defender who had just been bitten would cover the camp
    // in a couple of ticks.
    public static float DefendMoveSpeedFactor = 2.5f;
}

}
