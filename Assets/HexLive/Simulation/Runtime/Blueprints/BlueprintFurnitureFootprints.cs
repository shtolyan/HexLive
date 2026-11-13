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
        // #723/#724: стол и верстак — три ряда «4, 3, 4», как у кровати, только
        // короче: 0,65 × 1,125 wu, 11 точек, якорь в середине.
        private static readonly JunctionKey[] Table = BuildTable();
        private static readonly JunctionKey[] StationDisc =
        {
            FromAxial(0, 0), FromAxial(1, 0), FromAxial(1, -1), FromAxial(0, -1),
            FromAxial(-1, 0), FromAxial(-1, 1), FromAxial(0, 1)
        };
        private static readonly JunctionKey[] Single = { FromAxial(0, 0) };
        // #765: диван — стол с двумя торцевыми точками: ряды «4, 5, 4»,
        // 0,65 × 1,5 wu, 13 точек, якорь в середине. Модель 0,78 × 1,83 wu;
        // прежний диск из семи точек (0,75 в поперечнике) оставлял оба торца
        // проходимыми — сквозь подлокотники шли как сквозь воздух.
        private static readonly JunctionKey[] Sofa = BuildSofa();

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
            // #755-757: современная кровать занимает РОВНО те же 14 точек, что и
            // первобытная (два ряда по 5 через 0,65 wu и 4 в середине, 1,5 wu
            // вдоль), — модель подогнана под этот след, а не след под модель.
            if (string.Equals(definitionId, ContentIds.BedBasic, StringComparison.Ordinal) ||
                string.Equals(definitionId, ContentIds.ModernBed, StringComparison.Ordinal)) return Bed;
            if (string.Equals(definitionId, "furniture.wardrobe", StringComparison.Ordinal)) return Wardrobe;
            if (string.Equals(definitionId, "furniture.hearth", StringComparison.Ordinal)) return Hearth;
            if (string.Equals(definitionId, ContentIds.HeadPike, StringComparison.Ordinal)) return Single;
            if (string.Equals(definitionId, ContentIds.Campfire, StringComparison.Ordinal)) return OutdoorCampfire;
            if (string.Equals(definitionId, ContentIds.DryingRack, StringComparison.Ordinal)) return WideStation;
            if (string.Equals(definitionId, ContentIds.Workbench, StringComparison.Ordinal) ||
                string.Equals(definitionId, "furniture.modern_table", StringComparison.Ordinal)) return Table;
            if (string.Equals(definitionId, ContentIds.WaterCollector, StringComparison.Ordinal)) return StationDisc;
            // §151.6 r2 (#732): the chest has the collector's footprint.
            if (string.Equals(definitionId, ContentIds.StorageChest, StringComparison.Ordinal)) return StationDisc;
            // §120.13/§176: современная мебель ставится по той же сетке. Диван
            // занимает симметричный диск из семи точек (0,75 wu в поперечнике) —
            // ориентация модели на след не влияет; мелкие предметы — одну.
            if (string.Equals(definitionId, "furniture.modern_sofa", StringComparison.Ordinal)) return Sofa;
            // #765: остальная современная мебель до сих пор не занимала НИЧЕГО
            // (ни тега Obstacle, ни радиуса) — замер на сейве: стул, холодильник,
            // стеллаж, шкаф и телевизор блокировали 0 точек, персонаж проходил в
            // 0,04–0,34 wu от их центров. Следы сняты с габаритов моделей
            // (modern-apartment-art.json, длинная сторона — ось +Y следа):
            //   холодильник 0,70 × 0,61 — диск из семи точек (0,75 в поперечнике);
            //   стеллаж 0,30 × 0,70, шкаф 0,42 × 0,83, телевизор 0,46 × 1,11 —
            //   ряд из трёх точек (0,75 wu) вдоль стены;
            //   стул 0,43 × 0,51 — одна точка.
            if (string.Equals(definitionId, ContentIds.ModernFridge, StringComparison.Ordinal)) return StationDisc;
            if (string.Equals(definitionId, "furniture.modern_shelf", StringComparison.Ordinal) ||
                string.Equals(definitionId, ContentIds.ModernWardrobe, StringComparison.Ordinal) ||
                string.Equals(definitionId, "furniture.modern_tv", StringComparison.Ordinal)) return Wardrobe;
            if (string.Equals(definitionId, "furniture.modern_chair", StringComparison.Ordinal)) return Single;
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

        /// <summary>
        /// Authored footprint points relative to the visible centre of a piece.
        /// Runtime placement uses these offsets rather than renderer bounds so
        /// an interaction anchor may differ from the physical centre without
        /// moving the navigation obstacle (§120).
        /// </summary>
        public static IReadOnlyList<Float2> CenteredWorldOffsets(
            string definitionId, int yawStep)
        {
            var offsets = LocalOffsets(definitionId);
            var centroid = CentroidOffset(definitionId, yawStep);
            var result = new List<Float2>(offsets.Count);
            foreach (var local in offsets)
            {
                var point = BlueprintGeometry.JunctionToWorld(
                    BlueprintGeometry.RotateJunctionOffset(local, yawStep));
                result.Add(new Float2(point.X - centroid.X, point.Y - centroid.Y));
            }

            return result;
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

        private static JunctionKey[] BuildTable()
        {
            var result = new List<JunctionKey>(11);
            for (var y = -3; y <= 3; y += 2)
            {
                result.Add(new JunctionKey(-1, y));
                result.Add(new JunctionKey(1, y));
            }
            for (var y = -2; y <= 2; y += 2)
                result.Add(new JunctionKey(0, y));
            return result.ToArray();
        }

        private static JunctionKey[] BuildSofa()
        {
            var result = new List<JunctionKey>(13);
            for (var y = -3; y <= 3; y += 2)
            {
                result.Add(new JunctionKey(-1, y));
                result.Add(new JunctionKey(1, y));
            }
            for (var y = -4; y <= 4; y += 2)
                result.Add(new JunctionKey(0, y));
            return result.ToArray();
        }

        private static JunctionKey FromAxial(int q, int r) => new JunctionKey(q, 2 * r + q);
    }
}
