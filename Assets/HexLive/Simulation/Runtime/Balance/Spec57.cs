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
}

}
