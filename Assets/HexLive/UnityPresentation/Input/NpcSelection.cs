using System;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// Shared "who is selected" state. The camera writes it (click to select,
    /// Escape to clear) and the character panel / portrait stage read it, so
    /// selection is decoupled from any single view.
    /// </summary>
    public static class NpcSelection
    {
        public static event Action<int> SelectionChanged;

        /// <summary>
        /// True while the pointer is over the character bar, so world-picking
        /// (camera) ignores clicks that land on the UI.
        /// </summary>
        public static bool PointerOverUi { get; set; }

        private static int _selectedId = -1;

        public static bool HasSelection => _selectedId >= 0;

        public static int SelectedId => _selectedId;

        public static void Select(int npcId)
        {
            if (_selectedId == npcId)
            {
                return;
            }

            _selectedId = npcId;
            SelectionChanged?.Invoke(_selectedId);
        }

        public static void Clear()
        {
            if (_selectedId < 0)
            {
                return;
            }

            _selectedId = -1;
            SelectionChanged?.Invoke(_selectedId);
        }
    }
}
