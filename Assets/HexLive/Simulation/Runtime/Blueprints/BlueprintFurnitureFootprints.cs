using System;
using System.Collections.Generic;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime.Blueprints
{
    /// <summary>
    /// Logical junction footprints. They rotate with yawStep; visual bounds
    /// never participate in placement (§120, furniture authoring contract).
    /// </summary>
    public static class BlueprintFurnitureFootprints
    {
        private static readonly JunctionKey[] Bed = BuildBed();
        private static readonly JunctionKey[] Wardrobe =
        {
            FromAxial(-1, 0), FromAxial(0, 0), FromAxial(1, 0)
        };
        private static readonly JunctionKey[] Hearth =
        {
            FromAxial(0, 0), FromAxial(1, 0), FromAxial(1, -1), FromAxial(0, -1),
            FromAxial(-1, 0), FromAxial(-1, 1), FromAxial(0, 1)
        };
        private static readonly JunctionKey[] Single = { FromAxial(0, 0) };

        public static IReadOnlyList<JunctionKey> LocalOffsets(string definitionId)
        {
            if (string.Equals(definitionId, ContentIds.BedBasic, StringComparison.Ordinal)) return Bed;
            if (string.Equals(definitionId, "furniture.wardrobe", StringComparison.Ordinal)) return Wardrobe;
            if (string.Equals(definitionId, ContentIds.Campfire, StringComparison.Ordinal) ||
                string.Equals(definitionId, "furniture.hearth", StringComparison.Ordinal)) return Hearth;
            return Single;
        }

        public static IReadOnlyList<JunctionKey> OccupiedJunctions(FurniturePlacementData placement)
        {
            var primary = placement.PrimaryJunction;
            var result = new List<JunctionKey>();
            foreach (var local in LocalOffsets(placement.DefinitionId))
            {
                var rotated = BlueprintGeometry.RotateJunctionOffset(local, placement.YawStep);
                result.Add(new JunctionKey(primary.XKey + rotated.XKey, primary.YKey + rotated.YKey));
            }
            return result;
        }

        private static JunctionKey[] BuildBed()
        {
            var result = new List<JunctionKey>(14);
            // Two 1.5-wu side rails, 0.6495 wu apart, plus four centre
            // junctions. The pivot is a lattice junction; the artistic mesh
            // may keep its small padding but the logical footprint reaches
            // the approved bed corners.
            for (var y = -3; y <= 5; y += 2)
            {
                result.Add(new JunctionKey(-1, y));
                result.Add(new JunctionKey(1, y));
            }
            for (var y = -2; y <= 4; y += 2)
                result.Add(new JunctionKey(0, y));
            return result.ToArray();
        }

        private static JunctionKey FromAxial(int q, int r) => new JunctionKey(q, 2 * r + q);
    }
}
