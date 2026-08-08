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
        private string _bedProduct = string.Empty;
        private GameObject? _bedRoot;
        private BedAssembly? _bedAssembly;

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

            // Spec §54.2/§35.5B: a BED or RACK site is the SAME assembled prefab
            // as the finished piece with only its delivered pieces toggled on —
            // each hauled leaf/stick/log/rope lights up one more piece, so the
            // build grows exactly into the finished object. One prefab, no slot
            // table to keep in sync (see BedAssembly).
            if (BedAssembly.IsAssembled(site.BuildProduct))
            {
                RefreshBed(site);
                return;
            }

            ClearChildren();
            var placed = 0;
            placed = Pile("resource.stone", site.DeliveredStones, placed);
            placed = Pile("resource.log", site.DeliveredLogs, placed);
            placed = Pile("resource.palm_leaf", site.DeliveredLeaves, placed);

            // Nothing hauled in yet → nothing to show. The intent stake is
            // retired (§54.14) — an empty site is a sim-side marker only.
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

        private void RefreshBed(ObjectSnapshot site)
        {
            EnsureBed(site.BuildProduct);
            _bedAssembly?.Apply(site.DeliveredLogs, site.DeliveredSticks, site.DeliveredRope,
                site.DeliveredLeaves, site.DeliveredStones, site.DeliveredBoards);
        }

        private void EnsureBed(string product)
        {
            if (_bedRoot != null && _bedProduct == product)
            {
                return;
            }

            ClearChildren();
            _bedProduct = product;
            _bedRoot = BedAssembly.BuildPartial(product, 0, 0, 0, 0, 0, 0);
            if (_bedRoot == null)
            {
                return;
            }

            _bedRoot.transform.SetParent(transform, false); // absolute-sized (1:1)
            _bedAssembly = _bedRoot.GetComponent<BedAssembly>();
        }

        private void ClearChildren()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(transform.GetChild(i).gameObject);
            }

            _bedProduct = string.Empty;
            _bedRoot = null;
            _bedAssembly = null;
        }

        private static int Signature(ObjectSnapshot site)
        {
            unchecked
            {
                var signature = site.BuildProduct.GetHashCode();
                signature = signature * 31 + site.DeliveredStones;
                signature = signature * 31 + site.DeliveredLogs;
                signature = signature * 31 + site.DeliveredLeaves;
                signature = signature * 31 + site.DeliveredSticks;
                signature = signature * 31 + site.DeliveredRope;
                signature = signature * 31 + site.DeliveredBoards;
                return signature;
            }
        }
    }
}
