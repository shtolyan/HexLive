#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{

/// <summary>
/// Modular one-hex hut. It prefers the prefab distilled from the approved
/// Blender kit; the native assembly is a Player-safe artistic derivative of
/// the board/stick/frond resources with the exact 1.5-wu hex dimensions.
/// </summary>
public sealed class HutAssembly : MonoBehaviour
{
    private const string PrefabPath = "HexLive/Objects/building.hut_1hex";
    private static readonly Dictionary<Material, Material> DoubleSidedLeafMaterials = new();
    private static readonly Dictionary<Material, Material> ImportedMaterialVariants = new();
    private static readonly Dictionary<string, Material> NativeMaterials = new();
    private static Mesh? NativeFrondMesh;
    private readonly List<GameObject> _frame = new();
    private readonly List<GameObject> _enclosure = new();
    private readonly List<GameObject> _roof = new();
    private readonly Dictionary<string, List<GameObject>> _buildElements = new();
    private readonly List<Renderer> _cutawayRoof = new();
    private readonly List<Renderer>[] _cutawayWalls =
    {
        new(), new(), new(), new(), new(), new()
    };
    private readonly List<Renderer>[] _cutawayPosts =
    {
        new(), new(), new(), new(), new(), new()
    };
    private readonly HashSet<Renderer> _cutawayAllPosts = new();
    private readonly Vector2[] _cutawayWallDirections = new Vector2[6];
    private readonly Vector2[] _cutawayPairDirections = new Vector2[6];
    private bool _scanned;
    private bool _cutawayScanned;
    private bool _cutawayActive;
    private int _hiddenWallPair = -1;

    // The camera must cross the sector boundary by more than this before the
    // hidden pair changes. Without hysteresis, a camera resting exactly on a
    // sector boundary makes two adjacent pairs alternate because of tiny orbit
    // interpolation.
    private const float CutawaySwitchDotMargin = 0.08f;

    public static GameObject BuildFinished()
    {
        var root = BuildRoot();
        var assembly = root.GetComponent<HutAssembly>() ?? root.AddComponent<HutAssembly>();
        assembly.ApplyAll();
        return root;
    }

    public static GameObject BuildFinished(ObjectSnapshot building)
    {
        var root = BuildRoot();
        var assembly = root.GetComponent<HutAssembly>() ?? root.AddComponent<HutAssembly>();
        if (building.ArchitectureElements.Count > 0) assembly.ApplyElements(building.ArchitectureElements);
        else assembly.ApplyAll();
        return root;
    }

    public static GameObject BuildPartial(ObjectSnapshot site)
    {
        var root = BuildRoot();
        var assembly = root.GetComponent<HutAssembly>() ?? root.AddComponent<HutAssembly>();
        assembly.Apply(site);
        return root;
    }

    public void Apply(ObjectSnapshot site)
    {
        if (site.ArchitectureElements.Count > 0)
        {
            ApplyElements(site.ArchitectureElements);
            return;
        }
        Apply(site.Id.Value, site.DeliveredSticks, site.DeliveredBoards,
            site.DeliveredRope, site.DeliveredLeaves);
    }

    private void ApplyElements(IReadOnlyList<ArchitectureElementSnapshot> elements)
    {
        Scan();
        foreach (var piece in _frame) piece.SetActive(false);
        foreach (var piece in _enclosure) piece.SetActive(false);
        foreach (var piece in _roof) piece.SetActive(false);
        foreach (var element in elements)
        {
            if (_buildElements.TryGetValue(element.SlotKey, out var pieces))
            {
                ToggleFraction(pieces, element.Buildable ? element.Progress : 0f);
            }
        }
    }

    public void Apply(int siteSeed, int sticks, int boards, int rope, int leaves)
    {
        Scan();
        foreach (var piece in _frame) piece.SetActive(false);
        foreach (var piece in _enclosure) piece.SetActive(false);
        foreach (var piece in _roof) piece.SetActive(false);
        foreach (var element in BuildingRules.ResolveHutElements(
                     siteSeed, sticks, boards, rope, leaves))
        {
            if (_buildElements.TryGetValue(element.Key, out var pieces))
            {
                ToggleFraction(pieces, element.Progress);
            }
        }
    }

    public void ApplyAll()
    {
        Scan();
        ToggleFraction(_frame, 1f);
        ToggleFraction(_enclosure, 1f);
        ToggleFraction(_roof, 1f);
    }

