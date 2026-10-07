using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{
// §59.5: shared tool admission for menu, AI and execution. Context-specific
// reach, ownership and resource checks remain with their authoritative handlers.
public static class ActionRequirements
{
    public static bool HasTool(IEnumerable<string> carried, IEnumerable<GearCapability> anyOf, int hands)
    {
        var mask = GearCapability.None;
        foreach (var capability in anyOf) mask |= capability;
        if (mask == GearCapability.None) return true;
        if (hands == 0) return false;
        foreach (var id in carried)
        {
            var gear = GearCatalog.For(id);
            if (gear.Id == id && (gear.Capabilities & mask) != 0 && (!gear.TwoHanded || hands >= 2))
                return true;
        }
        return false;
    }

    public static bool HasTool(System.Collections.Generic.List<Agents.ItemInstance> carried,
        IEnumerable<GearCapability> anyOf, int hands)
    {
        var mask = GearCapability.None;
        foreach (var capability in anyOf) mask |= capability;
        return mask == GearCapability.None || GearCatalog.BestToolFor(carried, mask, hands) is not null;
    }

    public static string LabelKey(InteractionDefinition action) => "interaction." + action.Id + ".verb";
    public static string FallbackLabelKey(InteractionDefinition action) => "interaction." + action.Type + ".verb";
}
}
