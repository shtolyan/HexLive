#nullable enable
using UnityEngine;
using HexLive.Simulation.Debug;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54: renders a build-site as its DELIVERED materials piled up, so a
    /// piece visibly assembles from the stones/logs/leaves the colony hauls in
    /// (Stranded-Deep style — you see the components before the finished piece).
    /// The pile is rebuilt only when the delivered count changes, so it's cheap
    /// to poke every frame. When the site is stocked and raised, the sim swaps it
    /// for the finished object and this view is destroyed by the render diff.
    /// </summary>
    public sealed class BuildSitePile : MonoBehaviour
    {
        private int _signature = -1;

        // Cheap per-frame check: only rebuild when the delivered mix changed.
        public void Refresh(ObjectSnapshot site)
        {
            if (Signature(site) != _signature)
            {
                Rebuild(site);
            }
        }

        public void Rebuild(ObjectSnapshot site)
        {
            _signature = Signature(site);
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(transform.GetChild(i).gameObject);
            }

            var placed = 0;
            placed = Pile("resource.stone", site.DeliveredStones, placed);
            placed = Pile("resource.log", site.DeliveredLogs, placed);
            placed = Pile("resource.palm_leaf", site.DeliveredLeaves, placed);

            // Nothing hauled in yet → a small stake marks the intent point.
            if (placed == 0)
            {
                AddStake();
            }
        }

        private int Pile(string definitionId, int count, int placed)
        {
            for (var i = 0; i < count; i++)
            {
                var piece = LowPolyToolFactory.Build(definitionId);
                if (piece == null)
                {
                    continue;
                }

                piece.name = "Delivered " + definitionId;
                piece.transform.SetParent(transform, false);
                // Golden-angle scatter so the growing pile clusters naturally.
                var angle = placed * 2.399963f;
                var radius = 0.10f + 0.05f * placed;
                piece.transform.localScale = Vector3.one * 0.5f;
                piece.transform.localPosition = new Vector3(
                    Mathf.Cos(angle) * radius, 0.015f * placed, Mathf.Sin(angle) * radius);
                piece.transform.localRotation = Quaternion.Euler(0f, placed * 55f, 0f);
                placed++;
            }

            return placed;
        }

        private void AddStake()
        {
            var stake = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stake.name = "SiteStake";
            stake.transform.SetParent(transform, false);
            stake.transform.localScale = new Vector3(0.06f, 0.4f, 0.06f);
            stake.transform.localPosition = new Vector3(0f, 0.2f, 0f);
            var r = stake.GetComponent<MeshRenderer>();
            if (r != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.SetColor("_BaseColor", new Color(0.72f, 0.58f, 0.30f));
                mat.SetFloat("_Smoothness", 0.1f);
                r.sharedMaterial = mat;
            }
        }

        private static int Signature(ObjectSnapshot site) =>
            site.DeliveredStones * 10000 + site.DeliveredLogs * 100 + site.DeliveredLeaves;
    }
}