    /// <summary>
    /// Purely visual interior cutaway. Build-stage GameObjects keep their
    /// active state; cut pieces switch to ShadowsOnly, so material delivery,
    /// Indoor topology and save state are unaffected and the full shelter
    /// shadow remains visible.
    /// </summary>
    public void SetInteriorCutaway(bool active, Vector3 cameraWorldPosition)
    {
        ScanCutaway();
        if (!active)
        {
            if (_cutawayActive || _hiddenWallPair >= 0)
            {
                SetCutawayRenderers(false, -1);
            }

            _cutawayActive = false;
            _hiddenWallPair = -1;
            return;
        }

        var localCamera = transform.InverseTransformPoint(cameraWorldPosition);
        var cameraDirection = new Vector2(localCamera.x, localCamera.z);
        if (cameraDirection.sqrMagnitude < 0.0001f)
        {
            return;
        }

        cameraDirection.Normalize();
        var bestPair = 0;
        var bestDot = float.NegativeInfinity;
        for (var pair = 0; pair < _cutawayPairDirections.Length; pair++)
        {
            var dot = Vector2.Dot(cameraDirection, _cutawayPairDirections[pair]);
            if (dot > bestDot)
            {
                bestDot = dot;
                bestPair = pair;
            }
        }

        if (_cutawayActive && _hiddenWallPair >= 0 && bestPair != _hiddenWallPair)
        {
            var currentDot = Vector2.Dot(
                cameraDirection, _cutawayPairDirections[_hiddenWallPair]);
            if (bestDot < currentDot + CutawaySwitchDotMargin)
            {
                bestPair = _hiddenWallPair;
            }
        }

        if (!_cutawayActive || bestPair != _hiddenWallPair)
        {
            SetCutawayRenderers(true, bestPair);
            _hiddenWallPair = bestPair;
        }

        _cutawayActive = true;
    }

    private void OnDisable()
    {
        if (!_cutawayScanned) return;
        SetCutawayRenderers(false, -1);
        _cutawayActive = false;
        _hiddenWallPair = -1;
    }

    private void Scan()
    {
        if (_scanned) return;
        _scanned = true;
        CollectStage(transform, "1", _frame);
        CollectStage(transform, "2", _enclosure);
        CollectStage(transform, "3", _roof);
        IndexBuildElements(_frame);
        IndexBuildElements(_enclosure);
        IndexBuildElements(_roof);
    }

    private void IndexBuildElements(List<GameObject> pieces)
    {
        foreach (var piece in pieces)
        {
            var key = ElementKeyFor(piece.name);
            if (key == null) continue;
            if (!_buildElements.TryGetValue(key, out var group))
            {
                group = new List<GameObject>();
                _buildElements[key] = group;
            }

            group.Add(piece);
        }
    }

    private static string? ElementKeyFor(string name)
    {
        var numbers = NumbersIn(name);
        if (name.Contains("Floor_board") || name.Contains("floor_board"))
            return ElementKey(BuildingElementKind.Floor, NumberAt(numbers, 0));
        if (name.Contains("Floor_underbeam") || name.Contains("floor_underbeam"))
            return ElementKey(BuildingElementKind.Floor, NumberAt(numbers, 0) * 2);
        if (name.Contains("Post_") || name.Contains("frame_post_"))
            return ElementKey(BuildingElementKind.Support, NumberAt(numbers, 0) / 2);
        if (name.Contains("Roof_rafter_") || name.Contains("roof_frame_rafter_"))
            return ElementKey(BuildingElementKind.Support, NumberAt(numbers, 0));
        if (name.Contains("Roof_apex_lashing"))
            return ElementKey(BuildingElementKind.Support, 0);
        if (name.StartsWith("HL_Door_Pivot", System.StringComparison.Ordinal) ||
            name.Contains("door_leaf_") || name.Contains("door_lintel_"))
            return ElementKey(BuildingElementKind.Door, 0);
        if (name.Contains("Bay_") || name.Contains("wall_bay_"))
        {
            var bay = NumberAt(numbers, 0);
            var kind = bay == 0 ? BuildingElementKind.Door :
                bay == 3 || bay == 9 ? BuildingElementKind.Window : BuildingElementKind.Wall;
            return ElementKey(kind, bay);
        }
        if (name.Contains("Roof_woven_mat_") || name.Contains("roof_woven_mat_"))
            return ElementKey(BuildingElementKind.Roof, NumberAt(numbers, 0));
        if (name.Contains("Roof_palm_leaf_under_"))
            return ElementKey(BuildingElementKind.Roof, NumberAt(numbers, 1));
        if (name.Contains("Roof_palm_leaf_") || name.Contains("roof_bundle_"))
            return ElementKey(BuildingElementKind.Roof, NumberAt(numbers, 0) % BuildingRules.RoofElementCount);
        return null;
    }

    private static string ElementKey(BuildingElementKind kind, int index) =>
        $"{kind.ToString().ToLowerInvariant()}.{Mathf.Clamp(index, 0, 11)}";

