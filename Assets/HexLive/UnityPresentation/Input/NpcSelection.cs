using System;
using System.Collections.Generic;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// §123: shared ordered NPC selection. Selection and camera attachment are
    /// deliberately separate: programmatic set changes request one frame, while
    /// user activation (a click on a character) requests follow immediately.
    /// Follow is released by Escape or by panning (WASD/стрелки), not by a
    /// second click.
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
        /// User activation of one portrait/actor: the camera focuses on the
        /// subject and follows it at once. Re-activating the same singleton
        /// re-frames and keeps following; Escape/панорама release the camera.
        /// </summary>
        public static void Activate(int npcId)
        {
            Replace(npcId);
            CameraRequested?.Invoke(CameraRequest.Follow);
        }

        /// <summary>Legacy/programmatic exclusive selection with frame.</summary>
        public static void Select(int npcId) => Activate(npcId);

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
            if (!SameSelection(replacement))
            {
                SetSelection(replacement);
            }

            if (replacement.Count > 0)
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
