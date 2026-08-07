#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §54.14 (r2): meat hanging on the campfire's roasting spit. The sim keeps
    /// the hanging chunks in the fire's Contents (raw roasting → cooked waiting
    /// to be taken); this view threads the meat pieces onto the crossbar of the
    /// campfire_final prefab — one per fixed skewer slot, chunk centred ON the
    /// bar like a skewered kebab. Rebuilt only when the raw/cooked mix changes,
    /// so it's cheap to poke every frame.
    /// </summary>
    public sealed class CampfireSpitMeat : MonoBehaviour
    {
        // §54.14 (r4): SIX fixed skewer slots on the clear middle 53.3% of the
        // actual logical stick_bar half-span. The old view used absolute
        // campfire_final coordinates; the native build-safe FBX is allowed to
        // carry a different root/scale, so those points silently left the spit.
        // Resolve the bar's rendered bounds instead and keep the same relative
        // spacing. This also makes the primitive emergency assembly obey the
        // exact same attachment contract.
        private static readonly float[] SlotAlongBar =
            { -0.533333f, -0.32f, -0.106667f, 0.106667f, 0.32f, 0.533333f };
        private static readonly int[] FillOrder = { 2, 3, 1, 4, 0, 5 };

        private int _signature = -1;
        private Transform? _meatRoot;

        public void Refresh(int raw, int cooked)
        {
            var signature = raw * 31 + cooked;
            if (signature == _signature)
            {
                return;
            }

            _signature = signature;
            // Own container: this component shares the campfire_final root with
            // BedAssembly's staged pieces — clearing the whole root would wipe
            // the fire itself (and did: MissingReferenceException render stall).
            if (_meatRoot == null)
            {
                _meatRoot = new GameObject("SpitMeat").transform;
                _meatRoot.SetParent(transform, false);
            }

            for (var i = _meatRoot.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(_meatRoot.GetChild(i).gameObject);
            }

            var total = raw + cooked;
            for (var i = 0; i < total; i++)
            {
                var id = i < raw ? "food.meat_raw" : "food.meat_cooked";
                var piece = LoadMeatPiece(id);
                if (piece == null)
                {
                    continue;
                }

                piece.name = "Spit " + id;
                // Same physical size as the chunk on the ground / in the hand
                // (the shared ObjectFit table; the campfire renders 1:1).
                piece.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                piece.transform.localScale *= ObjectFit.FitScaleFactor(piece, id);

                // Fixed slot, centre-out; a chunk beyond the slots (capacity
                // tuned past 6) doubles up a hair behind the bar. Lying flat,
                // each slot at its own yaw so the bones fan out along the row.
                var slot = FillOrder[i % SlotAlongBar.Length];
                var overflow = i >= SlotAlongBar.Length ? 0.06f : 0f;
                var pose = Quaternion.Euler(0f, 90f + slot * 25f, 0f);
                piece.transform.rotation = pose;
                // The prefab pivot is off-centre (shifted toward the bone), so
                // centre the MESH on the skewer point: measure the rotated
                // bounds at the origin and cancel the pivot offset.
                var pivotShift = ObjectFit.WorldBounds(piece, out var b)
                    ? b.center : Vector3.zero;

                piece.transform.SetParent(_meatRoot, false);
                piece.transform.localRotation = pose;
                piece.transform.localPosition = SpitSlot(slot, overflow) - pivotShift;
            }
        }

        private Vector3 SpitSlot(int slot, float overflow)
        {
            var bar = FindLogicalPiece(transform, "stick_bar");
            if (bar == null || !TryLocalBounds(bar, transform, out var bounds))
            {
                // Legacy authored dimensions remain a safe last resort for an
                // incomplete custom prefab; the build-safe fallback has a real
                // stick_bar and normally never takes this branch.
                return new Vector3(SlotAlongBar[slot] * 0.75f, 0.673f, overflow);
            }

            var alongX = bounds.size.x >= bounds.size.z;
            var halfSpan = (alongX ? bounds.extents.x : bounds.extents.z);
            var point = bounds.center;
            if (alongX) point.x += SlotAlongBar[slot] * halfSpan;
            else point.z += SlotAlongBar[slot] * halfSpan;
            // Stack only overflow pieces behind the bar, never along it.
            if (alongX) point.z += overflow;
            else point.x += overflow;
            return point;
        }

        private static Transform? FindLogicalPiece(Transform root, string name)
        {
            foreach (Transform child in root)
            {
                if (child.name == name) return child;
                var nested = FindLogicalPiece(child, name);
                if (nested != null) return nested;
            }
            return null;
        }

        private static bool TryLocalBounds(Transform piece, Transform root, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var renderer in piece.GetComponentsInChildren<Renderer>(true))
            {
                var world = renderer.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var worldPoint = world.center + Vector3.Scale(world.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                    var localPoint = root.InverseTransformPoint(worldPoint);
                    if (!found)
                    {
                        bounds = new Bounds(localPoint, Vector3.zero);
                        found = true;
                    }
                    else bounds.Encapsulate(localPoint);
                }
            }
            return found;
        }

        // §54.14 (r3): the hanging chunk uses the SAME modelled prefab as the
        // ground drop (Resources/HexLive/Objects/<id>) when present, so the spit
        // shows real meat; falls back to the procedural chunk otherwise.
        private static GameObject? LoadMeatPiece(string id)
        {
            var prefab = WorldPropResources.Load(id);
            if (prefab != null)
            {
                return Object.Instantiate(prefab);
            }

            return LowPolyToolFactory.Build(id);
        }
    }
}
