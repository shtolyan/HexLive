using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime.Blueprints
{
    /// <summary>
    /// One buildable module of a player-authored plan: where it stands relative
    /// to the site anchor, and what it costs. This is the exact shape
    /// BuildingRules already keeps per element (LocalX/LocalZ/LocalYaw plus a
    /// four-material bill), which is why a custom plan needs no new state —
    /// only a different way of producing the list.
    /// </summary>
    public sealed class BuildingElementBlueprint
    {
        public string Key { get; internal set; } = string.Empty;
        public BlueprintElementKind Kind { get; internal set; }
        public int Index { get; internal set; }
        public int Sticks { get; internal set; }
        public int Boards { get; internal set; }
        public int Rope { get; internal set; }
        public int Leaves { get; internal set; }
        public float LocalX { get; internal set; }
        public float LocalZ { get; internal set; }
        public float LocalYaw { get; internal set; }
    }

    /// <summary>
    /// Turns a §120 constructor draft into the module list a build-site raises.
    ///
    /// Until now the two halves were parallel worlds: the constructor produced a
    /// BuildingBlueprintDraft that only presentation ever read, while the
    /// simulation could raise exactly one hard-coded shape — the twelve-bay
    /// single-hex hut in BuildingRules.HutDefinitions. Nothing converted one
    /// into the other, so a house the player designed could be looked at and
    /// never built.
    ///
    /// The bill per module is the §120 stage contract: every direct mesh child
    /// of BuildStage_1/2/3 in the authored model is ONE delivered resource, so
    /// the counts here must stay equal to the piece counts in the Blender kit
    /// (see .agents/skills/hexlive-furniture-authoring/SKILL.md).
    /// </summary>
    public static class BlueprintBuildingPlan
    {
        // sticks / boards / rope, and leaves where stage 2 is thatch.
        //
        // ⭐ These MUST equal the number of delivered-resource units in the
        // authored model, because that is what the reveal counts: every direct
        // mesh child of BuildStage_1/2/3 is one resource, and a module billed
        // for fewer than it has can never show its last pieces yet still reads
        // as Complete. Counted straight out of the exported FBX
        // (Tools: parse Models + Connections, skip "_deco_" hardware and the
        // transparent HL_Door_Pivot container), 2026-08-15:
        //
        //   wall    4 / 3 / 4      support  2 /  0 / 3
        //   window  4 / 3 / 4      floor    2 /  3 / 3
        //   door    5 / 2 / 4      roof     3 / 27 / 2
        //
        // Re-measure after any rebuild of the Blender kit. The roof moved a
        // long way when the flat panels became real palm fronds and the third
        // beam was added — an older table still said 2 / 3 / 1.
        private const int WallSticks = 4, WallBoards = 3, WallRope = 4;
        private const int WindowSticks = 4, WindowBoards = 3, WindowRope = 4;
        private const int DoorSticks = 5, DoorBoards = 2, DoorRope = 4;
        private const int SupportSticks = 2, SupportRope = 3;
        private const int FloorSticks = 2, FloorBoards = 3, FloorRope = 3;
        private const int RoofSticks = 3, RoofLeaves = 27, RoofRope = 2;

        /// <summary>
        /// The tile a plan is anchored on: the hex carrying the most floor, ties
        /// broken by coordinate so the same draft always anchors the same way.
        /// Everything else is expressed relative to that hex's centre, exactly
        /// as the hard-coded hut expresses its twelve bays.
        /// </summary>
        public static TileCoord AnchorTile(BuildingBlueprintDraft draft)
        {
            if (draft.HasAnchor) return new TileCoord(draft.AnchorQ, draft.AnchorR);
            var counts = new Dictionary<TileCoord, int>();
            foreach (var element in draft.Elements)
            {
                if (element.Kind != BlueprintElementKind.FloorSector) continue;
                var hex = element.FloorSector.Hex;
                counts[hex] = counts.TryGetValue(hex, out var seen) ? seen + 1 : 1;
            }
            if (counts.Count == 0) return TileCoord.Zero;
            var best = counts.First();
            foreach (var pair in counts)
            {
                if (pair.Value > best.Value ||
                    pair.Value == best.Value && Before(pair.Key, best.Key)) best = pair;
            }
            return best.Key;
        }

        private static bool Before(TileCoord left, TileCoord right) =>
            left.Q != right.Q ? left.Q < right.Q : left.R < right.R;

        /// <summary>Every hex the plan puts floor on — the building's footprint.</summary>
        public static IReadOnlyList<TileCoord> Footprint(BuildingBlueprintDraft draft) =>
            draft.Elements
                .Where(element => element.Kind == BlueprintElementKind.FloorSector)
                .Select(element => element.FloorSector.Hex)
                .Distinct()
                .OrderBy(hex => hex.Q).ThenBy(hex => hex.R)
                .ToArray();

        public static IReadOnlyList<BuildingElementBlueprint> Modules(BuildingBlueprintDraft draft)
        {
            var anchor = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(AnchorTile(draft)));
            var centroid = PlanCentroid(draft);
            // The same walk the renderer uses, so a section's post stands on the
            // same seam in the world as it does in the constructor preview.
            var seamPosts = BlueprintGeometry.AssignSeamPosts(draft.Elements);
            var modules = new List<BuildingElementBlueprint>();
            var index = 0;

            foreach (var element in Ordered(draft))
            {
                var module = new BuildingElementBlueprint
                {
                    Key = SlotKey(element),
                    Kind = element.Kind,
                    Index = index++
                };
                switch (element.Kind)
                {
                    case BlueprintElementKind.Support:
                    {
                        var point = BlueprintGeometry.ToWorld(element.Node);
                        module.Sticks = SupportSticks;
                        module.Rope = SupportRope;
                        Place(module, point, anchor, YawTowards(point, centroid, outward: true));
                        break;
                    }
                    case BlueprintElementKind.Wall:
                    case BlueprintElementKind.Window:
                    case BlueprintElementKind.Door:
                    {
                        var a = BlueprintGeometry.ToWorld(element.Segment.A);
                        var b = BlueprintGeometry.ToWorld(element.Segment.B);
                        // A section is turned around when the seam that owns its
                        // post pair is its B node — the same rule the preview
                        // applies, so simulation and view cannot disagree about
                        // which joint carries the post.
                        var flip = seamPosts.TryGetValue(element.Id, out var seam) &&
                                   seam == element.Segment.B;
                        var from = flip ? b : a;
                        var to = flip ? a : b;
                        module.Sticks = element.Kind switch
                        {
                            BlueprintElementKind.Window => WindowSticks,
                            BlueprintElementKind.Door => DoorSticks,
                            _ => WallSticks
                        };
                        module.Boards = element.Kind switch
                        {
                            BlueprintElementKind.Window => WindowBoards,
                            BlueprintElementKind.Door => DoorBoards,
                            _ => WallBoards
                        };
                        module.Rope = element.Kind switch
                        {
                            BlueprintElementKind.Window => WindowRope,
                            BlueprintElementKind.Door => DoorRope,
                            _ => WallRope
                        };
                        var midpoint = new Float2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
                        Place(module, midpoint, anchor, Yaw(to.X - from.X, to.Y - from.Y));
                        break;
                    }
                    case BlueprintElementKind.FloorSector:
                    case BlueprintElementKind.RoofSector:
                    {
                        var roof = element.Kind == BlueprintElementKind.RoofSector;
                        var hex = roof ? element.RoofSector.Hex : element.FloorSector.Hex;
                        var sector = roof ? element.RoofSector.Sector : element.FloorSector.Sector;
                        var centre = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(hex));
                        var first = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(hex, sector));
                        var second = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(hex, sector + 1));
                        module.Sticks = roof ? RoofSticks : FloorSticks;
                        module.Boards = roof ? 0 : FloorBoards;
                        module.Leaves = roof ? RoofLeaves : 0;
                        module.Rope = roof ? RoofRope : FloorRope;
                        // The sector model is drawn from the hex centre along its
                        // own bisector, so that is what the module carries too.
                        Place(module, centre, anchor,
                            Yaw((first.X + second.X) * 0.5f - centre.X,
                                (first.Y + second.Y) * 0.5f - centre.Y));
                        break;
                    }
                }
                modules.Add(module);
            }
            return modules;
        }

        /// <summary>
        /// Sum of every module's bill — what the site must be stocked with.
        /// Kept beside the module list on purpose: a bill computed anywhere else
        /// is a second place to forget a material.
        /// </summary>
        public static (int Sticks, int Boards, int Rope, int Leaves) Bill(
            IEnumerable<BuildingElementBlueprint> modules)
        {
            int sticks = 0, boards = 0, rope = 0, leaves = 0;
            foreach (var module in modules)
            {
                sticks += module.Sticks;
                boards += module.Boards;
                rope += module.Rope;
                leaves += module.Leaves;
            }
            return (sticks, boards, rope, leaves);
        }

        /// <summary>
        /// Stable order: supports, floors, walls/openings, then roofs. The roof
        /// is last because BuildingRules gates it behind finished supports, and
        /// a stable order keeps a site's module keys identical across a reload.
        /// </summary>
        private static IEnumerable<BlueprintElementData> Ordered(BuildingBlueprintDraft draft) =>
            draft.Elements
                .OrderBy(element => element.Kind switch
                {
                    BlueprintElementKind.Support => 0,
                    BlueprintElementKind.FloorSector => 1,
                    BlueprintElementKind.RoofSector => 3,
                    _ => 2
                })
                .ThenBy(element => element.Id, StringComparer.Ordinal);

        /// <summary>
        /// The stable identity of one module, shared by the site's
        /// ArchitectureElementState and the draft element it came from. Public
        /// because the topology repair has to walk BACK from a raised piece to
        /// the blueprint segment it stands on; deriving that key anywhere else
        /// would be a second place to get it wrong.
        /// </summary>
        public static string SlotKey(BlueprintElementData element) => element.Kind switch
        {
            BlueprintElementKind.Support => $"support:{element.Node}",
            BlueprintElementKind.FloorSector => $"floor:{element.FloorSector}",
            BlueprintElementKind.RoofSector => $"roof:{element.RoofSector}",
            _ => $"bay:{element.Segment}"
        };

        private static void Place(
            BuildingElementBlueprint module, Float2 point, Float2 anchor, float yaw)
        {
            module.LocalX = point.X - anchor.X;
            module.LocalZ = point.Y - anchor.Y;
            module.LocalYaw = yaw;
        }

        /// <summary>
        /// The yaw convention BuildingRules already writes for its bays:
        /// atan2(-dz, dx) in degrees. Derived from the existing hut so a custom
        /// plan and the canonical one are read by the same renderer.
        /// </summary>
        private static float Yaw(float dx, float dz) =>
            MathF.Atan2(-dz, dx) * 180f / MathF.PI;

        private static float YawTowards(Float2 point, Float2 centroid, bool outward)
        {
            var dx = outward ? point.X - centroid.X : centroid.X - point.X;
            var dz = outward ? point.Y - centroid.Y : centroid.Y - point.Y;
            return dx * dx + dz * dz < 1e-6f ? 0f : Yaw(dx, dz);
        }

        /// <summary>Centre of the floored area — a corner post aims away from it.</summary>
        private static Float2 PlanCentroid(BuildingBlueprintDraft draft)
        {
            float x = 0f, y = 0f;
            var count = 0;
            foreach (var element in draft.Elements)
            {
                if (element.Kind != BlueprintElementKind.FloorSector) continue;
                var sector = element.FloorSector;
                var centre = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCenter(sector.Hex));
                var a = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector));
                var b = BlueprintGeometry.ToWorld(BlueprintGeometry.HexCorner(sector.Hex, sector.Sector + 1));
                x += (centre.X + a.X + b.X) / 3f;
                y += (centre.Y + a.Y + b.Y) / 3f;
                count++;
            }
            return count == 0 ? new Float2(0f, 0f) : new Float2(x / count, y / count);
        }
    }
}
