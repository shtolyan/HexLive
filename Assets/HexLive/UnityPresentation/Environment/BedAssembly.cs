#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: toggle-assembles a bed from the assembled prefab's OWN piece
    /// children (<c>log_*</c> / <c>stick_*</c> / <c>rope_*</c> / <c>leaf_*</c>).
    /// The SAME prefab (bed_leaf_final / bed_basic_final, authored in Blender at
    /// 1:1) renders BOTH the finished bed (all pieces on) and the progressive
    /// build-site (the first N pieces of each material on, per delivered count) —
    /// so the mat visibly grows into exactly the finished piece as resources are
    /// hauled in, with no separate slot table to keep in sync.
    /// </summary>
    public sealed class BedAssembly : MonoBehaviour
    {
        private readonly List<GameObject> _logs = new();
        private readonly List<GameObject> _sticks = new();
        private readonly List<GameObject> _ropes = new();
        private readonly List<GameObject> _leaves = new();
        private readonly List<GameObject> _stones = new(); // §54.14: campfire ring
        private bool _scanned;

        private void Scan()
        {
            if (_scanned)
            {
                return;
            }

            _scanned = true;

            // §54.12: pieces live either directly under the root (old flat
            // prefabs) or inside numbered STAGE groups ("1".."4") that encode
            // the build order (BuildSiteMath.BedLeafStages). Walk the groups
            // in numeric order, each group's pieces name-sorted, so "the
            // first N pieces of a material" always lights up stage by stage.
            var stages = new List<Transform>();
            var flat = new List<Transform>();
            foreach (Transform t in transform)
            {
                if (int.TryParse(t.name, out _))
                {
                    stages.Add(t);
                }
                else
                {
                    flat.Add(t); // flat-prefab fallback
                }
            }

            flat.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var piece in flat)
            {
                AddPiece(piece);
            }

            stages.Sort((a, b) => int.Parse(a.name).CompareTo(int.Parse(b.name)));
            foreach (var stage in stages)
            {
                var pieces = new List<Transform>();
                foreach (Transform t in stage)
                {
                    pieces.Add(t);
                }

                pieces.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                foreach (var piece in pieces)
                {
                    AddPiece(piece);
                }
            }
        }

        private void AddPiece(Transform t)
        {
            var n = t.name;
            if (n.StartsWith("log_")) _logs.Add(t.gameObject);
            else if (n.StartsWith("stick_")) _sticks.Add(t.gameObject);
            else if (n.StartsWith("rope_")) _ropes.Add(t.gameObject);
            else if (n.StartsWith("leaf_")) _leaves.Add(t.gameObject);
            else if (n.StartsWith("stone_")) _stones.Add(t.gameObject);
        }

        /// Show the whole bed (every piece on).
        public void ApplyAll()
        {
            Scan();
            Apply(_logs.Count, _sticks.Count, _ropes.Count, _leaves.Count, _stones.Count);
        }

        /// Show only the delivered pieces: the first N of each material on, rest off.
        public void Apply(int logs, int sticks, int ropes, int leaves, int stones = 0)
        {
            Scan();
            Toggle(_logs, logs);
            Toggle(_sticks, sticks);
            Toggle(_ropes, ropes);
            Toggle(_leaves, leaves);
            Toggle(_stones, stones);
        }

        private static void Toggle(List<GameObject> list, int on)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var piece = list[i];
                if (piece == null) // destroyed externally — don't stall the render loop
                {
                    continue;
                }

                var active = i < on;
                if (piece.activeSelf != active)
                {
                    piece.SetActive(active);
                }
            }
        }

        // ── Factory ────────────────────────────────────────────────────────────
        // Which assembled prefab renders each staged product.
        private static string PrefabPath(string product) => "HexLive/Objects/" + product switch
        {
            "bed.basic" => "bed_basic_final_native",
            // drying rack is assembled directly from approved native
            // resource.stick + rope_lashing assets above.
            "station.drying_rack" => "drying_rack_final",
            "campfire.spot" => "campfire_final_native",
            "station.water_collector" => "water_collector_final_native",
            _ => "bed_leaf_final_native"
        };

        /// §35.5B/§54.14: every product rendered by a staged assembled prefab —
        /// the beds, the drying rack, the campfire and the water collector share
        /// the grow-in-place build-site view.
        public static bool IsAssembled(string product) =>
            product is "bed.leaf" or "bed.basic" or "station.drying_rack"
                or "campfire.spot" or "station.water_collector";

        private static GameObject? Instantiate(string product, out BedAssembly asm)
        {
            asm = null!;
            if (product == "station.drying_rack")
            {
                var rack = BuildNativeDryingRack();
                if (HasExpectedDryingRack(rack))
                {
                    asm = rack.GetComponent<BedAssembly>() ?? rack.AddComponent<BedAssembly>();
                    return rack;
                }
                Object.Destroy(rack);
            }

            // Never fall back to drying_rack_final: its four nested stands are
            // legacy stick_final→GLB wrappers. If the native assembly contract
            // fails, use the emergency primitive assembly below instead.
            var prefab = product == "station.drying_rack"
                ? null
                : Resources.Load<GameObject>(PrefabPath(product));
            GameObject? go = null;
            if (prefab != null)
            {
                go = Object.Instantiate(prefab);
                // A Resources prefab that points at a glTF ScriptedImporter can
                // survive a Player build as an empty hierarchy.  A non-null
                // wrapper is not proof that its mesh sub-assets were packed.
                if (!ObjectFit.HasRenderableGeometry(go))
                {
                    Object.Destroy(go);
                    go = null;
                }
            }

            go ??= BuildSafeFallback(product);
            asm = go.GetComponent<BedAssembly>() ?? go.AddComponent<BedAssembly>();
            return go;
        }

        private static GameObject BuildNativeDryingRack()
        {
            var root = new GameObject("drying_rack_final (native assembly)");
            var stick = WorldPropResources.Load("resource.stick");
            var rope = Resources.Load<GameObject>("HexLive/Objects/rope_lashing");
            if (stick == null || rope == null) return root;

            AddNative(root.transform, stick, "stick_00", new Vector3(-0.44f, 0.3675f, 0f),
                Quaternion.Euler(0f, 0f, 90f));
            AddNative(root.transform, stick, "stick_01", new Vector3(0.44f, 0.3675f, 0f),
                Quaternion.Euler(0f, 0f, 90f));
            AddNative(root.transform, stick, "stick_02", new Vector3(0f, 0.78f, 0.055f),
                Quaternion.identity);
            AddNative(root.transform, stick, "stick_03", new Vector3(0f, 0.50f, -0.055f),
                Quaternion.identity);

            var joints = new[]
            {
                new Vector3(-0.44f, 0.78f, 0.055f), new Vector3(0.44f, 0.78f, 0.055f),
                new Vector3(-0.44f, 0.50f, -0.055f), new Vector3(0.44f, 0.50f, -0.055f)
            };
            for (var i = 0; i < joints.Length; i++)
            {
                AddNative(root.transform, rope, $"rope_{i:00}", joints[i], Quaternion.identity);
            }
            return root;
        }

        private static void AddNative(Transform parent, GameObject prefab, string name,
            Vector3 position, Quaternion rotation)
        {
            var piece = Object.Instantiate(prefab, parent);
            piece.name = name;
            piece.transform.localPosition = position;
            piece.transform.localRotation = rotation;
            foreach (var collider in piece.GetComponentsInChildren<Collider>()) Object.Destroy(collider);
        }

        private static bool HasExpectedDryingRack(GameObject rack)
        {
            var sticks = 0;
            var ropes = 0;
            foreach (Transform child in rack.transform)
            {
                if (!ObjectFit.HasRenderableGeometry(child.gameObject)) continue;
                if (child.name.StartsWith("stick_")) sticks++;
                if (child.name.StartsWith("rope_")) ropes++;
            }
            return sticks == 4 && ropes == 4;
        }

        /// <summary>
        /// Build-safe last resort for every assembled product. These pieces use
        /// Unity meshes and URP materials only, so a stripped glTF dependency can
        /// never turn a real simulation object into an invisible empty wrapper.
        /// Names deliberately follow the normal log_/stick_/rope_/leaf_/stone_
        /// contract, therefore partial sites and finished structures share the
        /// exact same staging code.
        /// </summary>
        private static GameObject BuildSafeFallback(string product)
        {
            var root = new GameObject($"{product} (build-safe fallback)");
            switch (product)
            {
                case "campfire.spot":
                    for (var i = 0; i < 10; i++)
                    {
                        var angle = i * 30f;
                        AddPiece(root.transform, $"stick_{i:00}", PrimitiveType.Cylinder,
                            new Vector3(0.035f, 0.34f, 0.035f),
                            new Vector3(0f, 0.08f + (i % 2) * 0.035f, 0f),
                            new Vector3(90f, angle, 0f), new Color(0.48f, 0.27f, 0.14f));
                    }
                    for (var i = 0; i < 18; i++)
                    {
                        var radians = i * Mathf.PI * 2f / 18f;
                        AddPiece(root.transform, $"stone_{i:00}", PrimitiveType.Sphere,
                            new Vector3(0.16f, 0.10f, 0.13f),
                            new Vector3(Mathf.Cos(radians) * 0.46f, 0.07f,
                                Mathf.Sin(radians) * 0.46f),
                            new Vector3(0f, i * 37f, 0f), new Color(0.43f, 0.45f, 0.54f));
                    }
                    // The final two sticks read as the roasting spit once stage 3 is reached.
                    AddPiece(root.transform, "stick_spit_post_l", PrimitiveType.Cylinder,
                        new Vector3(0.025f, 0.34f, 0.025f), new Vector3(-0.52f, 0.34f, 0f),
                        new Vector3(0f, 0f, -10f), new Color(0.38f, 0.20f, 0.10f));
                    AddPiece(root.transform, "stick_spit_post_r", PrimitiveType.Cylinder,
                        new Vector3(0.025f, 0.34f, 0.025f), new Vector3(0.52f, 0.34f, 0f),
                        new Vector3(0f, 0f, 10f), new Color(0.38f, 0.20f, 0.10f));
                    AddPiece(root.transform, "rope_spit_l", PrimitiveType.Sphere,
                        Vector3.one * 0.07f, new Vector3(-0.52f, 0.58f, 0f),
                        Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
                    AddPiece(root.transform, "rope_spit_r", PrimitiveType.Sphere,
                        Vector3.one * 0.07f, new Vector3(0.52f, 0.58f, 0f),
                        Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
                    break;

                case "station.drying_rack":
                    AddFrame(root.transform, 0.75f, 0.85f, 0.05f);
                    break;

                case "station.water_collector":
                    AddFrame(root.transform, 0.62f, 0.72f, 0.045f);
                    AddPiece(root.transform, "leaf_funnel", PrimitiveType.Cylinder,
                        new Vector3(0.42f, 0.08f, 0.42f), new Vector3(0f, 0.58f, 0f),
                        Vector3.zero, new Color(0.25f, 0.53f, 0.23f));
                    break;

                default: // leaf/basic beds
                    for (var i = 0; i < 4; i++)
                    {
                        AddPiece(root.transform, $"log_{i:00}", PrimitiveType.Cylinder,
                            new Vector3(0.08f, 0.72f, 0.08f),
                            new Vector3((i < 2 ? -0.48f : 0.48f), 0.10f,
                                (i % 2 == 0 ? -0.62f : 0.62f)),
                            new Vector3(90f, 0f, 0f), new Color(0.43f, 0.24f, 0.12f));
                    }
                    for (var i = 0; i < 12; i++)
                    {
                        AddPiece(root.transform, $"leaf_{i:00}", PrimitiveType.Cube,
                            new Vector3(0.16f, 0.025f, 1.05f),
                            new Vector3(-0.44f + i * 0.08f, 0.18f, 0f),
                            new Vector3(0f, (i % 2 == 0 ? -5f : 5f), 0f),
                            new Color(0.28f, 0.54f, 0.23f));
                    }
                    break;
            }
            return root;
        }

        private static void AddFrame(Transform root, float halfWidth, float height, float radius)
        {
            var wood = new Color(0.43f, 0.24f, 0.12f);
            AddPiece(root, "log_post_l", PrimitiveType.Cylinder,
                new Vector3(radius, height * 0.5f, radius), new Vector3(-halfWidth, height * 0.5f, 0f),
                Vector3.zero, wood);
            AddPiece(root, "log_post_r", PrimitiveType.Cylinder,
                new Vector3(radius, height * 0.5f, radius), new Vector3(halfWidth, height * 0.5f, 0f),
                Vector3.zero, wood);
            AddPiece(root, "log_crossbar", PrimitiveType.Cylinder,
                new Vector3(radius, halfWidth, radius), new Vector3(0f, height, 0f),
                new Vector3(0f, 0f, 90f), wood);
            AddPiece(root, "rope_lashing_l", PrimitiveType.Sphere,
                Vector3.one * radius * 2.4f, new Vector3(-halfWidth, height, 0f),
                Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
            AddPiece(root, "rope_lashing_r", PrimitiveType.Sphere,
                Vector3.one * radius * 2.4f, new Vector3(halfWidth, height, 0f),
                Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
        }

        private static void AddPiece(Transform root, string name, PrimitiveType type,
            Vector3 scale, Vector3 position, Vector3 rotation, Color color)
        {
            var piece = GameObject.CreatePrimitive(type);
            piece.name = name;
            piece.transform.SetParent(root, false);
            piece.transform.localScale = scale;
            piece.transform.localPosition = position;
            piece.transform.localEulerAngles = rotation;
            foreach (var collider in piece.GetComponents<Collider>())
            {
                Object.Destroy(collider);
            }
            var renderer = piece.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (renderer != null && shader != null)
            {
                var material = new Material(shader) { color = color };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                material.SetFloat("_Smoothness", 0.08f);
                renderer.sharedMaterial = material;
            }
        }

        /// A finished bed — every piece visible. Absolute-sized (1:1), so the caller
        /// must NOT run FitObjectPrefab on it.
        public static GameObject? BuildFinished(string product)
        {
            var go = Instantiate(product, out var asm);
            asm?.ApplyAll();
            return go;
        }

        /// A build-site in progress — only the delivered pieces of each material.
        public static GameObject? BuildPartial(
            string product, int logs, int sticks, int ropes, int leaves, int stones = 0)
        {
            var go = Instantiate(product, out var asm);
            asm?.Apply(logs, sticks, ropes, leaves, stones);
            return go;
        }
    }
}
