using System;
using System.Collections.Generic;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// §123: shared ordered NPC selection. Selection and camera attachment are
    /// deliberately separate. A user click on a changed subject only selects;
    /// an exact repeat of the same singleton/set requests follow. Programmatic
    /// selection never moves the camera unless it explicitly requests a frame.
    /// </summary>
    public static class NpcSelection
    {
        public enum CameraRequest
        {
            Frame,
            Follow
        }

        public static event Action<IReadOnlyList<int>> SelectionChanged;
        public static event Action<CameraRequest> CameraRequested;

        private static readonly List<int> Selected = new();

        /// <summary>True while the pointer is over any character UI.</summary>
        public static bool PointerOverUi { get; set; }

        /// <summary>Visible bottom-bar fraction used by camera framing.</summary>
        public static float BottomUiCoverage { get; set; }

        /// <summary>Visible right-roster fraction used by camera framing.</summary>
        public static float RightUiCoverage { get; set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Selected.Clear();
            PointerOverUi = false;
            BottomUiCoverage = 0f;
            RightUiCoverage = 0f;
            SelectionChanged = null;
            CameraRequested = null;
        }

        public static bool HasSelection => Selected.Count > 0;
        public static int Count => Selected.Count;
        public static int PrimaryId => Selected.Count > 0 ? Selected[Selected.Count - 1] : -1;

        // Compatibility for small test-scene bootstraps. New code should use
        // PrimaryId only when it has explicitly decided how multi-selection is
        // represented.
        public static int SelectedId => PrimaryId;

        public static IReadOnlyList<int> SelectedIds => Selected;

        public static bool Contains(int npcId) => Selected.Contains(npcId);

        /// <summary>
        /// User activation of one portrait/actor. A changed singleton only
        /// selects it; activating that exact singleton again focuses/follows.
        /// </summary>
        public static void Activate(int npcId)
        {
            var focus = NpcActivationPolicy.ShouldFocus(Selected, npcId);
            Replace(npcId);
            if (focus) CameraRequested?.Invoke(CameraRequest.Follow);
        }

        /// <summary>Programmatic exclusive selection without camera movement.</summary>
        public static void Select(int npcId) => Replace(npcId);

        public static void Replace(int npcId, bool requestFrame = false)
        {
            if (Selected.Count == 1 && Selected[0] == npcId)
            {
                if (requestFrame) CameraRequested?.Invoke(CameraRequest.Frame);
                return;
            }

            Selected.Clear();
            if (npcId >= 0) Selected.Add(npcId);
            PublishSelection();
            if (requestFrame) CameraRequested?.Invoke(CameraRequest.Frame);
        }

        public static void ActivateMany(IEnumerable<int> npcIds)
        {
            var replacement = UniqueOrdered(npcIds);
            var focus = NpcActivationPolicy.ShouldFocus(Selected, replacement);
            if (!SameSelection(replacement))
            {
                SetSelection(replacement);
            }

            if (focus)
            {
                CameraRequested?.Invoke(CameraRequest.Follow);
            }
        }

        public static void ReplaceMany(IEnumerable<int> npcIds, bool requestFrame = true)
        {
            var replacement = UniqueOrdered(npcIds);
            if (!SameSelection(replacement))
            {
                SetSelection(replacement);
            }

            if (requestFrame && replacement.Count > 0)
            {
                CameraRequested?.Invoke(CameraRequest.Frame);
            }
        }

        public static void AddMany(IEnumerable<int> npcIds, bool requestFrame = true)
        {
            var changed = false;
            foreach (var id in npcIds)
            {
                if (id >= 0 && !Selected.Contains(id))
                {
                    Selected.Add(id);
                    changed = true;
                }
            }

            if (changed) PublishSelection();
            if (requestFrame && Selected.Count > 0)
            {
                CameraRequested?.Invoke(CameraRequest.Frame);
            }
        }

        public static void Toggle(int npcId, bool requestFrame = true)
        {
            var index = Selected.IndexOf(npcId);
            if (index >= 0)
            {
                Selected.RemoveAt(index);
            }
            else if (npcId >= 0)
            {
                Selected.Add(npcId);
            }

            PublishSelection();
            if (requestFrame && Selected.Count > 0)
            {
                CameraRequested?.Invoke(CameraRequest.Frame);
            }
        }

        public static void RequestFrame()
        {
            if (Selected.Count > 0) CameraRequested?.Invoke(CameraRequest.Frame);
        }

        public static void Clear()
        {
            if (Selected.Count == 0) return;
            Selected.Clear();
            PublishSelection();
        }

        private static List<int> UniqueOrdered(IEnumerable<int> ids)
        {
            var result = new List<int>();
            if (ids == null) return result;
            foreach (var id in ids)
            {
                if (id >= 0 && !result.Contains(id)) result.Add(id);
            }
            return result;
        }

        private static bool SameSelection(IReadOnlyList<int> other)
        {
            if (other.Count != Selected.Count) return false;
            for (var i = 0; i < other.Count; i++)
            {
                if (other[i] != Selected[i]) return false;
            }
            return true;
        }

        private static void SetSelection(List<int> replacement)
        {
            Selected.Clear();
            Selected.AddRange(replacement);
            PublishSelection();
        }

        private static void PublishSelection()
        {
            // Subscribers receive an immutable-in-practice point-in-time copy;
            // a later Toggle cannot mutate the event payload under their feet.
            SelectionChanged?.Invoke(Selected.ToArray());
        }
    }
}
