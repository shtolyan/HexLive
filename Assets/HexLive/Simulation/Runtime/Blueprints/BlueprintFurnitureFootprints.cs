using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
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
        private static readonly JunctionKey[] WideStation =
        {
            new JunctionKey(0, -2), new JunctionKey(0, 0), new JunctionKey(0, 2)
        };
        private static readonly JunctionKey[] StationDisc =
        {
            FromAxial(0, 0), FromAxial(1, 0), FromAxial(1, -1), FromAxial(0, -1),
            FromAxial(-1, 0), FromAxial(-1, 1), FromAxial(0, 1)
        };
        private static readonly JunctionKey[] Single = { FromAxial(0, 0) };

        public static IReadOnlyList<JunctionKey> LocalOffsets(string definitionId) =>
            Authored(definitionId) ?? Single;

        /// <summary>
        /// Does this id own an AUTHORED footprint, as opposed to falling back to
        /// the single junction every loose object occupies? Callers that must
        /// tell "a piece of furniture" from "a stick somebody dropped on the
        /// floor" ask here rather than keeping a second list of ids.
        /// </summary>
        public static bool HasFootprint(string definitionId) => Authored(definitionId) != null;

        private static JunctionKey[] Authored(string definitionId)
        {
            if (string.Equals(definitionId, ContentIds.BedBasic, StringComparison.Ordinal)) return Bed;
            if (string.Equals(definitionId, "furniture.wardrobe", StringComparison.Ordinal)) return Wardrobe;
            if (string.Equals(definitionId, "furniture.hearth", StringComparison.Ordinal)) return Hearth;
            if (string.Equals(definitionId, ContentIds.Campfire, StringComparison.Ordinal)) return OutdoorCampfire;
            if (string.Equals(definitionId, ContentIds.DryingRack, StringComparison.Ordinal) ||
                string.Equals(definitionId, ContentIds.Workbench, StringComparison.Ordinal)) return WideStation;
            if (string.Equals(definitionId, ContentIds.WaterCollector, StringComparison.Ordinal)) return StationDisc;
            return null;
        }

        /// <summary>
        /// From a piece's ANCHOR junction to the centre of the junctions it
        /// actually occupies, in world units, at the given <paramref name="yawStep"/>.
        ///
        /// <para>
        /// Furniture is DRAWN at the centroid of its footprint, never at the
        /// anchor: a bed's row runs from -0.5625 to +0.9375 wu along its length,
        /// so the anchor sits 0.1875 wu off centre and a mesh placed on it slides
        /// that far away from the wall it was laid against. The anchor stays the
        /// route/interaction point — this is the visual correction on top of it,
        /// and it lives here so the §120 constructor preview and the world
        /// renderer cannot disagree about where the same bed stands.
        /// </para>
        /// </summary>
        public static Float2 CentroidOffset(string definitionId, int yawStep)
        {
            var offsets = LocalOffsets(definitionId);
            if (offsets.Count == 0) return new Float2(0f, 0f);
            float x = 0f, y = 0f;
            foreach (var local in offsets)
            {
                var point = BlueprintGeometry.JunctionToWorld(
                    BlueprintGeometry.RotateJunctionOffset(local, yawStep));
                x += point.X;
                y += point.Y;
            }

            return new Float2(x / offsets.Count, y / offsets.Count);
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
