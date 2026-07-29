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
            "bed.basic" => "bed_basic_final",
            "station.drying_rack" => "drying_rack_final",
            "campfire.spot" => "campfire_final",
            "station.water_collector" => "water_collector_final",
            _ => "bed_leaf_final"
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
            var prefab = Resources.Load<GameObject>(PrefabPath(product));
            if (prefab == null)
            {
                return null;
            }

            var go = Object.Instantiate(prefab);
            asm = go.GetComponent<BedAssembly>() ?? go.AddComponent<BedAssembly>();
            return go;
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
