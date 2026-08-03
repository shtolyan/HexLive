using System.Collections.Generic;
using System.Text;
using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Spec 40.8-J verification, without entering play mode.
    ///
    /// The whole point of the projected decal path is that a wrap at the hip
    /// lands on the leg AND the torso — two different textures on two
    /// different material slots. That is measurable straight off the baked
    /// assets: walk every cell of every zone's paint grid, ask how many slots
    /// a real-sized bandage reaches from there, and report the spots where the
    /// answer is more than one. Those are exactly the placements the old
    /// per-slot rectangle used to cut in half.
    ///
    /// Also reports position-map coverage per texture group, which catches a
    /// bad bake (an empty or half-rasterized map) before anyone hunts for it
    /// in the game.
    ///
    /// Menu: <b>HexLive ▸ Paint Maps ▸ Probe Skin Seams</b>.
    /// </summary>
    public static class SkinSeamProbe
    {
        private const string ActorsFolder = "Assets/Resources/HexLive/Actors";

        // Same world-metre targets SkinTexturePainter.TryPlace uses.
        private const float BandageWorld = 0.14f;
        private const float WoundWorld = 0.09f;

        [MenuItem("HexLive/Paint Maps/Probe Skin Seams")]
        public static void Probe()
        {
            var report = new StringBuilder();
            var actors = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { ActorsFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    continue;
                }

                actors++;
                ProbeActor(prefab.name, report);
            }

            if (actors == 0)
            {
                Debug.LogWarning($"[SkinSeam] no actor prefabs under {ActorsFolder}");
                return;
            }

            Debug.Log($"[SkinSeam] {report}");
        }

        private static void ProbeActor(string actorName, StringBuilder report)
        {
            report.Append('\n').Append(actorName).Append(": ");

            var map = Resources.Load<PaintPointMap>($"{PaintPointMap.ResourceFolder}/skin_{actorName}");
            var set = Resources.Load<SkinPositionMapSet>(
                $"{PaintPointMap.ResourceFolder}/skinpos_{actorName}");
            if (map == null || set == null)
            {
                report.Append(map == null ? "NO point map" : "NO position maps")
                    .Append(" — run HexLive ▸ Paint Maps ▸ Regenerate");
                return;
            }

            if (!map.HasProjectedFrames)
            {
                report.Append($"point map is v{map.Version} (pre-projected) — regenerate");
                return;
            }

            var meshScale = Mathf.Max(0.0001f, map.MeshHeight / 1.7f);
            report.Append($"meshHeight={map.MeshHeight:0.###}, groups={set.GroupPositionMaps.Length}, ");
            report.Append(CoverageLine(set));

            foreach (var zone in map.Zones)
            {
                ProbeZone(zone, set, meshScale, report);
            }
        }

        // Fraction of each position map that carries geometry — a healthy bake
        // sits well above zero; 0 means the group rasterized nothing.
        private static string CoverageLine(SkinPositionMapSet set)
        {
            var parts = new List<string>();
            for (var group = 0; group < set.GroupPositionMaps.Length; group++)
            {
                var tex = set.GroupPositionMaps[group];
                if (tex == null)
                {
                    parts.Add($"g{group}=MISSING");
                    continue;
                }

                var cells = group < set.GroupCells.Length ? set.GroupCells[group] : null;
                var live = 0;
                if (cells != null)
                {
                    foreach (var valid in cells.Valid)
                    {
                        if (valid)
                        {
                            live++;
                        }
                    }
                }

                var total = SkinPositionMapSet.CellsPerSide * SkinPositionMapSet.CellsPerSide;
                parts.Add($"g{group}={live * 100 / Mathf.Max(1, total)}%");
            }

            return "coverage " + string.Join(" ", parts);
        }

        private static void ProbeZone(PaintPointMap.ZonePoints zone, SkinPositionMapSet set,
            float meshScale, StringBuilder report)
        {
            var bandageCrossing = 0;
            var woundCrossing = 0;
            var sampled = 0;
            var slotsSeen = new HashSet<int>();

            foreach (var point in zone.Points)
            {
                if (!point.Valid || set.GroupOf(point.Slot) < 0)
                {
                    continue;
                }

                sampled++;
                slotsSeen.Add(point.Slot);
                if (SlotsReached(set, point, BandageWorld * meshScale) > 1)
                {
                    bandageCrossing++;
                }

                if (SlotsReached(set, point, WoundWorld * meshScale) > 1)
                {
                    woundCrossing++;
                }
            }

            if (sampled == 0)
            {
                report.Append($"\n  {zone.Zone}: no usable cells");
                return;
            }

            report.Append($"\n  {zone.Zone}: bandage {bandageCrossing}/{sampled}, ")
                .Append($"wound {woundCrossing}/{sampled} spots span >1 slot ")
                .Append($"(anchor slots {string.Join(",", slotsSeen)})");
        }

        private static int SlotsReached(SkinPositionMapSet set, in PaintPointMap.Point point,
            float size)
        {
            var radius = size * 0.7071f + set.SampleSpacing;
            var count = 1; // the anchor slot always counts
            for (var slot = 0; slot < set.SlotSamples.Length; slot++)
            {
                if (slot != point.Slot && set.GroupOf(slot) >= 0 &&
                    set.SlotReaches(slot, point.BindPos, radius))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
