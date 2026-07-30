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
        // campfire_final crossbar (stick_bar): centre (0, 0.67, 0), 1.5 long in
        // X, ~0.05 thick; the forked posts flank it at |x| ≥ 0.485 and the rope
        // lashings sit further out. All in campfire-local units — the prefab is
        // authored 1:1, so these are world sizes too.
        // Where a skewered chunk's CENTRE sits: threaded on the bar, riding a
        // hair high so the rod reads as passing through the lower half
        // (hand-tuned in the editor — the hanging-below variant read as
        // floating under the spit).
        private const float SkewerY = 0.673f;
        // §54.14 (r4): SIX fixed skewer slots on the clear span between the
        // forks (outermost meat edge stays inside |x| 0.485). Chunks fill from
        // the CENTRE outward so a lone piece roasts over the flame, not at a post.
        private static readonly float[] SlotX = { -0.40f, -0.24f, -0.08f, 0.08f, 0.24f, 0.40f };
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
                var slot = FillOrder[i % SlotX.Length];
                var overflow = i >= SlotX.Length ? 0.06f : 0f;
                var pose = Quaternion.Euler(0f, 90f + slot * 25f, 0f);
                piece.transform.rotation = pose;
                // The prefab pivot is off-centre (shifted toward the bone), so
                // centre the MESH on the skewer point: measure the rotated
                // bounds at the origin and cancel the pivot offset.
                var pivotShift = ObjectFit.WorldBounds(piece, out var b)
                    ? b.center : Vector3.zero;

                piece.transform.SetParent(_meatRoot, false);
                piece.transform.localRotation = pose;
                piece.transform.localPosition =
                    new Vector3(SlotX[slot], SkewerY, overflow) - pivotShift;
            }
        }

        // §54.14 (r3): the hanging chunk uses the SAME modelled prefab as the
        // ground drop (Resources/HexLive/Objects/<id>) when present, so the spit
        // shows real meat; falls back to the procedural chunk otherwise.
        private static GameObject? LoadMeatPiece(string id)
        {
            var prefab = Resources.Load<GameObject>($"HexLive/Objects/{id}");
            if (prefab != null)
            {
                return Object.Instantiate(prefab);
            }

            return LowPolyToolFactory.Build(id);
        }
    }
}
