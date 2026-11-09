using HexLive.Simulation.Runtime;
namespace HexLive.Simulation.Content
{
/// <summary>§55/#650: physical reusable vessels. Coconut remains a source with its own rules.</summary>
public static class WaterVesselCatalog
{
    public static bool IsPortable(string id)=>id==ContentIds.Bottle||id==ContentIds.LeatherFlask;
    public static int Capacity(string id)=>id==ContentIds.Bottle?SimBalance.BottleCapacity:
        id==ContentIds.LeatherFlask?2*SimBalance.CoconutWaterCapacity:0;
}
}