    private static List<int> NumbersIn(string name)
    {
        var result = new List<int>();
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsDigit(name[i])) continue;
            var value = 0;
            while (i < name.Length && char.IsDigit(name[i]))
            {
                value = value * 10 + name[i] - '0';
                i++;
            }
            result.Add(value);
            i--;
        }
        return result;
    }

    private static int NumberAt(List<int> numbers, int index) =>
        index >= 0 && index < numbers.Count ? numbers[index] : 0;

    private static void CollectStage(Transform root, string stage, List<GameObject> pieces)
    {
        foreach (Transform child in root)
        {
            if (child.name == stage || child.name == $"BuildStage_{stage}")
            {
                foreach (Transform piece in child)
                {
                    if (piece.name.StartsWith("HL_Door_State_", System.StringComparison.Ordinal))
                        continue;
                    pieces.Add(piece.gameObject);
                }
                return;
            }

            CollectStage(child, stage, pieces);
            if (pieces.Count > 0) return;
        }
    }

    private static void ToggleFraction(List<GameObject> pieces, float fraction)
    {
        var visible = Mathf.Clamp(Mathf.CeilToInt(pieces.Count * fraction), 0, pieces.Count);
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i] != null && pieces[i].activeSelf != (i < visible))
            {
                pieces[i].SetActive(i < visible);
            }
        }
    }

    private void ScanCutaway()
    {
        if (_cutawayScanned) return;
        _cutawayScanned = true;

        var roofStage = FindStage(transform, "3");
        if (roofStage != null)
        {
            _cutawayRoof.AddRange(roofStage.GetComponentsInChildren<Renderer>(true));
        }

        var wallStage = FindStage(transform, "2");
        if (wallStage == null) return;

        var frameStage = FindStage(transform, "1");
        if (frameStage != null)
        {
            foreach (var renderer in frameStage.GetComponentsInChildren<Renderer>(true))
            {
                if (!TryPostNode(renderer.name, out var node)) continue;
                _cutawayAllPosts.Add(renderer);
                for (var side = 0; side < _cutawayPosts.Length; side++)
                {
                    var firstNode = side * 2;
                    if (node == firstNode || node == (firstNode + 1) % 12 ||
                        node == (firstNode + 2) % 12)
                    {
                        _cutawayPosts[side].Add(renderer);
                    }
                }
            }
        }

        foreach (var renderer in wallStage.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.name.Contains("Roof_") ||
                renderer.name.StartsWith("roof_frame_", System.StringComparison.Ordinal))
            {
                _cutawayRoof.Add(renderer);
                continue;
            }

            if (!TryWallSide(renderer.name, out var side)) continue;
            _cutawayWalls[side].Add(renderer);
        }

        for (var side = 0; side < _cutawayWalls.Length; side++)
        {
            var center = Vector3.zero;
            foreach (var renderer in _cutawayWalls[side])
            {
                center += transform.InverseTransformPoint(renderer.bounds.center);
            }

            if (_cutawayWalls[side].Count > 0)
            {
                center /= _cutawayWalls[side].Count;
                var direction = new Vector2(center.x, center.z);
                _cutawayWallDirections[side] = direction.sqrMagnitude > 0.0001f
                    ? direction.normalized
                    : DirectionForSide(side);
            }
            else
            {
                _cutawayWallDirections[side] = DirectionForSide(side);
            }
        }

        for (var pair = 0; pair < _cutawayPairDirections.Length; pair++)
        {
            var direction = _cutawayWallDirections[pair] +
                _cutawayWallDirections[(pair + 1) % _cutawayWallDirections.Length];
            _cutawayPairDirections[pair] = direction.sqrMagnitude > 0.0001f
                ? direction.normalized
                : DirectionForSide(pair);
        }
    }

    private static Transform? FindStage(Transform root, string stage)
    {
        foreach (Transform child in root)
        {
            if (child.name == stage || child.name == $"BuildStage_{stage}") return child;
            var nested = FindStage(child, stage);
            if (nested != null) return nested;
        }

        return null;
    }

    private static bool TryWallSide(string name, out int side)
    {
        var bay = -1;
        var marker = name.IndexOf("HL_Bay_", System.StringComparison.Ordinal);
        if (marker >= 0 && marker + 9 <= name.Length)
        {
            System.Int32.TryParse(name.Substring(marker + 7, 2), out bay);
        }
        else
        {
            marker = name.IndexOf("wall_bay_", System.StringComparison.Ordinal);
            if (marker >= 0)
            {
                var start = marker + 9;
                var end = start;
                while (end < name.Length && char.IsDigit(name[end])) end++;
                System.Int32.TryParse(name.Substring(start, end - start), out bay);
            }
            else if (name.StartsWith("door_", System.StringComparison.Ordinal))
            {
                // Native fallback puts its door in bay 5; imported Blender art
                // carries the explicit HL_Bay_00 prefix and never uses this.
                bay = 5;
            }
        }

        side = bay >= 0 ? Mathf.Clamp(bay / 2, 0, 5) : -1;
        return side >= 0;
    }

    private static bool TryPostNode(string name, out int node)
    {
        var start = -1;
        var marker = name.IndexOf("HL_Post_", System.StringComparison.Ordinal);
        if (marker >= 0)
        {
            start = marker + 8;
        }
        else
        {
            marker = name.IndexOf("frame_post_", System.StringComparison.Ordinal);
            if (marker >= 0)
            {
                start = marker + 11;
                const string lashing = "lashing_";
                if (name.IndexOf(lashing, start, System.StringComparison.Ordinal) == start)
                {
                    start += lashing.Length;
                }
            }
        }

        if (start < 0)
        {
            node = -1;
            return false;
        }

        var end = start;
        while (end < name.Length && char.IsDigit(name[end])) end++;
        return System.Int32.TryParse(name.Substring(start, end - start), out node) &&
            node is >= 0 and < 12;
    }

    private static Vector2 DirectionForSide(int side)
    {
        var angle = side * 60f * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private void SetCutawayRenderers(bool active, int hiddenPair)
    {
        foreach (var renderer in _cutawayRoof)
        {
            SetCutawayVisibility(renderer, !active);
        }

        var firstHiddenSide = hiddenPair;
        var secondHiddenSide = hiddenPair >= 0
            ? (hiddenPair + 1) % _cutawayWalls.Length
            : -1;
        for (var side = 0; side < _cutawayWalls.Length; side++)
        {
            var visible = !active || side != firstHiddenSide && side != secondHiddenSide;
            foreach (var renderer in _cutawayWalls[side])
            {
                SetCutawayVisibility(renderer, visible);
            }
        }

        foreach (var renderer in _cutawayAllPosts)
        {
            SetCutawayVisibility(renderer, true);
        }

        if (!active) return;
        foreach (var renderer in _cutawayPosts[firstHiddenSide])
        {
            SetCutawayVisibility(renderer, false);
        }

        foreach (var renderer in _cutawayPosts[secondHiddenSide])
        {
            SetCutawayVisibility(renderer, false);
        }
    }

    private static void SetCutawayVisibility(Renderer? renderer, bool visible)
    {
        if (renderer == null) return;
        renderer.shadowCastingMode = visible
            ? UnityEngine.Rendering.ShadowCastingMode.On
            : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
    }

    private static GameObject BuildRoot()
    {
        var prefab = Resources.Load<GameObject>(PrefabPath);
        if (prefab != null && ObjectFit.HasRenderableGeometry(prefab))
        {
            var instance = Object.Instantiate(prefab);
            PrepareImported(instance);
            return instance;
        }

        return BuildNative();
    }

    private static GameObject BuildNative()
    {
        var root = new GameObject("building.hut_1hex (native assembly)");
        var frame = Stage(root.transform, "1");
        var enclosure = Stage(root.transform, "2");
        var roof = Stage(root.transform, "3");

        // Stage 1. Never instance the loose resource props here: a pickup board
        // is a narrow stick-like silhouette and a palm pickup is a whole wild
        // frond with its own pivot. Repeating those assets made the finished
        // house look like scaffolding wrapped in exploded palm trees. Building
        // pieces are artistic derivatives with controlled dimensions.
        var floorLengths = new[] { 1.38f, 2.10f, 2.56f, 2.56f, 2.10f, 1.38f };
        for (var i = 0; i < floorLengths.Length; i++)
        {
            AddRoughBoard(frame, $"frame_floor_board_{i:00}",
                new Vector3(0f, 0.08f, -0.96f + i * 0.384f),
                Quaternion.Euler(90f, 0f, 0f),
                floorLengths[i], 0.36f, 0.09f, i);
        }
        for (var node = 0; node < 12; node++)
        {
            var angle = 90f + node * 30f;
            var radians = angle * Mathf.Deg2Rad;
            // Alternating corner/mid-edge nodes are what makes this a true
            // R=1.5 hex rather than a twelve-sided ring. Midpoints sit at the
            // 1.299-wu apothem shared with the neighbouring hex.
            var radius = node % 2 == 0 ? 1.5f : 1.299038f;
            var p = new Vector3(Mathf.Cos(radians) * radius, 1.10f,
                Mathf.Sin(radians) * radius);
            var tangent = new Vector3(-Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            for (var pair = 0; pair < 2; pair++)
            {
                var offset = tangent * (pair == 0 ? -0.035f : 0.035f);
                AddRoundPole(frame, $"frame_post_{node:00}_{pair}",
                    p + offset + Vector3.up * (HashSigned(node * 11 + pair) * 0.015f),
                    2.20f, 0.055f, node * 2 + pair);
            }

            AddLashing(frame, $"frame_post_lashing_{node:00}",
                p + Vector3.up * 1.00f, 0.095f);
        }

        // Stage 2: two straight 0.75-wu bays per real HEX EDGE. The old code
        // placed twelve bays around a dodecagon and rotated every bay by 30°;
        // their ends could never meet. Here each pair shares one edge tangent.
        for (var edge = 0; edge < 6; edge++)
        {
            var a0 = (90f + edge * 60f) * Mathf.Deg2Rad;
            var a1 = (90f + (edge + 1) * 60f) * Mathf.Deg2Rad;
            var corner0 = new Vector3(Mathf.Cos(a0) * 1.5f, 0f, Mathf.Sin(a0) * 1.5f);
            var corner1 = new Vector3(Mathf.Cos(a1) * 1.5f, 0f, Mathf.Sin(a1) * 1.5f);
            var edgeVector = corner1 - corner0;
            var yaw = Mathf.Atan2(-edgeVector.z, edgeVector.x) * Mathf.Rad2Deg;
            for (var half = 0; half < 2; half++)
            {
                var bay = edge * 2 + half;
                var center = Vector3.Lerp(corner0, corner1, half == 0 ? 0.25f : 0.75f);
                if (bay == 0)
                {
                    AddRoughBoard(enclosure, $"door_lintel_{bay:00}",
                        center + Vector3.up * 1.82f, Quaternion.Euler(0f, yaw, 0f),
                        0.79f, 0.31f, 0.10f, 100 + bay);
                    AddOpenDoor(enclosure, center, yaw, bay);
                    continue;
                }

                var levels = bay is 1 or 9
                    ? new[] { 0.37f, 1.68f }
                    : new[] { 0.37f, 1.03f, 1.69f };
                foreach (var y in levels)
                {
                    AddRoughBoard(enclosure, $"wall_bay_{bay:00}_{y:0.00}",
                        center + Vector3.up * y, Quaternion.Euler(0f, yaw, 0f),
                        0.80f, bay is 1 or 9 ? 0.48f : 0.60f, 0.10f,
                        200 + bay * 7 + Mathf.RoundToInt(y * 10f));
                }
            }
        }
        for (var side = 0; side < 6; side++)
        {
            var angle = 90f - side * 60f;
            var radians = angle * Mathf.Deg2Rad;
            var eave = new Vector3(Mathf.Cos(radians) * 1.48f, 2.14f, Mathf.Sin(radians) * 1.48f);
            var apex = new Vector3(0f, 2.72f, 0f);
            AddCraftBeam(enclosure, $"roof_frame_rafter_{side}", apex, eave, 0.045f,
                "Heartwood");
        }

        // Stage 3: twelve delivered bundles. Each is one direct stage child,
        // so progress reveals whole bundles; inside it the six roof directions
        // receive two compact, controlled fronds. Woven triangular mats below
        // guarantee a closed silhouette, while the 144 fronds supply the dense
        // irregular leaf edge approved in the Blender study.
        for (var bundle = 0; bundle < BuildingRules.RoofLeaves; bundle++)
        {
            var bundleRoot = new GameObject($"roof_bundle_{bundle:00}");
            bundleRoot.transform.SetParent(roof, false);
            if (bundle < 6) AddRoofMat(bundleRoot.transform, bundle);
            for (var crown = 0; crown < 2; crown++)
            {
                for (var side = 0; side < 6; side++)
                {
                    var seed = bundle * 37 + crown * 101 + side * 47;
                    var jitter = HashSigned(seed) * 5.5f;
                    var angle = side * 60f + crown * 30f + jitter;
                    var radians = angle * Mathf.Deg2Rad;
                    var lateral = ((bundle % 3) - 1) * 0.16f +
                        HashSigned(seed + 7) * 0.035f;
                    var outward = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
                    var across = new Vector3(outward.z, 0f, -outward.x);
                    var start = outward * (0.08f + crown * 0.04f) + across * lateral +
                        Vector3.up * (2.76f + HashSigned(seed + 11) * 0.035f);
                    var end = outward * (1.73f + HashSigned(seed + 3) * 0.12f) +
                        across * lateral + Vector3.up * (2.05f + HashSigned(seed + 13) * 0.06f);
                    AddRoofFrond(bundleRoot.transform,
                        $"roof_frond_{bundle:00}_{crown}_{side}", start, end,
                        0.22f + HashSigned(seed + 17) * 0.025f,
                        (bundle + crown + side) % 3);
                }
            }
        }

        return root;
    }

    private static Transform Stage(Transform root, string name)
    {
        var stage = new GameObject(name);
        stage.transform.SetParent(root, false);
        return stage.transform;
    }

    private static void PrepareImported(GameObject root)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var sourceMaterials = renderer.sharedMaterials;
            var remapped = new Material[sourceMaterials.Length];
            for (var i = 0; i < sourceMaterials.Length; i++)
            {
                remapped[i] = ImportedMaterial(sourceMaterials[i]);
            }

            renderer.sharedMaterials = remapped;
            renderer.receiveShadows = true;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        PrepareImportedDoor(root);
    }

    private static void PrepareImportedDoor(GameObject root)
    {
        Transform? pivot = null;
        Transform? closed = null;
        Transform? open = null;
        foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
        {
            if (candidate.name.StartsWith("HL_Door_Pivot", System.StringComparison.Ordinal)) pivot = candidate;
            else if (candidate.name.StartsWith("HL_Door_State_Closed", System.StringComparison.Ordinal)) closed = candidate;
            else if (candidate.name.StartsWith("HL_Door_State_Open", System.StringComparison.Ordinal)) open = candidate;
        }

        if (pivot == null || closed == null || open == null) return;
        var visual = pivot.GetComponent<HutDoorVisual>() ?? pivot.gameObject.AddComponent<HutDoorVisual>();
        visual.Configure(closed, open, startOpen: true);
        closed.gameObject.SetActive(false);
        open.gameObject.SetActive(false);
    }

    private static Material ImportedMaterial(Material? source)
    {
        if (source == null) return NativeMaterial("Board");
        if (ImportedMaterialVariants.TryGetValue(source, out var cached) && cached != null)
        {
            return cached;
        }

        // FBX has already converted Blender's stored linear Base Color into
        // Unity's colour representation. Preserve it exactly. Applying
        // Color.linear here a second time was what made the first Unity pass
        // much darker and browner than the approved Blender render.
        var material = new Material(source) { name = source.name + " (hut URP)" };
        var leaf = source.name.StartsWith("LeafGreen", System.StringComparison.Ordinal) ||
            source.name.StartsWith("HL_Roof_PalmMat_", System.StringComparison.Ordinal);
        // Several Blender boards are deliberately thin open shells. They must
        // remain visible from inside/cutaway and from below the raised floor;
        // otherwise URP back-face culling looks like random missing planks.
        // Apply this to every hut material, including future names, rather than
        // relying on a palette-name allow-list that silently misses new art.
        material.doubleSidedGI = true;
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", leaf ? 0.04f : 0.08f);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        material.enableInstancing = true;
        ImportedMaterialVariants[source] = material;
        return material;
    }

    private static void AddRoughBoard(Transform parent, string name, Vector3 position,
        Quaternion rotation, float length, float height, float depth, int seed)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = position + new Vector3(
            HashSigned(seed + 1) * 0.008f,
            HashSigned(seed + 2) * 0.012f,
            HashSigned(seed + 3) * 0.008f);
        piece.transform.localRotation = rotation * Quaternion.Euler(
            HashSigned(seed + 4) * 0.9f,
            HashSigned(seed + 5) * 0.8f,
            HashSigned(seed + 6) * 1.2f);
        piece.transform.localScale = new Vector3(
            length * (1f + HashSigned(seed + 7) * 0.025f),
            height * (1f + HashSigned(seed + 8) * 0.035f), depth);
        piece.GetComponent<Renderer>().sharedMaterial = NativeMaterial(
            seed % 3 == 0 ? "Sapwood" : seed % 3 == 1 ? "Heartwood" : "Bark");
        DestroyRuntime(piece.GetComponent<Collider>());
    }

    private static void AddRoundPole(Transform parent, string name, Vector3 position,
        float height, float radius, int seed)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = position;
        piece.transform.localRotation = Quaternion.Euler(
            HashSigned(seed + 9) * 1.2f, 0f, HashSigned(seed + 10) * 1.2f);
        piece.transform.localScale = new Vector3(radius, height * 0.5f, radius);
        piece.GetComponent<Renderer>().sharedMaterial = NativeMaterial(
            seed % 2 == 0 ? "Bark" : "Heartwood");
        DestroyRuntime(piece.GetComponent<Collider>());
    }

    private static void AddLashing(Transform parent, string name, Vector3 position, float radius)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = position;
        piece.transform.localScale = new Vector3(radius, 0.018f, radius);
        piece.GetComponent<Renderer>().sharedMaterial = NativeMaterial("Rope");
        DestroyRuntime(piece.GetComponent<Collider>());
    }

    private static void AddOpenDoor(Transform parent, Vector3 bayCenter, float wallYaw, int seed)
    {
        var wallDirection = Quaternion.Euler(0f, wallYaw, 0f) * Vector3.right;
        var hinge = bayCenter - wallDirection * 0.31f;
        var pivot = new GameObject("HL_Door_Pivot");
        pivot.transform.SetParent(parent, false);
        pivot.transform.localPosition = hinge;
        var closedRotation = Quaternion.Euler(0f, wallYaw, 0f);
        var openRotation = Quaternion.Euler(0f, wallYaw - 72f, 0f);
        pivot.transform.localRotation = openRotation;
        for (var i = 0; i < 3; i++)
        {
            AddRoughBoard(pivot.transform, $"door_leaf_board_{i}",
                new Vector3(0.105f + i * 0.205f, 0.78f, 0f), Quaternion.Euler(0f, 0f, 90f),
                1.36f, 0.18f, 0.075f, 500 + seed * 7 + i);
        }
        AddCraftBeam(pivot.transform, "door_leaf_brace",
            new Vector3(0.03f, 0.30f, 0f), new Vector3(0.59f, 1.26f, 0f),
            0.025f, "Heartwood");
        var visual = pivot.AddComponent<HutDoorVisual>();
        visual.Configure(hinge, closedRotation, hinge, openRotation, startOpen: true);
    }

    private static void AddCraftBeam(Transform parent, string name, Vector3 from,
        Vector3 to, float radius, string material)
    {
        var delta = to - from;
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        piece.name = name;
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = (from + to) * 0.5f;
        piece.transform.localRotation = Quaternion.FromToRotation(Vector3.up, delta.normalized);
        piece.transform.localScale = new Vector3(radius, delta.magnitude * 0.5f, radius);
        piece.GetComponent<Renderer>().sharedMaterial = NativeMaterial(material);
        DestroyRuntime(piece.GetComponent<Collider>());
    }

    private static void AddRoofMat(Transform parent, int side)
    {
        var a0 = (side * 60f - 30f) * Mathf.Deg2Rad;
        var a1 = (side * 60f + 30f) * Mathf.Deg2Rad;
        var mesh = new Mesh { name = $"roof_mat_mesh_{side}" };
        var apex = new Vector3(0f, 2.70f, 0f);
        var left = new Vector3(Mathf.Sin(a0) * 1.66f, 2.08f, Mathf.Cos(a0) * 1.66f);
        var right = new Vector3(Mathf.Sin(a1) * 1.66f, 2.08f, Mathf.Cos(a1) * 1.66f);
        mesh.vertices = new[]
        {
            apex, left, right,
            apex, left, right
        };
        // Separate vertices for the reverse face. Sharing the same three
        // vertices would cancel RecalculateNormals; relying only on Cull Off
        // left some URP camera angles reading the interior as a black wedge.
        mesh.triangles = new[] { 0, 1, 2, 3, 5, 4 };
        var normal = Vector3.Cross(left - apex, right - apex).normalized;
        mesh.normals = new[] { normal, normal, normal, -normal, -normal, -normal };
        var piece = new GameObject($"roof_woven_mat_{side}");
        piece.transform.SetParent(parent, false);
        var filter = piece.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        piece.AddComponent<MeshRenderer>().sharedMaterial = NativeMaterial("LeafMat");
    }

    private static void AddRoofFrond(Transform parent, string name, Vector3 from,
        Vector3 to, float width, int materialVariant)
    {
        var delta = to - from;
        var piece = new GameObject(name);
        piece.transform.SetParent(parent, false);
        piece.transform.localPosition = from;
        piece.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, delta.normalized);
        piece.transform.localScale = new Vector3(width / 0.22f, width / 0.22f, delta.magnitude);
        var filter = piece.AddComponent<MeshFilter>();
        filter.sharedMesh = FrondMesh();
        piece.AddComponent<MeshRenderer>().sharedMaterial = NativeMaterial(
            materialVariant == 0 ? "LeafDark" : materialVariant == 1 ? "Leaf" : "LeafLight");
    }

    private static Mesh FrondMesh()
    {
        if (NativeFrondMesh != null) return NativeFrondMesh;
        const int segments = 14;
        var vertices = new List<Vector3>(segments * 6 + 4);
        var triangles = new List<int>(segments * 6 + 6);
        for (var i = 0; i < segments; i++)
        {
            var t0 = i / (float)segments;
            var t1 = (i + 1f) / segments;
            var middle = (t0 + t1) * 0.5f;
            var width = Mathf.Sin(middle * Mathf.PI) * 0.22f * (0.92f + HashSigned(i * 13) * 0.08f);
            var baseIndex = vertices.Count;
            vertices.Add(new Vector3(0f, HashSigned(i * 7) * 0.006f, t0));
            vertices.Add(new Vector3(-width, 0f, middle));
            vertices.Add(new Vector3(0f, HashSigned(i * 7 + 1) * 0.006f, t1));
            vertices.Add(new Vector3(0f, HashSigned(i * 7 + 2) * 0.006f, t0));
            vertices.Add(new Vector3(width, 0f, middle));
            vertices.Add(new Vector3(0f, HashSigned(i * 7 + 3) * 0.006f, t1));
            triangles.Add(baseIndex); triangles.Add(baseIndex + 1); triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3); triangles.Add(baseIndex + 5); triangles.Add(baseIndex + 4);
        }
        var stem = vertices.Count;
        vertices.Add(new Vector3(-0.012f, 0.004f, 0f));
        vertices.Add(new Vector3(0.012f, 0.004f, 0f));
        vertices.Add(new Vector3(-0.006f, 0.004f, 1f));
        vertices.Add(new Vector3(0.006f, 0.004f, 1f));
        triangles.Add(stem); triangles.Add(stem + 2); triangles.Add(stem + 1);
        triangles.Add(stem + 1); triangles.Add(stem + 2); triangles.Add(stem + 3);
        NativeFrondMesh = new Mesh { name = "hut_roof_frond" };
        NativeFrondMesh.SetVertices(vertices);
        NativeFrondMesh.SetTriangles(triangles, 0);
        NativeFrondMesh.RecalculateNormals();
        NativeFrondMesh.RecalculateBounds();
        return NativeFrondMesh;
    }

    private static Material NativeMaterial(string key)
    {
        if (NativeMaterials.TryGetValue(key, out var material) && material != null) return material;
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        material = new Material(shader) { name = $"Hut {key}" };
        var authoredSrgb = key switch
        {
            "Bark" => new Color(0.30f, 0.17f, 0.08f),
            "BarkLight" => new Color(0.43f, 0.27f, 0.13f),
            "Heartwood" => new Color(0.48f, 0.28f, 0.13f),
            "Sapwood" => new Color(0.63f, 0.44f, 0.25f),
            "SapwoodLight" => new Color(0.76f, 0.56f, 0.30f),
            "BoardDark" => new Color(0.25f, 0.14f, 0.065f),
            "Board" => new Color(0.55f, 0.36f, 0.17f),
            "BoardLight" => new Color(0.72f, 0.53f, 0.28f),
            "Rope" => new Color(0.66f, 0.54f, 0.32f),
            "LeafDark" => new Color(0.13f, 0.31f, 0.10f),
            "LeafLight" => new Color(0.34f, 0.52f, 0.18f),
            "LeafMatDark" => new Color(0.15f, 0.31f, 0.105f),
            "LeafMat" => new Color(0.19f, 0.38f, 0.125f),
            "LeafMatLight" => new Color(0.23f, 0.43f, 0.145f),
            _ => new Color(0.22f, 0.43f, 0.14f)
        };
        // Blender's Base Color values are authored in sRGB. Runtime material
        // setters feed shader-space values directly in a Linear project, so
        // convert explicitly; otherwise pale boards and leaves look bleached.
        material.color = authoredSrgb.linear;
        material.doubleSidedGI = true;
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        if (key.StartsWith("Leaf") && material.HasProperty("_EmissionColor"))
        {
            // Palm planes turn almost black when their authored normal faces
            // away from a low sun. A restrained green bounce keeps the thatch
            // readable at night without making it an unlit/neon roof.
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", material.color * 0.10f);
        }
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.02f);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        material.enableInstancing = true;
        NativeMaterials[key] = material;
        return material;
    }

    private static void AddBeam(Transform parent, string name, Vector3 from, Vector3 to)
    {
        var delta = to - from;
        AddResource(parent, "resource.stick", name, (from + to) * 0.5f,
            Quaternion.FromToRotation(Vector3.right, delta.normalized), delta.magnitude);
    }

    private static void AddResource(Transform parent, string id, string name,
        Vector3 position, Quaternion rotation, float desiredLength)
    {
        var prefab = WorldPropResources.Load(id);
        GameObject piece;
        if (prefab != null && ObjectFit.HasRenderableGeometry(prefab))
        {
            piece = Object.Instantiate(prefab, parent);
            piece.name = name;
            piece.transform.localPosition = Vector3.zero;
            piece.transform.localRotation = Quaternion.identity;
            piece.transform.localScale = Vector3.one;
            if (ObjectFit.WorldBounds(piece, out var bounds))
            {
                var longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (longest > 0.0001f) piece.transform.localScale *= desiredLength / longest;
            }

            if (id == "resource.palm_leaf") MakeLeavesDoubleSided(piece);
        }
        else
        {
            piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.name = name + " (fallback)";
            piece.transform.SetParent(parent, false);
            piece.transform.localScale = id == "resource.palm_leaf"
                ? new Vector3(0.30f, 0.025f, desiredLength)
                : new Vector3(desiredLength, 0.10f, 0.13f);
            var renderer = piece.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = FallbackMaterial(id);
        }

        piece.transform.localPosition = position;
        piece.transform.localRotation = rotation;
        foreach (var collider in piece.GetComponentsInChildren<Collider>(true))
        {
            DestroyRuntime(collider);
        }
    }

    private static void MakeLeavesDoubleSided(GameObject piece)
    {
        // The loose resource is normally seen from above, so its imported
        // material may cull the reverse faces. A roof is viewed from every
        // azimuth: without a local double-sided variant, one half reads as
        // bare stems while the opposite half has leaflets. Reuse one clone per
        // source material instead of allocating a material per frond.
        foreach (var renderer in piece.GetComponentsInChildren<Renderer>(true))
        {
            var source = renderer.sharedMaterial;
            if (source == null) continue;
            if (!DoubleSidedLeafMaterials.TryGetValue(source, out var roofMaterial) ||
                roofMaterial == null)
            {
                roofMaterial = new Material(source)
                {
                    name = source.name + " (hut roof double-sided)",
                    doubleSidedGI = true
                };
                if (roofMaterial.HasProperty("_Cull")) roofMaterial.SetFloat("_Cull", 0f);
                DoubleSidedLeafMaterials[source] = roofMaterial;
            }

            renderer.sharedMaterial = roofMaterial;
        }
    }

    private static Material FallbackMaterial(string id)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader);
        material.color = id == "resource.palm_leaf"
            ? new Color(0.22f, 0.42f, 0.15f)
            : new Color(0.38f, 0.20f, 0.09f);
        material.doubleSidedGI = true;
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        return material;
    }

    private static float HashSigned(int value)
    {
        unchecked
        {
            uint x = (uint)value + 0x9E3779B9u;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            return (x & 0xFFFFu) / 32767.5f - 1f;
        }
    }

    private static void DestroyRuntime(Object value)
    {
        if (Application.isPlaying) Object.Destroy(value);
        else Object.DestroyImmediate(value);
    }
}

}
