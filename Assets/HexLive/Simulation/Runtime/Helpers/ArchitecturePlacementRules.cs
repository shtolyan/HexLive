using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

/// <summary>Placement policy for the player/NPC constructor, separate from furniture §66.</summary>
public static class ArchitecturePlacementRules
{
    public static bool CanPlace(WorldObjectState owner, ArchitectureElementState candidate)
    {
        if (owner == null || candidate == null || candidate.Layer != PlacementLayer.Architecture ||
            string.IsNullOrEmpty(candidate.DefinitionId) || string.IsNullOrEmpty(candidate.SlotKey))
        {
            return false;
        }

        foreach (var existing in owner.ArchitectureElements)
        {
            if (existing.ElementId == candidate.ElementId) continue;
            // Architecture intersects by authored slot, not by the furniture
            // rule "one object owns this hex". A bed may overlap this tile;
            // a second wall may not occupy the same wall bay.
            if (existing.Layer == PlacementLayer.Architecture &&
                existing.SlotKey == candidate.SlotKey)
            {
                return false;
            }
        }

        return true;
    }

    public static bool ConflictsWithFurniture(ArchitectureElementState element) => false;

    public static bool BlocksNpc(ArchitectureElementState element) => element != null &&
        element.Complete && element.DefinitionId is
            "architecture.wall.wood" or "architecture.window.wood" or "architecture.support.wood";
}

}
