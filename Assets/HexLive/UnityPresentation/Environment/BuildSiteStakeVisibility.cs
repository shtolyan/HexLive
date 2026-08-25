#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Debug;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>Bug #229: one source of truth for when intent stakes yield to
    /// the first real construction geometry.</summary>
    internal static class BuildSiteStakeVisibility
    {
        internal static bool HasElementProgress(ObjectSnapshot elementObject)
        {
            if (elementObject.ArchitectureElements.Count == 0) return false;
            var element = elementObject.ArchitectureElements[0];
            return element.DeliveredTotal > 0 || element.WorkDone > 0;
        }

        internal static bool HasPhysicalProgress(
            ObjectSnapshot site, IReadOnlyList<ObjectSnapshot>? modules)
        {
            var delivered = site.DeliveredLogs + site.DeliveredSticks + site.DeliveredRope +
                            site.DeliveredLeaves + site.DeliveredStones + site.DeliveredBoards;
            if (delivered > 0) return true;
            if (modules == null) return false;

            foreach (var module in modules)
            {
                if (HasElementProgress(module)) return true;
            }

            return false;
        }
    }
}
