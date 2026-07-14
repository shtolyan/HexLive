#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: build the two beds from the game's own primitive props — logs,
    /// sticks, leaves and rope. Each bed is defined as an ordered list of SLOTS
    /// (one primitive piece + where it goes). From that one list we render the
    /// finished bed, a PARTIALLY built bed (only the delivered pieces, each already
    /// sitting in its final spot — the progressive build-site), and the exact
    /// material bill (how many of each resource the bed is made of). Built at
    /// absolute size, so the caller must NOT run FitObjectPrefab on it.
    /// </summary>
    public static class BedFactory
    {
        // Sleeper body height above the ground, per bed, in HexRadius units
        // (measured in the BuildingTest scene). Premium sits up on its log frame;
        // the leaf mat is almost on the floor.
        private const float BasicSleepLiftR = 0.36f;
        private const float LeafSleepLiftR = 0.041f;

        // One piece of a bed: which resource it is, how long to render it, and its
        // place/rotation local to the bed root.
        public readonly struct Slot
        {
            public readonly string Material; // resource id (resource.log / .stick / .palm_leaf / .rope)
            public readonly float TargetLen;
            public readonly Vector3 Pos;
            public readonly Vector3 Euler;

            public Slot(string material, float targetLen, Vector3 pos, Vector3 euler)
            {
                Material = material;
                TargetLen = targetLen;
                Pos = pos;
                Euler = euler;
            }
        }

        public static bool IsBed(string definitionId) =>
            definitionId == "bed.basic" || definitionId == "bed.leaf";

        // Map a bill resource id to the prefab that renders it (leaf → frond).
        private static GameObject? Prefab(string material) => Resources.Load<GameObject>(
            material == "resource.palm_leaf"
                ? "HexLive/Objects/palm_frond"
                : $"HexLive/Objects/{material}");

        // The ordered piece list for a bed. Order = the order pieces are laid down.
        public static List<Slot> Slots(string definitionId)
        {
            var r = HexLive.UnityPresentation.Spatial.SimulationUnityMapper.HexRadius;
            return definitionId == "bed.basic" ? BedrollSlots(r) : LeafMatSlots(r);
        }

        // Exact material bill: how many of each resource the bed is built from.
        public static Dictionary<string, int> BillFor(string definitionId)
        {
            var bill = new Dictionary<string, int>();
            foreach (var s in Slots(definitionId))
            {
                bill.TryGetValue(s.Material, out var n);
                bill[s.Material] = n + 1;
            }

            return bill;
        }

        // Render the bed. filled == null → the whole bed; otherwise filled(material)
        // is how many pieces of that material have been delivered, and only those
        // (the first N slots of each material, in order) are placed — the rest of
        // the bed is not there yet. That is the progressive build-site look.
        public static GameObject Build(string definitionId, System.Func<string, int>? filled = null)
        {
            var root = new GameObject(filled == null ? $"Bed {definitionId}" : $"Bed-site {definitionId}");
            var placed = new Dictionary<string, int>();
            foreach (var s in Slots(definitionId))
            {
                if (filled != null)
                {
                    placed.TryGetValue(s.Material, out var used);
                    if (used >= filled(s.Material))
                    {
                        continue; // this piece hasn't been delivered yet
                    }

                    placed[s.Material] = used + 1;
                }

                var prefab = Prefab(s.Material);
                if (prefab != null)
                {
                    Piece(root, prefab, s.TargetLen, s.Pos, s.Euler);
                }
            }

            // Sleep anchor (only meaningful on a finished bed).
            var r = HexLive.UnityPresentation.Spatial.SimulationUnityMapper.HexRadius;
            var liftR = definitionId == "bed.basic" ? BasicSleepLiftR : LeafSleepLiftR;
            var point = new GameObject("point");
            point.transform.SetParent(root.transform, false);
            point.transform.localPosition = new Vector3(0f, r * liftR, 0f);

            return root;
        }

        // Premium bedroll: 2 log side-rails + 4 stick slats + 7 leaf mattress + 1 rope.
        private static List<Slot> BedrollSlots(float r)
        {
            var s = new List<Slot>();
            var length = r * 1.05f;
            var width = r * 0.5f;
            var railR = length * 0.11f;
            var deck = railR * 2f;

            s.Add(new Slot("resource.log", length, new Vector3(-width * 0.5f, railR, 0f), new Vector3(0f, 90f, 0f)));
            s.Add(new Slot("resource.log", length, new Vector3(width * 0.5f, railR, 0f), new Vector3(0f, 90f, 0f)));

            const int slats = 4;
            for (var i = 0; i < slats; i++)
            {
                var t = slats == 1 ? 0.5f : i / (float)(slats - 1);
                var z = Mathf.Lerp(-length * 0.42f, length * 0.42f, t);
                s.Add(new Slot("resource.stick", width * 1.15f, new Vector3(0f, deck, z), Vector3.zero));
            }

            const int pad = 7;
            for (var i = 0; i < pad; i++)
            {
                var t = pad == 1 ? 0.5f : i / (float)(pad - 1);
                var z = Mathf.Lerp(-length * 0.34f, length * 0.34f, t);
                var yaw = (i % 2 == 0 ? 90f : 92f) + (t - 0.5f) * 8f;
                s.Add(new Slot("resource.palm_leaf", width * 1.25f, new Vector3(0f, deck + railR * (0.4f + i * 0.03f), z), new Vector3(0f, yaw, 0f)));
            }

            s.Add(new Slot("resource.rope", width * 0.4f, new Vector3(width * 0.5f, deck + railR * 0.4f, length * 0.46f), Vector3.zero));
            return s;
        }

        // Cheap mat: 8 leaf blades + 2 stick edge-rails.
        private static List<Slot> LeafMatSlots(float r)
        {
            var s = new List<Slot>();
            var length = r * 0.95f;
            var width = r * 0.5f;

            const int blades = 8;
            for (var i = 0; i < blades; i++)
            {
                var t = blades == 1 ? 0.5f : i / (float)(blades - 1);
                var x = Mathf.Lerp(-width * 0.4f, width * 0.4f, t);
                var yaw = (t - 0.5f) * 16f;
                s.Add(new Slot("resource.palm_leaf", length, new Vector3(x, 0.02f + i * 0.005f, 0f), new Vector3(0f, yaw, 0f)));
            }

            s.Add(new Slot("resource.stick", width * 1.1f, new Vector3(0f, 0.03f, -length * 0.42f), Vector3.zero));
            s.Add(new Slot("resource.stick", width * 1.1f, new Vector3(0f, 0.03f, length * 0.42f), Vector3.zero));
            return s;
        }

        private static void Piece(GameObject root, GameObject prefab, float targetLen, Vector3 pos, Vector3 euler)
        {
            var go = Object.Instantiate(prefab, root.transform);
            go.name = prefab.name;
            if (ObjectFit.WorldBounds(go, out var b))
            {
                var maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (maxDim > 0.0001f)
                {
                    go.transform.localScale *= targetLen / maxDim;
                }
            }

            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localPosition = pos;
        }
    }
}
