#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>§119 paid ingredients and a persistent, project-owned tabletop indicator.</summary>
    public sealed class CraftProjectVisual : MonoBehaviour
    {
        // Tools/make_workbench_models.py: top board upper face, not its centre.
        private const float TableTopY = 0.780f;
        private const float TableWidth = 0.980f;
        private const float TableDepth = 0.760f;
        private const float StackGap = 0.012f;
        private readonly List<GameObject> _ingredients = new();
        private Transform? _output;
        private Quaternion _authoredOutputRotation;
        private string _ingredientSignature = string.Empty;
        private bool _contentPending;
        private ObjectSnapshot? _snapshot;
        private UI.NpcWorldProgressBar? _progress;
        private Transform? _progressAnchor;

        public void Sync(ObjectSnapshot snapshot)
        {
            _snapshot = snapshot;
            EnsureOutput();
            var onTable = snapshot.CraftStationObjectId.HasValue;
            var signature = onTable + ":" + string.Join("|", snapshot.CraftIngredients);
            if (signature != _ingredientSignature || _contentPending)
            {
                PositionOutput(onTable);
                RebuildIngredients(snapshot.CraftIngredients, onTable);
                _ingredientSignature = signature;
            }
            if (onTable)
            {
                EnsureProgress();
                _progress!.SetProgress(snapshot.CraftWorkDone /
                    (float)Mathf.Max(1, snapshot.CraftWorkRequired), snapshot.CraftWorkRequired > 0);
            }
            else if (_progress != null) _progress.SetProgress(0f, false);
        }

        private void LateUpdate()
        {
            // Atomic models may arrive while the simulation remains paused.
            // Discover the real product and complete its paid stock layout
            // without waiting for another simulation tick.
            if (_snapshot != null && (_contentPending || _output == null)) Sync(_snapshot);
        }

        private void EnsureOutput()
        {
            if (_output != null) return;
            for (var i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (!child.name.StartsWith("Craft ") &&
                    child.GetComponent<Views.ObjectImpostorVisual>() == null)
                {
                    _output = child;
                    _authoredOutputRotation = child.localRotation;
                    _ingredientSignature = string.Empty;
                    break;
                }
            }
        }

        private void PositionOutput(bool onTable)
        {
            if (_output == null) return;
            _output.localPosition = new Vector3(0f, onTable ? TableTopY : 0.035f, 0f);
            _output.localRotation = onTable ? _authoredOutputRotation : Quaternion.Euler(86f, 0f, 0f);
            if (onTable) SetBottomCenter(_output, 0f, 0f, TableTopY);
        }

        private void RebuildIngredients(IReadOnlyList<string> ids, bool onTable)
        {
            var prefabs = new GameObject[ids.Count];
            for (var i = 0; i < prefabs.Length; i++)
            {
                prefabs[i] = WorldPropResources.Load(ids[i]);
                if (prefabs[i] == null)
                {
                    _contentPending = true;
                    return;
                }
            }

            _contentPending = false;
            foreach (var go in _ingredients) if (go != null) Destroy(go);
            _ingredients.Clear();
            for (var i = 0; i < prefabs.Length; i++)
            {
                // §119.1 / §152: use the exact authored owner, exactly like a
                // world drop. There is no procedural substitute while its
                // independent bundle is still travelling.
                var model = Object.Instantiate(prefabs[i]);
                model.name = $"Craft Ingredient {i:00} {ids[i]}";
                model.transform.SetParent(transform, false);
                model.transform.localScale *= ObjectFit.FitScaleFactor(model, ids[i]);

                if (onTable)
                {
                    var bounds = LocalBounds(model.transform);
                    if (bounds.size.z > bounds.size.x)
                        model.transform.localRotation = Quaternion.Euler(0f, 90f, 0f) * model.transform.localRotation;
                }
                else
                {
                    var angle = i * Mathf.PI * 2f / Mathf.Max(1, ids.Count);
                    model.transform.localPosition = new Vector3(
                        Mathf.Cos(angle) * 0.28f, 0.025f, Mathf.Sin(angle) * 0.28f);
                    model.transform.localRotation = Quaternion.Euler(86f, i * 47f, 0f);
                }
                foreach (var collider in model.GetComponentsInChildren<Collider>()) Destroy(collider);
                _ingredients.Add(model);
            }
            if (onTable) ArrangeStacks(ids);
        }

        private sealed class Stack
        {
            public readonly List<Transform> Models = new();
            public float Width;
            public float Depth;
        }

        private void ArrangeStacks(IReadOnlyList<string> ids)
        {
            var stacks = new List<Stack>();
            var byId = new Dictionary<string, Stack>();
            for (var i = 0; i < _ingredients.Count; i++)
            {
                if (!byId.TryGetValue(ids[i], out var stack))
                {
                    stack = new Stack();
                    byId.Add(ids[i], stack);
                    stacks.Add(stack);
                }
                var model = _ingredients[i].transform;
                var b = LocalBounds(model);
                stack.Models.Add(model);
                stack.Width = Mathf.Max(stack.Width, b.size.x);
                stack.Depth = Mathf.Max(stack.Depth, b.size.z);
            }
            var occupied = new List<Rect>();
            var heights = new List<float>();
            var top = TableTopY;
            if (_output != null)
            {
                var b = LocalBounds(_output);
                var x = Mathf.Max(0f, TableWidth * 0.5f - b.size.x * 0.5f - StackGap);
                var z = Mathf.Min(0f, -TableDepth * 0.5f + b.size.z * 0.5f + StackGap);
                SetBottomCenter(_output, x, z, TableTopY);
                b = LocalBounds(_output);
                occupied.Add(new Rect(b.min.x, b.min.z, b.size.x, b.size.z));
                heights.Add(b.max.y);
                top = b.max.y;
            }
            // The product's spot is stable before/after completion and reload.
            // Pack material TYPES in rows, then stack every physical unit upward.
            stacks.Sort((a, b) => b.Width.CompareTo(a.Width));
            foreach (var stack in stacks)
            {
                var xs = new List<float> { -TableWidth * 0.5f + StackGap };
                var zs = new List<float> { TableDepth * 0.5f - StackGap };
                foreach (var rectangle in occupied)
                {
                    xs.Add(rectangle.xMax + StackGap);
                    zs.Add(rectangle.yMin - StackGap);
                }
                xs.Sort(); zs.Sort((a, b) => b.CompareTo(a));
                var placement = new Rect();
                var found = false;
                foreach (var z in zs)
                {
                    foreach (var x in xs)
                    {
                        var candidate = new Rect(x, z - stack.Depth, stack.Width, stack.Depth);
                        if (candidate.xMax > TableWidth * 0.5f - StackGap + 0.001f ||
                            candidate.yMin < -TableDepth * 0.5f + StackGap - 0.001f) continue;
                        var overlaps = false;
                        foreach (var rectangle in occupied)
                            if (candidate.Overlaps(rectangle)) { overlaps = true; break; }
                        if (overlaps) continue;
                        placement = candidate; found = true; break;
                    }
                    if (found) break;
                }
                var y = TableTopY;
                if (!found)
                {
                    // Preserve all paid units if a future recipe exceeds deck
                    // capacity. Oversized bills need an authored larger station;
                    // this last-resort layer is not a promise of deck clearance.
                    var materialSlot = occupied.Count > 1 ? 1 : -1;
                    placement = materialSlot >= 0 ? occupied[materialSlot] :
                        new Rect(-stack.Width * 0.5f, -stack.Depth * 0.5f, stack.Width, stack.Depth);
                    y = materialSlot >= 0 ? heights[materialSlot] + StackGap : top + StackGap;
                    placement = new Rect(placement.center.x - stack.Width * 0.5f,
                        placement.center.y - stack.Depth * 0.5f, stack.Width, stack.Depth);
                    foreach (var rectangle in occupied)
                        if (placement.Overlaps(rectangle)) y = Mathf.Max(y, top + StackGap);
                }
                foreach (var model in stack.Models)
                {
                    SetBottomCenter(model, placement.center.x, placement.center.y, y);
                    var bounds = LocalBounds(model);
                    top = Mathf.Max(top, bounds.max.y);
                    y = bounds.max.y + StackGap;
                }
                occupied.Add(placement);
                heights.Add(y - StackGap);
            }
            EnsureProgress();
            _progressAnchor!.localPosition = new Vector3(0f, top, 0f);
        }

        private void SetBottomCenter(Transform model, float x, float z, float bottom)
        {
            var bounds = LocalBounds(model);
            model.localPosition += new Vector3(x - bounds.center.x, bottom - bounds.min.y,
                z - bounds.center.z);
        }

        private Bounds LocalBounds(Transform model)
        {
            var bounds = new Bounds();
            var any = false;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                var meshBounds = filter.sharedMesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = meshBounds.center + Vector3.Scale(meshBounds.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var point = transform.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (any) bounds.Encapsulate(point);
                    else { bounds = new Bounds(point, Vector3.zero); any = true; }
                }
            }
            return bounds;
        }

        private void EnsureProgress()
        {
            if (_progress != null) return;
            _progressAnchor = new GameObject("Craft Progress Anchor").transform;
            _progressAnchor.SetParent(transform, false);
            _progressAnchor.localPosition = Vector3.up * TableTopY;
            var bar = new GameObject("Craft Project Progress");
            bar.transform.SetParent(transform, false);
            _progress = bar.AddComponent<UI.NpcWorldProgressBar>();
            _progress.Initialize(_progressAnchor, horizontal: true);
        }
    }
}
