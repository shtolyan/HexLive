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

            // Unity's console keeps only the FIRST line of a multi-line entry
            // in its list view (and in what the MCP bridge reads back), so the
            // report also lands in a file — that is the copy to read.
            var reportPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Build/skin_seam_probe.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath));
            System.IO.File.WriteAllText(reportPath, report.ToString());
            Debug.Log($"[SkinSeam] {actors} actors -> {reportPath}{report}");
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
                ProbeZone(zone, set, meshScale, zone.BindAxisDir, report);
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
            float meshScale, Vector3 axis, StringBuilder report)
        {
            var bandageCrossing = 0;
            var woundCrossing = 0;
            var sampled = 0;
            var slotsSeen = new HashSet<int>();
            var reachCells = new Dictionary<int, int>();
            // Fill cost of ONE wound: the shader runs over the whole UV window
            // the cell grid hands back, per claimed slot, per pass. That is
            // what a combat burst multiplies.
            var slotSum = 0f;
            var oldAlbedoWindowSum = 0f;
            var albedoWindowSum = 0f;
            var oldGlossWindowSum = 0f;
            var glossWindowSum = 0f;

            foreach (var point in zone.Points)
            {
                if (!point.Valid || set.GroupOf(point.Slot) < 0)
                {
                    continue;
                }

                sampled++;
                slotsSeen.Add(point.Slot);
                if (SlotsReached(set, point, axis, BandageWorld * meshScale, reachCells) > 1)
                {
                    bandageCrossing++;
                }

                var woundSlots = SlotsReached(set, point, axis, WoundWorld * meshScale);
                if (woundSlots > 1)
                {
                    woundCrossing++;
                }

                slotSum += woundSlots;
                WindowFractions(set, point, axis, WoundWorld * meshScale, footprintScale: 1.6f,
                    out var oldAlbedo, out var albedo);
                WindowFractions(set, point, axis, WoundWorld * meshScale, footprintScale: 1f,
                    out var oldGloss, out var gloss);
                oldAlbedoWindowSum += oldAlbedo;
                albedoWindowSum += albedo;
                oldGlossWindowSum += oldGloss;
                glossWindowSum += gloss;
            }

            if (sampled == 0)
            {
                report.Append($"\n  {zone.Zone}: no usable cells");
                return;
            }

            report.Append($"\n  {zone.Zone}: bandage {bandageCrossing}/{sampled}, ")
                .Append($"wound {woundCrossing}/{sampled} spots span >1 slot ")
                .Append($"(anchor slots {string.Join(",", slotsSeen)}")
                .Append($" -> bandage reaches {FormatReach(reachCells, sampled)}); ")
                .Append($"wound cost: {slotSum / sampled:0.00} slots; windows old->box: ")
                .Append($"albedo {oldAlbedoWindowSum / sampled * 100f:0.0}%->")
                .Append($"{albedoWindowSum / sampled * 100f:0.0}%, gloss ")
                .Append($"{oldGlossWindowSum / sampled * 100f:0.0}%->")
                .Append($"{glossWindowSum / sampled * 100f:0.0}%");
        }

        // UV-window area (0..1 of the whole target) before and after the
        // oriented-box optimization. footprintScale=1.6 is wound albedo (halo
        // + art); 1 is the detailed-art/gloss pass.
        private static void WindowFractions(SkinPositionMapSet set,
            in PaintPointMap.Point point, Vector3 axis, float size, float footprintScale,
            out float oldFraction, out float boxFraction)
        {
            var group = set.GroupOf(point.Slot);
            var footprint = size * footprintScale;
            var radius = footprint * 0.7071f + set.SampleSpacing;
            oldFraction = set.TryGetUvBounds(group, point.BindPos, radius, out var oldUv)
                ? oldUv.width * oldUv.height
                : 0f;

            BuildProjection(point, axis, size, footprintScale,
                set.SampleSpacing + 0.005f, out var objectToDecal, out var half);
            boxFraction = set.TryGetUvBounds(group, objectToDecal, half, out var boxUv)
                ? boxUv.width * boxUv.height
                : 0f;
        }

        // Mirrors SkinTexturePainter.TryPlaceProjected's frame + claim box, so
        // the report counts exactly what the runtime will paint.
        private static int SlotsReached(SkinPositionMapSet set, in PaintPointMap.Point point,
            Vector3 axis, float size, Dictionary<int, int> reach = null)
        {
            BuildProjection(point, axis, size, footprintScale: 1f,
                set.SampleSpacing + 0.005f, out var objectToDecal, out var half);

            var count = 1; // the anchor slot always counts
            for (var slot = 0; slot < set.SlotSamples.Length; slot++)
            {
                if (slot == point.Slot || set.GroupOf(slot) < 0 ||
                    !set.SlotReaches(slot, objectToDecal, half))
                {
                    continue;
                }

                count++;
                if (reach != null)
                {
                    reach.TryGetValue(slot, out var n);
                    reach[slot] = n + 1;
                }
            }

            return count;
        }

        // "spans >1 slot" alone hides the asymmetry the eye notices: a decal
        // high on the thigh reaches the buttock's texture, one half way down
        // simply is not near it. This says WHICH slots a zone can spill onto
        // and from how many of its cells.
        private static string FormatReach(Dictionary<int, int> reach, int sampled)
        {
            if (reach.Count == 0)
            {
                return "nothing else";
            }

            var parts = new List<string>();
            foreach (var pair in reach)
            {
                parts.Add($"slot{pair.Key} from {pair.Value}/{sampled}");
            }

            parts.Sort(System.StringComparer.Ordinal);
            return string.Join(", ", parts);
        }

        private static void BuildProjection(in PaintPointMap.Point point, Vector3 axis,
            float size, float footprintScale, float slack, out Matrix4x4 objectToDecal,
            out Vector3 half)
        {
            var normal = point.BindNormal.normalized;
            var along = axis - normal * Vector3.Dot(normal, axis);
            if (along.sqrMagnitude < 1e-6f)
            {
                along = Vector3.Cross(normal, Vector3.right);
                if (along.sqrMagnitude < 1e-6f)
                {
                    along = Vector3.Cross(normal, Vector3.forward);
                }
            }

            along.Normalize();
            var frame = Matrix4x4.TRS(point.BindPos,
                Quaternion.LookRotation(normal, along), Vector3.one).inverse;
            var footprint = size * footprintScale;
            objectToDecal = Matrix4x4.Scale(
                new Vector3(1f / footprint, 1f / footprint, 1f)) * frame;
            var depth = size * 0.4f * footprintScale; // ProjectedDepthFactor
            half = new Vector3(0.5f + slack / footprint,
                0.5f + slack / footprint, depth + slack);
        }
    }
}
