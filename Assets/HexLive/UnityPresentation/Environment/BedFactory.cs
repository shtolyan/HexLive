#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: build the two beds from the game's own primitive props — logs,
    /// sticks, leaves and rope. Each bed is defined as an ordered list of SLOTS
    /// (one primitive piece + exactly where it goes). The slot tables below are
    /// BAKED from the hand-authored prefabs the user arranged
    /// (Resources/HexLive/Objects/"bed.leaf (leaf mat)" and "bed.basic (premium
    /// bedroll)") — literal localPosition / localEuler / localScale per piece, so
    /// the runtime bed reproduces the prefab 1:1. From that one list we render the
    /// finished bed, a PARTIALLY built bed (only the delivered pieces, each already
    /// in its final spot — the progressive build-site), and the exact material bill.
    /// Built at absolute size, so the caller must NOT run FitObjectPrefab on it.
    /// To re-capture after editing a bed prefab: scratchpad/emit_slots.py.
    /// </summary>
    public static class BedFactory
    {
        // Sleeper body height above the ground, per bed, in world units (the "point"
        // child; the sim reads its Y as the laying surface). Matches the prefabs.
        private const float BasicSleepY = 0.5400f;
        private const float LeafSleepY = 0.0615f;

        // One piece of a bed: which resource it is, and its literal local transform
        // (uniform scale) relative to the bed root — copied from the prefab.
        public readonly struct Slot
        {
            public readonly string Material; // resource id (resource.log / .stick / .palm_leaf / .rope)
            public readonly Vector3 Pos;
            public readonly Vector3 Euler;
            public readonly float Scale;

            public Slot(string material, Vector3 pos, Vector3 euler, float scale)
            {
                Material = material;
                Pos = pos;
                Euler = euler;
                Scale = scale;
            }
        }

        public static bool IsBed(string definitionId) =>
            definitionId == "bed.basic" || definitionId == "bed.leaf";

        // Map a bill resource id to the prefab that renders it (leaf → frond).
        private static GameObject? Prefab(string material) => Resources.Load<GameObject>(
            material == "resource.palm_leaf"
                ? "HexLive/Objects/palm_frond"
                : $"HexLive/Objects/{material}");

        // The ordered piece list for a bed. Order = the order pieces are laid down
        // (frame first, then slats, then leaf mattress, then rope) — drives the
        // progressive build-site reveal.
        public static List<Slot> Slots(string definitionId) =>
            definitionId == "bed.basic" ? BedrollSlots() : LeafMatSlots();

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
                    Piece(root, prefab, s);
                }
            }

            // Sleep anchor (only meaningful on a finished bed).
            var point = new GameObject("point");
            point.transform.SetParent(root.transform, false);
            point.transform.localPosition = new Vector3(0f, definitionId == "bed.basic" ? BasicSleepY : LeafSleepY, 0f);

            return root;
        }

        // Premium bedroll: 2 log side-rails + 4 stick slats + 20 leaf mattress + 1 rope.
        // Baked from "bed.basic (premium bedroll).prefab".
        private static List<Slot> BedrollSlots()
        {
            var s = new List<Slot>(27);
            s.Add(new Slot("resource.log", new Vector3(-0.3750f, 0.1732f, 0.0000f), new Vector3(0f, 90.00f, 0f), 1.5719f));
            s.Add(new Slot("resource.log", new Vector3(0.3750f, 0.1732f, 0.0000f), new Vector3(0f, 90.00f, 0f), 1.5719f));
            s.Add(new Slot("resource.stick", new Vector3(0.0000f, 0.3465f, -0.6615f), new Vector3(0f, 0.00f, 0f), 0.8608f));
            s.Add(new Slot("resource.stick", new Vector3(0.0000f, 0.3465f, -0.2205f), new Vector3(0f, 0.00f, 0f), 0.8608f));
            s.Add(new Slot("resource.stick", new Vector3(0.0000f, 0.3465f, 0.2205f), new Vector3(0f, 0.00f, 0f), 0.8608f));
            s.Add(new Slot("resource.stick", new Vector3(0.0000f, 0.3465f, 0.6615f), new Vector3(0f, 0.00f, 0f), 0.8608f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.2460f, 0.5370f, -0.6740f), new Vector3(0f, 349.78f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5380f, 0.5000f, -0.5840f), new Vector3(0f, 86.00f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.4990f, -0.5760f), new Vector3(0f, 261.93f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5380f, 0.5052f, -0.4055f), new Vector3(0f, 89.33f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.5042f, -0.3975f), new Vector3(0f, 265.26f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5380f, 0.5104f, -0.2270f), new Vector3(0f, 88.67f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.2440f, 0.5370f, -0.2220f), new Vector3(0f, 349.78f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.5094f, -0.2190f), new Vector3(0f, 264.60f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5380f, 0.5156f, -0.0485f), new Vector3(0f, 92.00f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.5146f, -0.0405f), new Vector3(0f, 267.93f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.2060f, 0.5370f, 0.0350f), new Vector3(0f, 189.28f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.4150f, 0.5370f, 0.0840f), new Vector3(0f, 179.47f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5380f, 0.5208f, 0.1300f), new Vector3(0f, 91.33f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.5198f, 0.1380f), new Vector3(0f, 267.26f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5380f, 0.5260f, 0.3085f), new Vector3(0f, 94.67f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.5250f, 0.3165f), new Vector3(0f, 270.60f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.6120f, 0.5302f, 0.4950f), new Vector3(0f, 269.93f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.5270f, 0.5180f, 0.4980f), new Vector3(0f, 94.00f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.4040f, 0.5370f, 0.5100f), new Vector3(0f, 179.47f, 0f), 0.9357f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.4000f, 0.5370f, 0.9430f), new Vector3(0f, 179.47f, 0f), 0.9357f));
            s.Add(new Slot("resource.rope", new Vector3(0.3750f, 0.4158f, 0.7245f), new Vector3(0f, 0.00f, 0f), 0.3004f));
            return s;
        }

        // Leaf mat: 16 leaf blades (8 large + 8 small) + 6 stick rails.
        // Baked from "bed.leaf (leaf mat).prefab".
        private static List<Slot> LeafMatSlots()
        {
            var s = new List<Slot>(22);
            s.Add(new Slot("resource.stick", new Vector3(0.0000f, 0.0300f, -0.5985f), new Vector3(0f, 0.00f, 0f), 0.8234f));
            s.Add(new Slot("resource.stick", new Vector3(-0.3420f, 0.0300f, -0.3360f), new Vector3(0f, 90.00f, 0f), 0.8234f));
            s.Add(new Slot("resource.stick", new Vector3(0.3620f, 0.0300f, -0.2910f), new Vector3(0f, 90.00f, 0f), 0.8234f));
            s.Add(new Slot("resource.stick", new Vector3(0.3560f, 0.0300f, 0.2890f), new Vector3(0f, 90.00f, 0f), 0.8234f));
            s.Add(new Slot("resource.stick", new Vector3(-0.3470f, 0.0300f, 0.4000f), new Vector3(0f, 90.00f, 0f), 0.8234f));
            s.Add(new Slot("resource.stick", new Vector3(0.0000f, 0.0300f, 0.5985f), new Vector3(0f, 0.00f, 0f), 0.8234f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.1170f, 0.0440f, -0.6580f), new Vector3(0f, 355.61f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.0280f, 0.0640f, -0.6470f), new Vector3(0f, 7.55f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.0970f, 0.0590f, -0.5280f), new Vector3(0f, 360.00f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.1820f, 0.0490f, -0.3860f), new Vector3(0f, 357.89f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.3240f, 0.0390f, -0.3370f), new Vector3(0f, 355.38f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.2160f, 0.0690f, -0.3230f), new Vector3(0f, 7.04f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.2680f, 0.0740f, -0.2400f), new Vector3(0f, 9.32f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.0020f, 0.0540f, -0.1140f), new Vector3(0f, 0.18f, 0f), 1.4223f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.0070f, 0.0590f, 0.1310f), new Vector3(0f, 174.73f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.2430f, 0.0740f, 0.1480f), new Vector3(0f, 184.05f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.4150f, 0.0770f, 0.1530f), new Vector3(0f, 170.12f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.1650f, 0.0490f, 0.1760f), new Vector3(0f, 172.62f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.3490f, 0.0840f, 0.2110f), new Vector3(0f, 181.77f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.3000f, 0.0840f, 0.2370f), new Vector3(0f, 170.34f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(0.0550f, 0.0640f, 0.2460f), new Vector3(0f, 182.29f, 0f), 1.0000f));
            s.Add(new Slot("resource.palm_leaf", new Vector3(-0.0520f, 0.0540f, 0.2920f), new Vector3(0f, 174.91f, 0f), 1.0000f));
            return s;
        }

        private static void Piece(GameObject root, GameObject prefab, Slot s)
        {
            var go = Object.Instantiate(prefab, root.transform);
            go.name = prefab.name;
            go.transform.localRotation = Quaternion.Euler(s.Euler);
            go.transform.localScale = Vector3.one * s.Scale;
            go.transform.localPosition = s.Pos;
        }
    }
}
