#nullable enable
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{

/// <summary>
/// Compact furniture authored for the 1.5-wu hut interior. These are deliberate
/// assemblies in the hut's board/stick/leaf palette, not scaled outdoor props.
/// </summary>
public static class HutFurnitureFactory
{
    // The simulation anchor is the nearest safe interior junction at local
    // X=±0.6495. Move only the furniture/pose outward: 0.6495 + 0.246 =
    // 0.8955, and the measured 0.766-wu cot leaves a 0.0205-wu seam to the
    // vertical wall at apothem 1.299. Interaction still starts from the safe
    // junction; furniture itself may occupy the architecture-edge space.
    public const float BedWallSnugOffset = 0.246f;
    // Match the player-approved BedSleepPoseTest contract exactly: the bed root
    // sits on the walkable plane. Do not reinterpret FBX bounds as a second
    // placement offset; doing so dropped the production beds toward terrain.
    public const float BedRootLift = HutAssembly.FloorSurfaceLift;

    /// <summary>
    /// The ordinary production bed.basic assembly, compacted as integrated
    /// one-hex furniture. Meshes, materials, construction pieces and sleep
    /// marker still come from the same BedAssembly used by world furniture.
    /// </summary>
    public static GameObject? BuildBed()
    {
        var bed = BedAssembly.BuildFinished(ContentIds.BedBasic);
        if (bed == null) return null;
        bed.name = "Integrated bed.basic (native)";
        return bed;
    }

    private const string HearthPrefabPath = "HexLive/Objects/furniture.hearth";

    /// <summary>
    /// The authored indoor hearth: the same colony craft as the outdoor
    /// campfire, built small. It fits inside one junction cell (0.32 wu against
    /// the 0.375 lattice), costs 4 sticks / 7 stones / 1 rope against the
    /// outdoor 12/18/2, and carries a real spit so meat roasts on it.
    /// Simulation warmth, fuel and cooking stay the normal campfire contract.
    /// </summary>
    public static GameObject BuildHearth()
    {
        var authored = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>(HearthPrefabPath);
        if (authored != null)
        {
            var host = new GameObject("Integrated hut hearth");
            var model = Object.Instantiate(authored, host.transform, false);
            model.name = "furniture.hearth (model)";
            // The renderer looks the flame anchor up as a DIRECT child, so lift
            // the authored marker out of the imported hierarchy (Spec 120.2).
            foreach (var child in model.GetComponentsInChildren<Transform>(true))
            {
                if (child.name != "fire_point") continue;
                child.SetParent(host.transform, worldPositionStays: true);
                break;
            }
            return host;
        }

        throw new System.InvalidOperationException(
            "Missing authored hearth at Resources/" + HearthPrefabPath +
            ". There is exactly one indoor hearth model; rebuild it with " +
            "Tools/blender/build_arch_elements.py and export_arch_elements.py.");
    }

}

}
