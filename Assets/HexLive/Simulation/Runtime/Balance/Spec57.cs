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
    public static float HelpCryDecisionThreshold = 0.56f;
    public static float HelpCryHealthGate = 0.65f;

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

    // §71: SPRINT TO THE RESCUE. While the goal is Defend — a help cry, the
    // friend guard, or the §62 first strike — she moves at this multiple of
    // her normal pace, so "run at the attacker" finally means running. It does
    // NOT compound with the adrenaline sprint: MovementSystem takes the larger
    // of the two, or a defender who had just been bitten would cover the camp
    // in a couple of ticks.
    public static float DefendMoveSpeedFactor = 2.5f;
}

}
