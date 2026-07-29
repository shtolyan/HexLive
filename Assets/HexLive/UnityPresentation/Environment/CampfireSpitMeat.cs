#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §54.14 (r2): meat hanging on the campfire's roasting spit. The sim keeps
    /// the hanging chunks in the fire's Contents (raw roasting → cooked waiting
    /// to be taken); this view strings the low-poly meat pieces under the
    /// crossbar of the campfire_final prefab. Rebuilt only when the raw/cooked
    /// mix changes, so it's cheap to poke every frame.
    /// </summary>
    public sealed class CampfireSpitMeat : MonoBehaviour
    {
        // campfire_final: stick_bar sits at local (0, 0.67, 0) spanning ±0.62 in X.
        private const float BarY = 0.67f;
        private const float BarHalfSpan = 0.38f;
        // Every hanging chunk is normalized to this authored (campfire-local) max
        // dimension, so a crude procedural chunk and a modelled prefab chunk hang
        // at the same size on the crossbar.
        private const float MeatSpitSize = 0.46f;

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
                // Normalize the chunk to a consistent hanging size (prefab meat is
                // authored larger than the old procedural cube).
                var scale = NormalizeScale(piece, MeatSpitSize);
                piece.transform.SetParent(_meatRoot, false);
                // Even spread along the crossbar, hanging just beneath it.
                var t = total == 1 ? 0.5f : i / (float)(total - 1);
                piece.transform.localScale = Vector3.one * scale;
                piece.transform.localPosition = new Vector3(
                    Mathf.Lerp(-BarHalfSpan, BarHalfSpan, t), BarY - 0.22f, 0f);
                piece.transform.localRotation = Quaternion.Euler(0f, 90f + i * 25f, 0f);
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

        // Uniform scale so the piece's authored max dimension equals target.
        // Measured before parenting (piece at world origin, unit scale), so the
        // renderer bounds read as the authored local size.
        private static float NormalizeScale(GameObject piece, float target)
        {
            var rs = piece.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0)
            {
                return 1f;
            }

            var b = rs[0].bounds;
            for (var i = 1; i < rs.Length; i++)
            {
                b.Encapsulate(rs[i].bounds);
            }

            var maxDim = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            return maxDim > 0.0001f ? target / maxDim : 1f;
        }
    }
}
