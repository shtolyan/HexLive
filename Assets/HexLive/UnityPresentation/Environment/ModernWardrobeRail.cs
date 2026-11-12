#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §176.3 / bug #736: the apartment wardrobe shows what it stores. Every
    /// garment hangs on a hanger along the cabinet's own clothes rail, and
    /// footwear stands on the board under it, the way §133 hangs garments in
    /// the island wardrobe. This is presentation only: the renderer passes the
    /// snapshot's container contents plus the garments stored at the wardrobe
    /// junction, in a stable order.
    /// </summary>
    public sealed class ModernWardrobeRail : MonoBehaviour
    {
        // Tools/blender/author_modern_apartment.py, wardrobe(): the steel
        // clothes_rail runs from (-.038, -.375, 1.135) to (-.038, .097, 1.135)
        // and drawer_top's upper face is at .298 (Blender metres, Z up).
        private const float RailHeight = 1.135f;
        private const float BoardTop = .298f;
        private const float RailTolerance = .02f;
        private static readonly Vector3 FallbackRailA = new(.038f, RailHeight, .375f);
        private static readonly Vector3 FallbackRailB = new(.038f, RailHeight, -.097f);

        // Same shoulder drop below the hook as the island wardrobe (§133).
        private const float ShoulderDrop = .120f;
        private const float RailMargin = .05f;
        private const float MaxStep = .09f;
        private const float BoardClearance = .01f;

        private sealed class Entry
        {
            public string Id = string.Empty;
            public bool Footwear;
            public bool PrewarmRequested;
            public GameObject? Garment;
            public GameObject? Hanger;
        }

        private readonly List<Entry> _entries = new();
        private Transform? _contents;
        private bool _geometryResolved;
        private Vector3 _railA;
        private Vector3 _railB;
        private float _boardY;
        private int _wardrobeKey;

        public static void Sync(GameObject wardrobeView, int wardrobeKey, IReadOnlyList<string> garments,
            float garmentScale)
        {
            var rail = wardrobeView.GetComponent<ModernWardrobeRail>();
            if (rail == null) rail = wardrobeView.AddComponent<ModernWardrobeRail>();
            rail.Apply(wardrobeKey, garments, garmentScale);
        }

        private void Apply(int wardrobeKey, IReadOnlyList<string> garments, float garmentScale)
        {
            _wardrobeKey = wardrobeKey;
            var count = Mathf.Min(garments.Count, Spec133Capacity);
            if (!Matches(garments, count))
            {
                ClearEntries();
                for (var i = 0; i < count; i++)
                {
                    _entries.Add(new Entry
                    {
                        Id = garments[i],
                        Footwear = GarmentDropFactory.IsFootwear(garments[i]),
                    });
                }
            }

            if (_entries.Count == 0) return;
            if (!_geometryResolved && !ResolveGeometry()) return;

            var changed = false;
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                // §155: these garments have no object view of their own.
                ContentResidency.ReportGroundGarment(ResidencyKey(i), entry.Id, gameObject.activeInHierarchy);
                if (entry.Garment == null)
                {
                    var garment = entry.Footwear
                        ? GarmentDropFactory.Build(entry.Id)
                        : GarmentDropFactory.BuildHanging(entry.Id);
                    if (garment == null)
                    {
                        if (!entry.PrewarmRequested)
                        {
                            entry.PrewarmRequested = true;
                            GarmentDropFactory.Prewarm(entry.Id);
                        }
                        continue;
                    }

                    garment.name = $"Stored {entry.Id}";
                    garment.transform.SetParent(_contents, false);
                    garment.transform.localScale = Vector3.one * garmentScale;
                    entry.Garment = garment;
                    changed = true;
                }

                if (!entry.Footwear && entry.Hanger == null)
                {
                    // The authored hanger lives in the island wardrobe bundle
                    // (§133). Until that bundle arrives the garment simply
                    // hangs without it; the next sync adds the hanger.
                    var hanger = WardrobeHangerFactory.Build();
                    if (hanger != null)
                    {
                        hanger.transform.SetParent(_contents, false);
                        entry.Hanger = hanger;
                        changed = true;
                    }
                }
            }

            if (changed) Layout();
        }

        private const int Spec133Capacity = HexLive.Simulation.Runtime.Spec133.WardrobeCapacity;

        private bool Matches(IReadOnlyList<string> garments, int count)
        {
            if (_entries.Count != count) return false;
            for (var i = 0; i < count; i++)
                if (_entries[i].Id != garments[i]) return false;
            return true;
        }

        private int ResidencyKey(int index) => -(_wardrobeKey * 16 + index + 1);

        /// <summary>
        /// Measures the rail and the hanging section's board in this view's own
        /// space. The cabinet is one baked mesh, so the rail is recognised by
        /// its height: nothing else in the model has vertices within 2 cm of
        /// 1.135 (the nearest shelf tops out at 1.098). Measuring keeps the
        /// placement correct under any import basis and the renderer's fit
        /// scale and six-way yaw.
        /// </summary>
        private bool ResolveGeometry()
        {
            if (_contents == null)
            {
                _contents = new GameObject("Modern wardrobe contents").transform;
                _contents.SetParent(transform, false);
            }

            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;
            var points = new List<Vector3>();
            MeshFilter? source = null;
            foreach (var filter in GetComponentsInChildren<MeshFilter>(true))
            {
                // Only the authored cabinet: not this rail's own garments and
                // not a §150.4 overview impostor quad.
                if (filter.sharedMesh == null || filter.transform.IsChildOf(_contents) ||
                    filter.GetComponentInParent<HexLive.UnityPresentation.Views.ObjectImpostorVisual>(true) != null)
                    continue;
                source ??= filter;
                var mesh = filter.sharedMesh;
                if (mesh.isReadable)
                {
                    foreach (var vertex in mesh.vertices)
                        points.Add(transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
                }
                else
                {
                    var bounds = mesh.bounds;
                    for (var corner = 0; corner < 8; corner++)
                        points.Add(transform.InverseTransformPoint(filter.transform.TransformPoint(
                            bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                                (corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)))));
                }
            }

            if (source == null || points.Count == 0) return false;
            foreach (var point in points)
            {
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }

            var scale = (maxY - minY) / ModernLootCatalog.WardrobeHeight;
            if (scale <= 0f) return false;
            var railY = minY + RailHeight * scale;
            var tolerance = RailTolerance * scale;

            var found = false;
            var bestDistance = 0f;
            for (var i = 0; i < points.Count; i++)
            {
                if (Mathf.Abs(points[i].y - railY) > tolerance) continue;
                for (var j = i + 1; j < points.Count; j++)
                {
                    if (Mathf.Abs(points[j].y - railY) > tolerance) continue;
                    var delta = points[j] - points[i];
                    delta.y = 0f;
                    var distance = delta.sqrMagnitude;
                    if (distance <= bestDistance) continue;
                    bestDistance = distance;
                    _railA = points[i];
                    _railB = points[j];
                    found = true;
                }
            }

            if (!found || Mathf.Sqrt(bestDistance) < .2f * scale)
            {
                // Unreadable or unexpected mesh: the authored rail under the
                // FBX import basis (Blender x, y, z -> Unity -x, z, -y).
                _railA = transform.InverseTransformPoint(source.transform.TransformPoint(FallbackRailA));
                _railB = transform.InverseTransformPoint(source.transform.TransformPoint(FallbackRailB));
                railY = (_railA.y + _railB.y) * .5f;
            }

            _railA.y = railY;
            _railB.y = railY;
            _boardY = minY + BoardTop * scale;
            _geometryResolved = true;
            return true;
        }

        private void Layout()
        {
            if (_contents == null) return;
            var along = _railB - _railA;
            along.y = 0f;
            var length = along.magnitude;
            if (length < 1e-4f) return;
            along /= length;
            // A hung garment's cloth plane faces along the rail, exactly as on
            // the island wardrobe whose rail is its local +Z (§133).
            var facing = Quaternion.LookRotation(along, Vector3.up);
            var centre = (_railA + _railB) * .5f;

            var hanging = 0;
            var standing = 0;
            foreach (var entry in _entries)
            {
                if (entry.Footwear) standing++;
                else hanging++;
            }

            var usable = Mathf.Max(0f, length - 2f * RailMargin);
            var hangStep = hanging > 1 ? Mathf.Min(MaxStep, usable / (hanging - 1)) : 0f;
            var standStep = standing > 1 ? Mathf.Min(.16f, usable / (standing - 1)) : 0f;
            var hangIndex = 0;
            var standIndex = 0;
            var boardWorldY = transform.TransformPoint(new Vector3(0f, _boardY, 0f)).y;
            foreach (var entry in _entries)
            {
                if (entry.Garment == null) continue;
                var garment = entry.Garment.transform;
                garment.localRotation = facing;
                if (entry.Footwear)
                {
                    var spot = centre + along * (standStep * (standIndex++ - (standing - 1) * .5f));
                    garment.localPosition = new Vector3(spot.x, _boardY, spot.z);
                    if (TryWorldBounds(entry.Garment, out var shoe))
                        garment.position += Vector3.up * (boardWorldY - shoe.min.y);
                    continue;
                }

                var hook = centre + along * (hangStep * (hangIndex++ - (hanging - 1) * .5f));
                var half = TryWorldBounds(entry.Garment, out var cloth) ? Mathf.Max(.02f, cloth.extents.y) : .15f;
                garment.localPosition = new Vector3(hook.x,
                    Mathf.Max(_boardY + BoardClearance + half, hook.y - half - ShoulderDrop), hook.z);
                if (entry.Hanger != null)
                {
                    entry.Hanger.transform.localPosition = hook;
                    entry.Hanger.transform.localRotation = facing;
                }
            }
        }

        private static bool TryWorldBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return false;
            bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return true;
        }

        private void ClearEntries()
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                ContentResidency.ForgetObject(ResidencyKey(i));
                DestroyOwned(_entries[i].Garment);
                DestroyOwned(_entries[i].Hanger);
            }
            _entries.Clear();
        }

        private static void DestroyOwned(GameObject? owned)
        {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _entries.Count; i++)
                ContentResidency.ForgetObject(ResidencyKey(i));
            _entries.Clear();
        }
    }
}
