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

// Spec §50: limb loss / amputation tuning. A survivor can lose an arm or a
// leg — for good — either emergently (a bite that overwhelms an already-mauled
// limb) or at a prepared hazard (reef/trap). The consequence is HARD: a heavy
// one-shot blood dump plus a deep, slow-clotting wound → a likely bleed-out
// spiral unless dressed. All balance lives here so the harness can bisect it.
public static class Spec50
{
    public static bool Enabled = true;

    // A limb severs the moment a bite drives an arm/leg to 0 HP AND either:
    //  • the blow's own damage ≥ LimbSeverThreshold  (a big single hit — the
    //    shark's 0.2, a future weapon — tears it clean off), OR
    //  • a deterministic roll < GrindSeverChance     (the small dog bite that
    //    finally destroys an already-mauled leg rips it off — rare, so most
    //    zeroed legs stay attached-but-useless as before).
    // Both stay high/low so amputation is dramatic, not routine.
    public static float LimbSeverThreshold = 0.14f;
    public static float GrindSeverChance = 0.25f;

    // The instant blood loss (0..1 of the Blood need) when a limb comes off.
    public static float LimbSeverBloodLoss = 0.4f;

    // The severity of the fresh stump wound filed on sever — deep, so §44
    // clotting keeps it bleeding for a while (ongoing Blood drain).
    public static float LimbSeverWoundSeverity = 0.35f;

    // Strike collapse for a severed ARM (below the 0.4 mauled floor). One arm
    // gone → strike ×this; both gone → ×this².
    public static float SeveredLimbMobilityMult = 0.15f;

    // A survivor who has lost a leg (one or both) crawls at this fraction of
    // walking speed — a fixed ~1/3, matching the crawl animation.
    public static float CrawlSpeedFactor = 1f / 3f;

    // How long a severed limb lies in the world before it decays away (slow
    // ticks × 16/tick, mirroring corpse decay — 4800 ≈ 2 in-game days).
    public static float SeveredLimbDecayTicks = 4800f;

    // Prepared-hazard chance to take a leg per slow tick while standing on a
    // hazard junction (0..1). 1 = deterministic on contact.
    public static float HazardSeverChance = 1f;
}

}
