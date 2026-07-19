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
        private const float MeatScale = 0.8f;

        private int _signature = -1;

        public void Refresh(int raw, int cooked)
        {
            var signature = raw * 31 + cooked;
            if (signature == _signature)
            {
                return;
            }

            _signature = signature;
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(transform.GetChild(i).gameObject);
            }

            var total = raw + cooked;
            for (var i = 0; i < total; i++)
            {
                var id = i < raw ? "food.meat_raw" : "food.meat_cooked";
                var piece = LowPolyToolFactory.Build(id);
                if (piece == null)
                {
                    continue;
                }

                piece.name = "Spit " + id;
                piece.transform.SetParent(transform, false);
                // Even spread along the crossbar, hanging just beneath it.
                var t = total == 1 ? 0.5f : i / (float)(total - 1);
                piece.transform.localScale = Vector3.one * MeatScale;
                piece.transform.localPosition = new Vector3(
                    Mathf.Lerp(-BarHalfSpan, BarHalfSpan, t), BarY - 0.22f, 0f);
                piece.transform.localRotation = Quaternion.Euler(0f, 90f + i * 25f, 0f);
            }
        }
    }
}
