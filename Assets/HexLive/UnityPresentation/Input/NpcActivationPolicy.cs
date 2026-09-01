using System.Collections.Generic;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// Bug #348: one authoritative interpretation of a character activation.
    /// A changed selection is selection-only; an exact repeat focuses it.
    /// </summary>
    public static class NpcActivationPolicy
    {
        public static bool ShouldFocus(
            IReadOnlyList<int> currentSelection,
            IReadOnlyList<int> requestedSelection)
        {
            if (currentSelection == null || requestedSelection == null ||
                requestedSelection.Count == 0 ||
                currentSelection.Count != requestedSelection.Count)
            {
                return false;
            }

            for (var i = 0; i < currentSelection.Count; i++)
            {
                if (currentSelection[i] != requestedSelection[i])
                {
                    return false;
                }
            }

            return true;
        }

        public static bool ShouldFocus(IReadOnlyList<int> currentSelection, int npcId)
        {
            return npcId >= 0 && currentSelection != null &&
                currentSelection.Count == 1 && currentSelection[0] == npcId;
        }
    }
}
