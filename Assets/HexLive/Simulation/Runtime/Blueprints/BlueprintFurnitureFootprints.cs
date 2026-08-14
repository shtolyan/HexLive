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
        // Three junctions in a row along the SAME base axis the models are
        // authored on (+Y at yawStep 0), like the bed. The old axial row ran at
        // 30 deg instead, so a wardrobe blocked a line of junctions rotated a
        // full hex step away from the cabinet the player could see.
        private static readonly JunctionKey[] Wardrobe =
        {
            new JunctionKey(0, -2), new JunctionKey(0, 0), new JunctionKey(0, 2)
        };
        // The indoor hearth is a single-junction fire: its authored ring is
        // 0.32 wu across against the 0.375 lattice, so it never reaches a
        // neighbouring point. The old seven-junction disc was the outdoor
        // campfire's reach and blocked the whole middle of the hut.
        private static readonly JunctionKey[] Hearth = { FromAxial(0, 0) };
        private static readonly JunctionKey[] OutdoorCampfire =
        {
            FromAxial(0, 0), FromAxial(1, 0), FromAxial(1, -1), FromAxial(0, -1),
            FromAxial(-1, 0), FromAxial(-1, 1), FromAxial(0, 1)
        };
        private static readonly JunctionKey[] Single = { FromAxial(0, 0) };

        public static IReadOnlyList<JunctionKey> LocalOffsets(string definitionId)
        {
            if (string.Equals(definitionId, ContentIds.BedBasic, StringComparison.Ordinal)) return Bed;
            if (string.Equals(definitionId, "furniture.wardrobe", StringComparison.Ordinal)) return Wardrobe;
            if (string.Equals(definitionId, "furniture.hearth", StringComparison.Ordinal)) return Hearth;
            if (string.Equals(definitionId, ContentIds.Campfire, StringComparison.Ordinal)) return OutdoorCampfire;
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
