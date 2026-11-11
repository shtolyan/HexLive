using HexLive.Simulation.Runtime;
namespace HexLive.Simulation.Content
{
/// <summary>§55/#650: physical reusable vessels. Coconut remains a source with its own rules.</summary>
public static class WaterVesselCatalog
{
    public static bool IsPortable(string id) => Capacity(id) > 0;

    public static int Capacity(string id) => id switch
    {
        ContentIds.Bottle => SimBalance.BottleCapacity,
        ContentIds.LeatherFlask => 2 * SimBalance.CoconutWaterCapacity,
        ContentIds.DrinkingGlass => 4, // §176: four 100 ml portions
        _ => 0,
    };
}
}
