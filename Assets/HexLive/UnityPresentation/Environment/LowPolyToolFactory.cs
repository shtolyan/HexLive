#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec 20.16: procedural low-poly models for tools, resources and food, so
    /// the simulation's items (axe, pickaxe, bow, arrow, spear, pot, saw, …) are
    /// visible both on the ground and in an NPC's hand — no external assets, no
    /// prefab wiring. Built from cubes / a pyramid / a small prism, flat-shaded
    /// to match the terrain style. Returns null for ids it doesn't model, so the
    /// caller falls back to its generic primitive.
    /// </summary>
    public static class LowPolyToolFactory
    {
        private static readonly Color Wood = new(0.45f, 0.30f, 0.16f);
        private static readonly Color DarkWood = new(0.34f, 0.22f, 0.12f);
        private static readonly Color Stone = new(0.50f, 0.50f, 0.53f);
        private static readonly Color Metal = new(0.60f, 0.62f, 0.66f);
        private static readonly Color StringCol = new(0.85f, 0.82f, 0.70f);
        private static readonly Color Leaf = new(0.30f, 0.55f, 0.24f);
        private static readonly Color Hide = new(0.72f, 0.58f, 0.42f);
        private static readonly Color Coconut = new(0.40f, 0.26f, 0.15f);
        private static readonly Color MeatRaw = new(0.80f, 0.32f, 0.32f);
        private static readonly Color MeatCooked = new(0.52f, 0.33f, 0.20f);
        private static readonly Color Rock = new(0.50f, 0.48f, 0.46f);
        private static readonly Color Gauze = new(0.93f, 0.93f, 0.90f);
        private static readonly Color MedRed = new(0.86f, 0.20f, 0.22f);

        public static GameObject? Build(string definitionId)
        {
            var root = new GameObject($"LowPoly {definitionId}");
            switch (definitionId)
            {
                case "tool.axe_stone": BuildAxe(root.transform); break;
                case "tool.machete": BuildMachete(root.transform); break;
                case "tool.pickaxe_stone": BuildPickaxe(root.transform); break;
                case "tool.spear": BuildSpear(root.transform); break;
                case "tool.bow": BuildBow(root.transform); break;
                case "tool.pot": BuildPot(root.transform); break;
                case "tool.bottle": BuildBottle(root.transform); break;
                case "tool.saw": BuildSaw(root.transform); break;
                case "tool.lighter": BuildLighter(root.transform); break;
                case "resource.arrow": BuildArrow(root.transform); break;
                case "resource.log": BuildLog(root.transform); break;
                case "resource.stick": BuildStick(root.transform); break;
                case "resource.stone": BuildStone(root.transform); break;
                case "resource.hide": BuildHide(root.transform); break;
                case "resource.palm_leaf": BuildPalmLeaf(root.transform); break;
                case "food.coconut": AddBox(root.transform, new Vector3(0.42f, 0.42f, 0.42f), new Vector3(0f, 0.21f, 0f), Vector3.zero, Coconut); break;
                case "food.coconut_pierced":
                    AddBox(root.transform, new Vector3(0.42f, 0.42f, 0.42f), new Vector3(0f, 0.21f, 0f), Vector3.zero, Coconut);
                    AddBox(root.transform, new Vector3(0.08f, 0.03f, 0.08f), new Vector3(0f, 0.43f, 0f), Vector3.zero, MeatCooked);
                    break;
                // §55: the cracked-open coconut — the eaten meal. Was propless, so
                // eating it from the inventory showed NOTHING in hand. Brown shell
                // with a cream flesh top so it reads as an opened husk.
                case "food.coconut_open":
                    AddBox(root.transform, new Vector3(0.42f, 0.30f, 0.42f), new Vector3(0f, 0.15f, 0f), Vector3.zero, Coconut);
                    AddBox(root.transform, new Vector3(0.30f, 0.12f, 0.30f), new Vector3(0f, 0.32f, 0f), Vector3.zero, new Color(0.93f, 0.89f, 0.78f));
                    break;
                case "food.meat_raw": BuildMeat(root.transform, MeatRaw); break;
                case "food.meat_cooked": BuildMeat(root.transform, MeatCooked); break;
                case "bed.leaf": BuildLeafMat(root.transform); break;
                case "shelter.tent": BuildTent(root.transform); break;
                case "vessel.raft": BuildRaft(root.transform); break;
                case "herb.bush": BuildHerbBush(root.transform); break;
                case "resource.herb_leaf": BuildHerbLeaf(root.transform); break;
                case "plant.yucca": BuildFibrousPlant(root.transform); break;
                case "resource.fiber": BuildFiber(root.transform); break;
                case "resource.rope": BuildRope(root.transform); break;
                case "resource.cloth": BuildCloth(root.transform); break;
                case "tool.knife": BuildKnife(root.transform); break;
                case "carcass.animal": BuildCarcass(root.transform); break;
                case "item.bandage": BuildBandage(root.transform); break;
                default:
                    Object.Destroy(root);
                    return null;
            }

            return root;
        }

        private static void BuildAxe(Transform p)
        {
            AddBox(p, new Vector3(0.06f, 1.0f, 0.06f), new Vector3(0f, 0.5f, 0f), Vector3.zero, Wood);
            AddBox(p, new Vector3(0.30f, 0.24f, 0.08f), new Vector3(0.15f, 0.88f, 0f), new Vector3(0f, 0f, -18f), Stone);
            AddBox(p, new Vector3(0.12f, 0.16f, 0.08f), new Vector3(-0.06f, 0.9f, 0f), Vector3.zero, Stone);
        }

        // Bug #59: shipped machetes are glTF ScriptedImporter assets rather
        // than ordinary prefabs. Some player builds cannot resolve that main
        // asset through Resources.Load<GameObject>; unlike the axe/saw, the
        // machete then had no procedural fallback and simply vanished from the
        // acting hand. Keep the silhouette unmistakable: brown one-handed grip,
        // long iron blade and a short widened cutting tip.
        private static void BuildMachete(Transform p)
        {
            AddBox(p, new Vector3(0.075f, 0.42f, 0.065f),
                new Vector3(0f, 0.21f, 0f), Vector3.zero, DarkWood);
            AddBox(p, new Vector3(0.055f, 0.06f, 0.15f),
                new Vector3(0f, 0.43f, 0.015f), Vector3.zero, Metal);
            AddBox(p, new Vector3(0.075f, 0.78f, 0.035f),
                new Vector3(0f, 0.82f, 0.035f), new Vector3(0f, 0f, -2f), Metal);
            AddBox(p, new Vector3(0.12f, 0.20f, 0.035f),
                new Vector3(0.02f, 1.27f, 0.035f), new Vector3(0f, 0f, -8f), Metal);
        }

        private static void BuildPickaxe(Transform p)
        {
            AddBox(p, new Vector3(0.06f, 1.0f, 0.06f), new Vector3(0f, 0.5f, 0f), Vector3.zero, Wood);
            AddBox(p, new Vector3(0.7f, 0.06f, 0.06f), new Vector3(0f, 0.92f, 0f), new Vector3(0f, 0f, 6f), Stone);
            AddPyramid(p, 0.10f, 0.16f, new Vector3(0.42f, 0.94f, 0f), new Vector3(0f, 0f, -96f), Stone);
            AddPyramid(p, 0.10f, 0.16f, new Vector3(-0.42f, 0.90f, 0f), new Vector3(0f, 0f, 96f), Stone);
        }

        private static void BuildBandage(Transform p)
        {
            // A small rolled gauze pad with a red medical cross on the top face.
            AddBox(p, new Vector3(0.30f, 0.12f, 0.22f), new Vector3(0f, 0.06f, 0f), Vector3.zero, Gauze);
            AddBox(p, new Vector3(0.16f, 0.02f, 0.05f), new Vector3(0f, 0.125f, 0f), Vector3.zero, MedRed);
            AddBox(p, new Vector3(0.05f, 0.02f, 0.14f), new Vector3(0f, 0.125f, 0f), Vector3.zero, MedRed);
        }

        private static void BuildSpear(Transform p)
        {
            AddBox(p, new Vector3(0.05f, 1.3f, 0.05f), new Vector3(0f, 0.65f, 0f), Vector3.zero, DarkWood);
            AddPyramid(p, 0.12f, 0.24f, new Vector3(0f, 1.3f, 0f), Vector3.zero, Stone);
        }

        private static void BuildBow(Transform p)
        {
            // A shallow arc of segments in the XY plane + a straight string.
            const int segs = 5;
            for (var i = 0; i < segs; i++)
            {
                var t = (i / (float)(segs - 1)) * 2f - 1f; // -1..1
                var y = 0.5f + t * 0.45f;
                var x = -0.22f * (1f - t * t); // parabola bulging -x
                var roll = t * 55f;
                AddBox(p, new Vector3(0.05f, 0.28f, 0.05f), new Vector3(x, y, 0f), new Vector3(0f, 0f, roll), Wood);
            }

            AddBox(p, new Vector3(0.015f, 0.9f, 0.015f), new Vector3(0.0f, 0.5f, 0f), Vector3.zero, StringCol);
        }

        private static void BuildArrow(Transform p)
        {
            AddBox(p, new Vector3(0.02f, 0.7f, 0.02f), new Vector3(0f, 0.35f, 0f), Vector3.zero, Wood);
            AddPyramid(p, 0.05f, 0.10f, new Vector3(0f, 0.7f, 0f), Vector3.zero, Stone);
            AddBox(p, new Vector3(0.09f, 0.14f, 0.006f), new Vector3(0f, 0.06f, 0f), new Vector3(0f, 0f, 30f), StringCol);
            AddBox(p, new Vector3(0.09f, 0.14f, 0.006f), new Vector3(0f, 0.06f, 0f), new Vector3(0f, 90f, 30f), StringCol);
        }

        private static void BuildPot(Transform p)
        {
            var body = new GameObject("body");
            body.transform.SetParent(p, false);
            body.AddComponent<MeshFilter>().sharedMesh = PrismMesh(8, 0.35f, 0.42f);
            body.AddComponent<MeshRenderer>().sharedMaterial = FlatMat(new Color(0.28f, 0.28f, 0.30f));

            var rim = new GameObject("rim");
            rim.transform.SetParent(p, false);
            rim.transform.localPosition = new Vector3(0f, 0.40f, 0f);
            rim.AddComponent<MeshFilter>().sharedMesh = PrismMesh(8, 0.39f, 0.07f);
            rim.AddComponent<MeshRenderer>().sharedMaterial = FlatMat(Metal);
        }

        private static void BuildBottle(Transform p)
        {
            // Spec 29H: a small translucent-blue bottle with a cork.
            var glass = new Color(0.40f, 0.62f, 0.78f);
            var body = new GameObject("body");
            body.transform.SetParent(p, false);
            body.AddComponent<MeshFilter>().sharedMesh = PrismMesh(8, 0.12f, 0.34f);
            body.AddComponent<MeshRenderer>().sharedMaterial = FlatMat(glass);
            AddBox(p, new Vector3(0.10f, 0.10f, 0.10f), new Vector3(0f, 0.38f, 0f), Vector3.zero, DarkWood);
        }

        private static void BuildSaw(Transform p)
        {
            AddBox(p, new Vector3(0.18f, 0.30f, 0.06f), new Vector3(-0.5f, 0.1f, 0f), Vector3.zero, DarkWood);
            AddBox(p, new Vector3(0.9f, 0.16f, 0.02f), new Vector3(0.1f, 0.12f, 0f), Vector3.zero, Metal);
        }

        private static void BuildLighter(Transform p)
        {
            AddBox(p, new Vector3(0.12f, 0.22f, 0.08f), new Vector3(0f, 0.11f, 0f), Vector3.zero, DarkWood);
            AddBox(p, new Vector3(0.06f, 0.05f, 0.06f), new Vector3(0f, 0.24f, 0f), Vector3.zero, Metal);
        }

        // Spec §54: a single fat log (the chop output). One thick round-ish
        // trunk lying on its side, split into two tones for a low-poly bark read.
        private static void BuildLog(Transform p)
        {
            AddBox(p, new Vector3(0.24f, 0.22f, 1.0f), new Vector3(0f, 0.12f, 0f), new Vector3(0f, 3f, 0f), Wood);
            AddBox(p, new Vector3(0.18f, 0.10f, 1.0f), new Vector3(0.02f, 0.24f, 0f), new Vector3(0f, -2f, 0f), DarkWood);
            // pale cut faces at each end
            AddBox(p, new Vector3(0.22f, 0.20f, 0.04f), new Vector3(0f, 0.12f, 0.5f), Vector3.zero, StringCol);
            AddBox(p, new Vector3(0.22f, 0.20f, 0.04f), new Vector3(0f, 0.12f, -0.5f), Vector3.zero, StringCol);
        }

        // Spec §54: a thin stick (fuel / craft currency) — a slim short rod.
        private static void BuildStick(Transform p)
        {
            AddBox(p, new Vector3(0.05f, 0.05f, 0.55f), new Vector3(0f, 0.03f, 0f), new Vector3(0f, 6f, 2f), Wood);
            AddBox(p, new Vector3(0.045f, 0.045f, 0.4f), new Vector3(0.05f, 0.03f, 0.05f), new Vector3(0f, -18f, 0f), DarkWood);
        }

        private static void BuildStone(Transform p)
        {
            AddBox(p, new Vector3(0.5f, 0.4f, 0.45f), new Vector3(0f, 0.2f, 0f), new Vector3(12f, 20f, 8f), Rock);
        }

        private static void BuildHide(Transform p)
        {
            AddBox(p, new Vector3(0.7f, 0.02f, 0.5f), new Vector3(0f, 0.01f, 0f), new Vector3(0f, 12f, 0f), Hide);
        }

        private static void BuildPalmLeaf(Transform p)
        {
            AddBox(p, new Vector3(0.10f, 0.02f, 0.6f), new Vector3(0f, 0.02f, 0.25f), new Vector3(-8f, 0f, 0f), Leaf);
            AddBox(p, new Vector3(0.10f, 0.02f, 0.5f), new Vector3(0.06f, 0.03f, 0.15f), new Vector3(-8f, 40f, 0f), Leaf);
            AddBox(p, new Vector3(0.10f, 0.02f, 0.5f), new Vector3(-0.06f, 0.03f, 0.15f), new Vector3(-8f, -40f, 0f), Leaf);
        }

        // Spec 44: the healing herb bush — a small leafy medicinal shrub with
        // pale flower tips so it reads as "special" among the greenery.
        private static void BuildHerbBush(Transform p)
        {
            var herb = new Color(0.33f, 0.60f, 0.30f);
            var herbDark = new Color(0.24f, 0.46f, 0.22f);
            var bloom = new Color(0.95f, 0.93f, 0.78f);

            // Splayed stems with a leaf blade at each tip.
            for (var i = 0; i < 5; i++)
            {
                var yaw = i * 72f;
                var lean = 22f + (i % 2) * 10f;
                var rot = Quaternion.Euler(0f, yaw, lean);
                var dir = rot * Vector3.up;
                var stemLen = 0.42f + (i % 3) * 0.07f;
                AddBox(p, new Vector3(0.035f, stemLen, 0.035f),
                    dir * (stemLen * 0.5f), new Vector3(0f, yaw, lean),
                    i % 2 == 0 ? herb : herbDark);
                AddBox(p, new Vector3(0.16f, 0.02f, 0.28f),
                    dir * stemLen + new Vector3(0f, 0.02f, 0f), new Vector3(12f, yaw, 0f), herb);
                // Flower tips on three of the stems.
                if (i % 2 == 0)
                {
                    AddBox(p, new Vector3(0.07f, 0.06f, 0.07f),
                        dir * (stemLen + 0.05f), new Vector3(0f, yaw + 45f, 0f), bloom);
                }
            }

            // Low leafy base clump.
            AddBox(p, new Vector3(0.34f, 0.14f, 0.34f), new Vector3(0f, 0.07f, 0f), new Vector3(0f, 20f, 0f), herbDark);
        }

        // Spec 44: a single picked herb leaf (ground/hand pickup).
        private static void BuildHerbLeaf(Transform p)
        {
            var herb = new Color(0.36f, 0.62f, 0.32f);
            AddBox(p, new Vector3(0.16f, 0.015f, 0.30f), new Vector3(0f, 0.02f, 0.1f), new Vector3(-6f, 0f, 0f), herb);
            AddBox(p, new Vector3(0.02f, 0.015f, 0.14f), new Vector3(0f, 0.015f, -0.1f), Vector3.zero,
                new Color(0.30f, 0.45f, 0.22f));
        }

        // Spec 40.14: a woven leaf sleeping mat — a thin flat pad of leaves.
        private static void BuildLeafMat(Transform p)
        {
            AddBox(p, new Vector3(1.1f, 0.06f, 0.6f), new Vector3(0f, 0.03f, 0f), Vector3.zero, Leaf);
            AddBox(p, new Vector3(1.0f, 0.02f, 0.5f), new Vector3(0f, 0.08f, 0f), new Vector3(0f, 6f, 0f),
                new Color(0.26f, 0.48f, 0.20f));
        }

        // Spec 40.14: a lean-to sun canopy — two leaf panels over a ridge pole
        // on stick legs (an angled roof shading one hex).
        private static void BuildTent(Transform p)
        {
            AddBox(p, new Vector3(0.06f, 0.9f, 0.06f), new Vector3(-0.5f, 0.45f, 0f), Vector3.zero, DarkWood);
            AddBox(p, new Vector3(0.06f, 0.9f, 0.06f), new Vector3(0.5f, 0.45f, 0f), Vector3.zero, DarkWood);
            AddBox(p, new Vector3(1.1f, 0.05f, 0.06f), new Vector3(0f, 0.9f, 0f), Vector3.zero, DarkWood); // ridge
            AddBox(p, new Vector3(0.7f, 0.04f, 1.0f), new Vector3(-0.28f, 0.62f, 0f), new Vector3(0f, 0f, 42f), Leaf);
            AddBox(p, new Vector3(0.7f, 0.04f, 1.0f), new Vector3(0.28f, 0.62f, 0f), new Vector3(0f, 0f, -42f), Leaf);
        }

        // Spec 40.15: the escape raft — a platform of lashed logs with a little
        // mast, sitting at the waterline.
        private static void BuildRaft(Transform p)
        {
            for (var i = 0; i < 5; i++)
            {
                var z = -0.5f + i * 0.25f;
                AddBox(p, new Vector3(1.3f, 0.12f, 0.2f), new Vector3(0f, 0.06f, z), new Vector3(0f, (i % 2 == 0 ? 2f : -2f), 0f),
                    i % 2 == 0 ? Wood : DarkWood);
            }

            AddBox(p, new Vector3(0.12f, 0.06f, 1.2f), new Vector3(0f, 0.15f, 0f), Vector3.zero, DarkWood); // cross-lash
            AddBox(p, new Vector3(0.05f, 0.8f, 0.05f), new Vector3(0.4f, 0.5f, 0.3f), Vector3.zero, Wood);   // mast
            AddBox(p, new Vector3(0.02f, 0.5f, 0.4f), new Vector3(0.4f, 0.55f, 0.5f), Vector3.zero, StringCol); // sail
        }

        private static void BuildMeat(Transform p, Color color)
        {
            AddBox(p, new Vector3(0.42f, 0.16f, 0.30f), new Vector3(0f, 0.08f, 0f), new Vector3(0f, 15f, 0f), color);
            AddBox(p, new Vector3(0.05f, 0.05f, 0.22f), new Vector3(0f, 0.08f, 0.24f), Vector3.zero, DarkWood);
        }

        // Spec §54: the cordage plant — a tuft of tall pale-green blades fanning up.
        private static void BuildFibrousPlant(Transform p)
        {
            var fiberGreen = new Color(0.54f, 0.62f, 0.34f);
            var fiberDry = new Color(0.68f, 0.64f, 0.42f);
            for (var i = 0; i < 6; i++)
            {
                var yaw = i * 60f;
                var lean = 10f + (i % 3) * 6f;
                AddBox(p, new Vector3(0.05f, 0.7f, 0.02f), new Vector3(0f, 0.35f, 0f),
                    new Vector3(lean, yaw, 0f), i % 2 == 0 ? fiberGreen : fiberDry);
            }
        }

        // Spec §54: a little bundle of loose plant fiber.
        private static void BuildFiber(Transform p)
        {
            var fiberDry = new Color(0.72f, 0.66f, 0.44f);
            AddBox(p, new Vector3(0.05f, 0.03f, 0.5f), new Vector3(0f, 0.03f, 0f), new Vector3(0f, 4f, 0f), fiberDry);
            AddBox(p, new Vector3(0.05f, 0.03f, 0.44f), new Vector3(0.05f, 0.03f, 0.02f), new Vector3(0f, -12f, 0f), fiberDry);
            AddBox(p, new Vector3(0.05f, 0.03f, 0.4f), new Vector3(-0.05f, 0.03f, -0.02f), new Vector3(0f, 14f, 0f), fiberDry);
        }

        // Spec §54: a coil of rope — a short twisted cord looped over on itself.
        private static void BuildRope(Transform p)
        {
            var rope = new Color(0.78f, 0.68f, 0.44f);
            for (var i = 0; i < 3; i++)
            {
                AddBox(p, new Vector3(0.30f, 0.05f, 0.06f), new Vector3(0f, 0.04f + i * 0.05f, 0f),
                    new Vector3(0f, i * 12f, 0f), rope);
            }
        }

        // Spec §54: a folded bolt of cloth — a flat stacked square.
        private static void BuildCloth(Transform p)
        {
            var cloth = new Color(0.82f, 0.80f, 0.72f);
            var clothDark = new Color(0.70f, 0.68f, 0.60f);
            AddBox(p, new Vector3(0.5f, 0.06f, 0.4f), new Vector3(0f, 0.03f, 0f), new Vector3(0f, 6f, 0f), cloth);
            AddBox(p, new Vector3(0.44f, 0.05f, 0.34f), new Vector3(0.02f, 0.09f, 0f), new Vector3(0f, -4f, 0f), clothDark);
        }

        // Spec §54: a stone knife — a short handle with a chipped blade.
        private static void BuildKnife(Transform p)
        {
            AddBox(p, new Vector3(0.05f, 0.05f, 0.26f), new Vector3(0f, 0.03f, -0.14f), Vector3.zero, DarkWood);
            AddBox(p, new Vector3(0.02f, 0.10f, 0.34f), new Vector3(0f, 0.05f, 0.16f), new Vector3(0f, 0f, 0f), Stone);
        }

        // Spec §54: an animal carcass on the ground — a slumped hide-brown body
        // with a splayed leg, so it reads as "a downed beast to butcher".
        private static void BuildCarcass(Transform p)
        {
            AddBox(p, new Vector3(0.5f, 0.34f, 0.85f), new Vector3(0f, 0.17f, 0f), new Vector3(6f, 8f, 4f), Hide);
            AddBox(p, new Vector3(0.30f, 0.26f, 0.30f), new Vector3(0f, 0.20f, 0.5f), new Vector3(0f, 12f, 0f), Hide); // head
            AddBox(p, new Vector3(0.10f, 0.10f, 0.35f), new Vector3(0.22f, 0.06f, -0.3f), new Vector3(60f, 0f, 0f), Hide); // splayed leg
            AddBox(p, new Vector3(0.22f, 0.05f, 0.30f), new Vector3(-0.1f, 0.02f, -0.1f), new Vector3(0f, 20f, 0f), MeatRaw); // exposed flesh
        }

        // ---- primitives ----

        private static void AddBox(Transform parent, Vector3 size, Vector3 pos, Vector3 euler, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            go.transform.SetParent(parent, false);
            go.transform.localScale = size;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.GetComponent<MeshRenderer>().sharedMaterial = FlatMat(color);
        }

        private static void AddPyramid(Transform parent, float baseSize, float height, Vector3 pos, Vector3 euler, Color color)
        {
            var go = new GameObject("tip");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = new Vector3(baseSize, height, baseSize);
            go.AddComponent<MeshFilter>().sharedMesh = PyramidMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = FlatMat(color);
        }

        // ---- shared meshes & materials ----

        private static Mesh? _pyramidMesh;

        private static Mesh PyramidMesh()
        {
            if (_pyramidMesh != null)
            {
                return _pyramidMesh;
            }

            var mesh = new Mesh { name = "Pyramid" };
            var v = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0f, 1f, 0f)
            };
            var t = new[]
            {
                0, 4, 1, 1, 4, 2, 2, 4, 3, 3, 4, 0, // sides
                0, 1, 2, 0, 2, 3                       // base
            };
            mesh.SetVertices(new List<Vector3>(v));
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _pyramidMesh = mesh;
            return mesh;
        }

        private static readonly Dictionary<int, Mesh> _prismMeshes = new();

        private static Mesh PrismMesh(int sides, float radius, float height)
        {
            var key = sides * 100000 + Mathf.RoundToInt(radius * 100f) * 1000 + Mathf.RoundToInt(height * 100f);
            if (_prismMeshes.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            var mesh = new Mesh { name = "Prism" };
            var verts = new List<Vector3>();
            var tris = new List<int>();

            for (var i = 0; i < sides; i++)
            {
                var a = Mathf.PI * 2f * i / sides;
                var x = Mathf.Cos(a) * radius;
                var z = Mathf.Sin(a) * radius;
                verts.Add(new Vector3(x, 0f, z));
                verts.Add(new Vector3(x, height, z));
            }

            for (var i = 0; i < sides; i++)
            {
                var b0 = i * 2;
                var t0 = i * 2 + 1;
                var b1 = (i + 1) % sides * 2;
                var t1 = (i + 1) % sides * 2 + 1;
                tris.Add(b0); tris.Add(t0); tris.Add(t1);
                tris.Add(b0); tris.Add(t1); tris.Add(b1);
            }

            // caps
            var centerBottom = verts.Count;
            verts.Add(new Vector3(0f, 0f, 0f));
            var centerTop = verts.Count;
            verts.Add(new Vector3(0f, height, 0f));
            for (var i = 0; i < sides; i++)
            {
                var b0 = i * 2;
                var b1 = (i + 1) % sides * 2;
                tris.Add(centerBottom); tris.Add(b1); tris.Add(b0);
                var t0 = i * 2 + 1;
                var t1 = (i + 1) % sides * 2 + 1;
                tris.Add(centerTop); tris.Add(t0); tris.Add(t1);
            }

            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _prismMeshes[key] = mesh;
            return mesh;
        }

        private static readonly Dictionary<int, Material> _flatMaterials = new();

        private static Material FlatMat(Color color)
        {
            var key = (Mathf.RoundToInt(color.r * 32f) << 16)
                      | (Mathf.RoundToInt(color.g * 32f) << 8)
                      | Mathf.RoundToInt(color.b * 32f);
            if (_flatMaterials.TryGetValue(key, out var cached) && cached != null)
            {
                return cached;
            }

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            material.SetFloat("_Smoothness", 0.1f);
            _flatMaterials[key] = material;
            return material;
        }
    }
}
